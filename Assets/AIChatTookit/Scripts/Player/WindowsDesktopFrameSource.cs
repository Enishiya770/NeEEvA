using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace NeEEvA.Player
{
    /// <summary>
    /// Local Windows monitor capture. GDI or TV-local filtering runs on a background thread;
    /// the consumer uploads the latest frame to a TextureFormat.BGRA32 texture.
    /// This class never calls Unity APIs and never encodes or transmits screen contents.
    /// </summary>
    public sealed class WindowsDesktopFrameSource : IDisposable
    {
        private static readonly object sourcesGate = new object();
        private static readonly HashSet<WindowsDesktopFrameSource> activeSources = new HashSet<WindowsDesktopFrameSource>();
        private readonly object gate = new object();
        private readonly int monitorIndex, maxWidth, maxHeight, framesPerSecond;
        private readonly bool excludeUnityWindows;
        private readonly Thread worker = null;
        private readonly Stack<FrameBuffer> available = new Stack<FrameBuffer>(3);
        private FrameBuffer pending, leased;
        private bool disposed;
        private string lastError;
        private string exclusionStatus;
        private int excludedWindowCount;

        public static bool IsSupported
        {
            get
            {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>Null after a successful capture; failures are retried once per second.</summary>
        public string LastError { get { lock (gate) return lastError; } }
        public string ExclusionStatus { get { lock (gate) return exclusionStatus; } }
        public int ExcludedWindowCount { get { lock (gate) return excludedWindowCount; } }
        public bool IsDisposed { get { lock (gate) return disposed; } }

        /// <summary>Stop native callbacks before Unity unloads the scripting domain.</summary>
        public static string StopAll()
        {
            WindowsDesktopFrameSource[] sources;
            lock (sourcesGate) { sources = new WindowsDesktopFrameSource[activeSources.Count]; activeSources.CopyTo(sources); }
            foreach (var source in sources) source.Dispose();
            foreach (var source in sources)
                if (source.worker != null && source.worker != Thread.CurrentThread && !source.worker.Join(3000))
                    return "A desktop capture worker did not finish shutting down in time.";
            return null;
        }

        /// <param name="monitorIndex">0 is the primary monitor; others are sorted left-to-right, then top-to-bottom.</param>
        /// <param name="maxWidth">Maximum capture width, preserving aspect ratio without upscaling.</param>
        /// <param name="maxHeight">Maximum capture height, preserving aspect ratio without upscaling.</param>
        /// <param name="framesPerSecond">Capture limit, clamped to 1–30. Texture upload remains on the consumer thread.</param>
        /// <param name="excludeUnityWindows">Filter only this capture; never change global window display affinity.</param>
        public WindowsDesktopFrameSource(int monitorIndex = 0, int maxWidth = 1280,
            int maxHeight = 720, int framesPerSecond = 12, bool excludeUnityWindows = false)
        {
            this.excludeUnityWindows = excludeUnityWindows;
            exclusionStatus = excludeUnityWindows ? "Starting TV-only window filtering..." : "Unity is included in this TV's capture.";
            this.monitorIndex = Math.Max(0, monitorIndex);
            this.maxWidth = Math.Max(16, Math.Min(4096, maxWidth));
            this.maxHeight = Math.Max(16, Math.Min(4096, maxHeight));
            this.framesPerSecond = Math.Max(1, Math.Min(30, framesPerSecond));
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            worker = new Thread(CaptureLoop) { IsBackground = true, Name = "TV desktop capture" };
            lock (sourcesGate) activeSources.Add(this);
            try { worker.Start(); }
            catch { lock (sourcesGate) activeSources.Remove(this); throw; }
#else
            lastError = "Local desktop capture requires Windows. Standalone headsets require a PC stream.";
#endif
        }

        /// <summary>
        /// Takes the most recent frame without waiting. Rows start at the bottom-left;
        /// bytes are BGRA with alpha 255. The array remains stable until ReleaseFrame.
        /// There can be one outstanding lease. Always release it in a finally block.
        /// </summary>
        public bool TryConsumeFrame(out byte[] bgraPixels, out int width, out int height)
        {
            lock (gate)
            {
                bgraPixels = null;
                width = height = 0;
                if (disposed || leased != null || pending == null) return false;
                leased = pending;
                pending = null;
                bgraPixels = leased.Pixels;
                width = leased.Width;
                height = leased.Height;
                return true;
            }
        }

        /// <summary>Returns a consumed buffer after upload; do not retain or read it afterwards.</summary>
        public void ReleaseFrame(byte[] bgraPixels)
        {
            lock (gate)
            {
                if (leased == null || !ReferenceEquals(leased.Pixels, bgraPixels)) return;
                if (!disposed) available.Push(leased);
                leased = null;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                pending = null;
                available.Clear();
                Monitor.PulseAll(gate);
            }
            // A driver call cannot safely be interrupted. Bound the main-thread wait;
            // the background thread retains ownership and frees GDI handles on exit.
            if (worker != null && worker != Thread.CurrentThread) worker.Join(250);
        }

        private sealed class FrameBuffer
        {
            public readonly int Width, Height;
            public readonly byte[] Pixels;
            public FrameBuffer(int width, int height)
            {
                Width = width;
                Height = height;
                Pixels = new byte[checked(width * height * 4)];
            }
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private bool WaitForStop(int milliseconds)
        {
            lock (gate)
            {
                if (!disposed && milliseconds > 0) Monitor.Wait(gate, milliseconds);
                return disposed;
            }
        }

        private void CaptureLoop()
        {
            IMonitorCapture capture = null;
            IntPtr previousDpiContext = IntPtr.Zero;
            try
            {
                // Keep monitor rectangles and GDI coordinates in physical pixels even
                // if an editor/player window is on a display with a different DPI.
                try { previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4)); }
                catch (EntryPointNotFoundException) { /* Older Windows uses process DPI awareness. */ }
                var clock = Stopwatch.StartNew();
                long refreshMonitorAt = 0;
                while (!WaitForStop(0))
                {
                    long startedAt = clock.ElapsedMilliseconds;
                    try
                    {
                        if (capture == null || startedAt >= refreshMonitorAt)
                        {
                            Rect rect = SelectMonitor(monitorIndex);
                            if (capture == null || !capture.Matches(rect))
                            {
                                if (capture != null) capture.Dispose();
                                capture = null;
                                capture = excludeUnityWindows
                                    ? (IMonitorCapture)new FilteredCapture(rect, maxWidth, maxHeight)
                                    : new NativeCapture(rect, maxWidth, maxHeight);
                            }
                            refreshMonitorAt = startedAt + 2000;
                        }

                        FrameBuffer frame;
                        lock (gate)
                        {
                            if (disposed) break;
                            frame = available.Count > 0 ? available.Pop() : null;
                        }
                        if (frame == null || frame.Width != capture.Width || frame.Height != capture.Height)
                            frame = new FrameBuffer(capture.Width, capture.Height);
                        capture.CopyTo(frame.Pixels);
                        lock (gate)
                        {
                            if (disposed) break;
                            // Drop obsolete frames rather than queueing latency. Together
                            // with the outstanding lease and worker this reuses three buffers.
                            if (pending != null) available.Push(pending);
                            pending = frame;
                            lastError = null;
                            excludedWindowCount = capture.ExcludedWindowCount;
                            exclusionStatus = capture.Diagnostic;
                        }
                    }
                    catch (Exception error)
                    {
                        if (capture != null) capture.Dispose();
                        capture = null;
                        lock (gate)
                        {
                            lastError = error.Message;
                            exclusionStatus = error.Message;
                            excludedWindowCount = 0;
                            pending = null;
                        }
                        if (WaitForStop(1000)) break;
                        continue;
                    }
                    int delay = Math.Max(1, (int)Math.Ceiling(1000.0 / framesPerSecond -
                        (clock.ElapsedMilliseconds - startedAt)));
                    if (WaitForStop(delay)) break;
                }
            }
            catch (Exception error)
            {
                lock (gate) lastError = error.Message;
            }
            finally
            {
                try
                {
                    if (capture != null) capture.Dispose();
                    if (previousDpiContext != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpiContext);
                }
                finally { lock (sourcesGate) activeSources.Remove(this); }
            }
        }

        private interface IMonitorCapture : IDisposable
        {
            int Width { get; }
            int Height { get; }
            int ExcludedWindowCount { get; }
            string Diagnostic { get; }
            bool Matches(Rect other);
            void CopyTo(byte[] pixels);
        }

        private sealed class FilteredCapture : IMonitorCapture
        {
            private readonly Rect rect;
            private WindowsFilteredDesktopCapture capture;
            private bool disposed;
            public int Width => capture.Width;
            public int Height => capture.Height;
            public int ExcludedWindowCount => capture.ExcludedWindowCount;
            public string Diagnostic => capture.Diagnostic;
            public FilteredCapture(Rect rect, int maxWidth, int maxHeight)
            {
                this.rect = rect;
                FilteredDispatcher.Invoke(() =>
                {
                    capture = new WindowsFilteredDesktopCapture(rect.Left, rect.Top, rect.Right - rect.Left,
                        rect.Bottom - rect.Top, maxWidth, maxHeight);
                    FilteredDispatcher.AddClient();
                });
            }
            public bool Matches(Rect other) => rect.Left == other.Left && rect.Top == other.Top &&
                rect.Right == other.Right && rect.Bottom == other.Bottom;
            public void CopyTo(byte[] pixels) => FilteredDispatcher.Invoke(() => capture.CopyTo(pixels));
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                FilteredDispatcher.Invoke(() =>
                {
                    try { capture.Dispose(); }
                    finally { FilteredDispatcher.RemoveClient(); }
                });
            }
        }

        // Magnification permits multiple native controls only on the same owner thread.
        // Each TV keeps its own control, filter and output size; only native work is serialized.
        private static class FilteredDispatcher
        {
            private static readonly object queueGate = new object();
            private static readonly Queue<Job> jobs = new Queue<Job>();
            private static Thread owner;
            private static int clients;
            private sealed class Job
            {
                public Action Action;
                public Exception Error;
                public bool Done;
            }

            public static void AddClient() { clients++; }
            public static void RemoveClient() { clients--; }

            public static void Invoke(Action action)
            {
                var job = new Job { Action = action };
                lock (queueGate)
                {
                    jobs.Enqueue(job);
                    if (owner == null)
                    {
                        owner = new Thread(Run) { IsBackground = true, Name = "TV private capture compositor" };
                        try { owner.Start(); }
                        catch { owner = null; jobs.Dequeue(); throw; }
                    }
                    Monitor.PulseAll(queueGate);
                }
                lock (job) while (!job.Done) Monitor.Wait(job);
                if (job.Error != null) throw new InvalidOperationException(job.Error.Message, job.Error);
            }

            private static void Run()
            {
                IntPtr dpi = IntPtr.Zero;
                try
                {
                    try { dpi = SetThreadDpiAwarenessContext(new IntPtr(-4)); }
                    catch (EntryPointNotFoundException) { }
                    while (true)
                    {
                        Job job;
                        lock (queueGate)
                        {
                            while (jobs.Count == 0)
                            {
                                if (clients == 0)
                                {
                                    // Keep shutdown inside the lock so a new owner cannot overlap it.
                                    if (dpi != IntPtr.Zero) SetThreadDpiAwarenessContext(dpi);
                                    dpi = IntPtr.Zero;
                                    owner = null;
                                    return;
                                }
                                Monitor.Wait(queueGate);
                            }
                            job = jobs.Dequeue();
                        }
                        try { job.Action(); }
                        catch (Exception error) { job.Error = error; }
                        finally { lock (job) { job.Done = true; Monitor.PulseAll(job); } }
                    }
                }
                finally { if (dpi != IntPtr.Zero) SetThreadDpiAwarenessContext(dpi); }
            }
        }

        private sealed class NativeCapture : IMonitorCapture
        {
            public int Width { get; }
            public int Height { get; }
            public int ExcludedWindowCount => 0;
            public string Diagnostic => "Unity is included in this TV's capture. Other capture tools are unaffected.";
            private readonly Rect rect;
            private IntPtr screenDc, memoryDc, bitmap, previousBitmap, bits;

            public NativeCapture(Rect rect, int maxWidth, int maxHeight)
            {
                this.rect = rect;
                int sourceWidth = rect.Right - rect.Left, sourceHeight = rect.Bottom - rect.Top;
                double scale = Math.Min(1.0, Math.Min((double)maxWidth / sourceWidth, (double)maxHeight / sourceHeight));
                Width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
                Height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
                try
                {
                    screenDc = GetDC(IntPtr.Zero);
                    if (screenDc == IntPtr.Zero) throw NativeError("GetDC");
                    memoryDc = CreateCompatibleDC(screenDc);
                    if (memoryDc == IntPtr.Zero) throw NativeError("CreateCompatibleDC");
                    var info = new BitmapInfo();
                    info.Header.Size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader));
                    info.Header.Width = Width;
                    info.Header.Height = Height; // Positive height: bottom-up, matching Unity texture rows.
                    info.Header.Planes = 1;
                    info.Header.BitCount = 32;
                    bitmap = CreateDIBSection(screenDc, ref info, 0, out bits, IntPtr.Zero, 0);
                    if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw NativeError("CreateDIBSection");
                    previousBitmap = SelectObject(memoryDc, bitmap);
                    if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1))
                        throw NativeError("SelectObject");
                    SetStretchBltMode(memoryDc, 4); // HALFTONE
                    SetBrushOrgEx(memoryDc, 0, 0, IntPtr.Zero);
                }
                catch { Dispose(); throw; }
            }

            public bool Matches(Rect other)
            {
                return rect.Left == other.Left && rect.Top == other.Top &&
                    rect.Right == other.Right && rect.Bottom == other.Bottom;
            }

            public void CopyTo(byte[] pixels)
            {
                const uint SourceCopyWithLayeredWindows = 0x00CC0020u | 0x40000000u;
                if (!StretchBlt(memoryDc, 0, 0, Width, Height, screenDc, rect.Left, rect.Top,
                    rect.Right - rect.Left, rect.Bottom - rect.Top, SourceCopyWithLayeredWindows))
                    throw NativeError("StretchBlt");
                // CreateDIBSection requires flushing GDI before reading its memory.
                if (!GdiFlush()) throw NativeError("GdiFlush");
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                // BI_RGB's fourth byte is undefined; make every pixel explicitly opaque.
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            }

            public void Dispose()
            {
                if (memoryDc != IntPtr.Zero && previousBitmap != IntPtr.Zero && previousBitmap != new IntPtr(-1))
                    SelectObject(memoryDc, previousBitmap);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
                // GetDC/ReleaseDC must run on the same thread.
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
                screenDc = memoryDc = bitmap = previousBitmap = bits = IntPtr.Zero;
            }
        }

        private static Exception NativeError(string operation)
        {
            return new InvalidOperationException("Desktop capture: " + operation +
                " failed (Windows error " + Marshal.GetLastWin32Error() + ").");
        }

        private static Rect SelectMonitor(int index)
        {
            var monitors = new List<MonitorInfo>();
            GCHandle handle = GCHandle.Alloc(monitors);
            try
            {
                if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, monitorCallback, GCHandle.ToIntPtr(handle)))
                    throw NativeError("EnumDisplayMonitors");
            }
            finally { handle.Free(); }
            monitors.Sort((a, b) =>
            {
                int primary = (b.Flags & 1).CompareTo(a.Flags & 1);
                if (primary != 0) return primary;
                int horizontal = a.Monitor.Left.CompareTo(b.Monitor.Left);
                return horizontal != 0 ? horizontal : a.Monitor.Top.CompareTo(b.Monitor.Top);
            });
            if (index >= monitors.Count)
                throw new InvalidOperationException("Desktop capture: monitor " + index + " is unavailable.");
            return monitors[index].Monitor;
        }

        private static readonly MonitorEnumProc monitorCallback = CollectMonitor;
#if ENABLE_IL2CPP
        [AOT.MonoPInvokeCallback(typeof(MonitorEnumProc))]
#endif
        private static bool CollectMonitor(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (GetMonitorInfo(monitor, ref info) && info.Monitor.Right > info.Monitor.Left &&
                info.Monitor.Bottom > info.Monitor.Top)
                ((List<MonitorInfo>)GCHandle.FromIntPtr(data).Target).Add(info);
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, SizeImage;
            public int XPelsPerMeter, YPelsPerMeter;
            public uint ColorsUsed, ColorsImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo { public BitmapInfoHeader Header; public uint FirstColor; }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage,
            out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")]
        private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool GdiFlush();
#endif
    }
}
