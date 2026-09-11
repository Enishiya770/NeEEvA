using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace NeEEvA.Player
{
    /// <summary>
    /// A private desktop capture excluding this process's windows from this capture only.
    /// Create, capture and dispose on the shared filtered-capture background thread. Uses a hidden magnifier
    /// control, never changes target windows or global screen/capture/input settings.
    /// The legacy callback is limited to tested Windows x64 single-monitor systems.
    /// </summary>
    public sealed class WindowsFilteredDesktopCapture : IDisposable
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int ExcludedWindowCount { get; private set; }
        public string Diagnostic { get; private set; }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private static readonly object runtimeGate = new object();
        private static int runtimeUsers, runtimeOwnerThread;
        private static readonly ScaleCallback imageCallback = OnImage;
        private static readonly EnumWindowsCallback enumCallback = OnWindow;
        private static readonly Guid callbackFormat = new Guid("f5c7ad2d-6a8d-43dd-a7a8-a29935261ae9");
        [ThreadStatic] private static WindowsFilteredDesktopCapture activeCapture;
        private static int nativeCallbackCount;
        private static int lastCallbackThread;
        private readonly int ownerThread;
        private readonly uint processId;
        private readonly Rect source;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<IntPtr> windows = new List<IntPtr>();
        private IntPtr[] filteredWindows;
        private IntPtr host, magnifier, screenDc, memoryDc, bitmap, oldBitmap, bits;
        private bool runtimeAcquired, disposed, callbackReceived, needsWarmup = true;
        private long refreshAt;
        private byte[] destination;
        private Exception callbackError;
        private GCHandle enumerationContext;
#endif

        /// <param name="left">Source rectangle in physical desktop pixels.</param>
        /// <param name="top">Source rectangle in physical desktop pixels.</param>
        public WindowsFilteredDesktopCapture(int left, int top, int sourceWidth, int sourceHeight,
            int maxWidth, int maxHeight)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            ownerThread = Thread.CurrentThread.ManagedThreadId;
            processId = GetCurrentProcessId();
            RequireSingleMonitor();
            if (sourceWidth <= 0 || sourceHeight <= 0 || maxWidth <= 0 || maxHeight <= 0)
                throw new ArgumentOutOfRangeException("Capture dimensions must be positive.");
            source = new Rect { Left = left, Top = top, Right = checked(left + sourceWidth), Bottom = checked(top + sourceHeight) };
            double scale = Math.Min(1.0, Math.Min((double)Math.Min(4096, maxWidth) / sourceWidth,
                (double)Math.Min(4096, maxHeight) / sourceHeight));
            Width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            Height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
            try
            {
                lock (runtimeGate)
                {
                    // Magnification's runtime belongs to its initial thread; all concurrent
                    // instances must share the filtered-capture dispatcher thread.
                    if (runtimeUsers > 0 && runtimeOwnerThread != ownerThread)
                        throw new InvalidOperationException("Concurrent TV-only capture instances must share one capture thread.");
                    if (runtimeUsers == 0)
                    {
                        if (!MagInitialize()) throw Failure("MagInitialize");
                        runtimeOwnerThread = ownerThread;
                    }
                    runtimeUsers++;
                    runtimeAcquired = true;
                }
                IntPtr module = GetModuleHandle(null);
                host = CreateWindow(0x00080000, "STATIC", "NeEEvA private desktop capture", 0,
                    0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
                if (host == IntPtr.Zero) throw Failure("Create hidden capture host");
                // WS_CHILD | WS_VISIBLE; its parent remains hidden for the entire lifetime.
                magnifier = CreateWindow(0, "Magnifier", "NeEEvA capture filter", 0x50000000,
                    left, top, sourceWidth, sourceHeight, host, IntPtr.Zero, module, IntPtr.Zero);
                if (magnifier == IntPtr.Zero) throw Failure("Create magnifier control");
                ShowWindow(host, 0);
                if (!MagSetImageScalingCallback(magnifier, imageCallback)) throw Failure("Set capture callback");

                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) throw Failure("Get desktop DC");
                memoryDc = CreateCompatibleDC(screenDc);
                if (memoryDc == IntPtr.Zero) throw Failure("Create output DC");
                var outputInfo = MakeBitmapInfo(Width, Height);
                bitmap = CreateDIBSection(screenDc, ref outputInfo, 0, out bits, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw Failure("Create output DIB");
                oldBitmap = SelectObject(memoryDc, bitmap);
                if (oldBitmap == IntPtr.Zero || oldBitmap == new IntPtr(-1)) throw Failure("Select output DIB");
                if (SetStretchBltMode(memoryDc, 4) == 0) throw Failure("Set output scaling mode");
                if (!SetBrushOrgEx(memoryDc, 0, 0, IntPtr.Zero)) throw Failure("Set output brush origin");
                enumerationContext = GCHandle.Alloc(this);
                RefreshFilter();
            }
            catch { Dispose(); throw; }
#else
            Diagnostic = "TV-only window filtering requires Windows x64 with one connected monitor.";
            throw new PlatformNotSupportedException(Diagnostic);
#endif
        }

        /// <summary>Writes bottom-up BGRA32 with alpha 255 to an exactly sized caller-owned buffer.</summary>
        public void CopyTo(byte[] bottomUpBgra)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            CheckThread();
            if (disposed) throw new ObjectDisposedException(nameof(WindowsFilteredDesktopCapture));
            // WebRTC disables this API on every multi-monitor configuration because it
            // can crash natively. Check every frame, including after display hot-plug.
            RequireSingleMonitor();
            if (bottomUpBgra == null || bottomUpBgra.Length != checked(Width * Height * 4))
                throw new ArgumentException("Destination must contain exactly Width * Height * 4 bytes.", nameof(bottomUpBgra));
            try
            {
                if (clock.ElapsedMilliseconds >= refreshAt) RefreshFilter();
                if (needsWarmup)
                {
                    // A changed filter can initially deliver an older compositor frame.
                    // Warm up only this private capture, away from Unity's main thread.
                    Capture(null);
                    Thread.Sleep(40);
                    RequireSingleMonitor();
                    Capture(null);
                    Thread.Sleep(40);
                    needsWarmup = false;
                }
                RequireSingleMonitor();
                Capture(bottomUpBgra);
                Diagnostic = "TV-only filtered desktop capture; " + ExcludedWindowCount +
                    " Unity window(s) omitted. Other capture applications are unaffected.";
            }
            catch (Exception error)
            {
                Diagnostic = error.Message;
                throw;
            }
#else
            throw new PlatformNotSupportedException(Diagnostic);
#endif
        }

        public void Dispose()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            CheckThread();
            if (disposed) return;
            disposed = true;
            destination = null;
            if (magnifier != IntPtr.Zero) MagSetImageScalingCallback(magnifier, null);
            // Native windows must die on their owning thread, before MagUninitialize.
            if (host != IntPtr.Zero && !DestroyWindow(host)) Diagnostic = Failure("Destroy capture host").Message;
            host = magnifier = IntPtr.Zero;
            if (enumerationContext.IsAllocated) enumerationContext.Free();
            if (oldBitmap != IntPtr.Zero && oldBitmap != new IntPtr(-1) && memoryDc != IntPtr.Zero)
                SelectObject(memoryDc, oldBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            oldBitmap = bitmap = memoryDc = screenDc = bits = IntPtr.Zero;
            if (runtimeAcquired)
            {
                lock (runtimeGate)
                {
                    if (--runtimeUsers == 0)
                    {
                        if (!MagUninitialize()) Diagnostic = Failure("MagUninitialize").Message;
                        runtimeOwnerThread = 0;
                    }
                    runtimeAcquired = false;
                }
            }
#endif
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Filtered capture must stay on the background thread that created it.");
        }

        private static void RequireSingleMonitor()
        {
            if (IntPtr.Size != 8)
                throw new PlatformNotSupportedException("TV-only desktop filtering requires a 64-bit Windows process; WOW64 is unsupported.");
            if (GetSystemMetrics(80) != 1)
                throw new PlatformNotSupportedException("TV-only desktop filtering requires exactly one connected monitor. Disable the filter to use ordinary multi-monitor capture.");
        }

        private void RefreshFilter()
        {
            windows.Clear();
            if (!EnumWindows(enumCallback, GCHandle.ToIntPtr(enumerationContext))) throw Failure("Enumerate Unity windows");
            windows.Sort((a, b) => a.ToInt64().CompareTo(b.ToInt64()));
            bool changed = filteredWindows == null || filteredWindows.Length != windows.Count;
            if (!changed)
                for (int i = 0; i < windows.Count; i++)
                    if (windows[i] != filteredWindows[i]) { changed = true; break; }
            if (changed)
            {
                IntPtr[] next = windows.ToArray();
                // MW_FILTERMODE_EXCLUDE = 0. This list belongs only to our magnifier HWND.
                if (!MagSetWindowFilterList(magnifier, 0, next.Length, next)) throw Failure("Set TV-only window filter");
                filteredWindows = next;
                ExcludedWindowCount = next.Length;
                needsWarmup = true;
            }
            refreshAt = clock.ElapsedMilliseconds + 500;
        }

        private void Capture(byte[] output)
        {
            int callbacksBefore = Volatile.Read(ref nativeCallbackCount);
            callbackReceived = false;
            callbackError = null;
            destination = output;
            activeCapture = this;
            try
            {
                // Magnifier needs the position notification even while its host is hidden.
                // Only our child control moves; no Unity or desktop application window does.
                if (!SetWindowPos(magnifier, IntPtr.Zero, source.Left, source.Top,
                    source.Right - source.Left, source.Bottom - source.Top, 0x14)) throw Failure("Size private capture control");
                if (!MagSetWindowSource(magnifier, source)) throw Failure("Capture filtered desktop");
            }
            finally { activeCapture = null; destination = null; }
            if (callbackError != null) throw callbackError;
            if (!callbackReceived)
                throw new InvalidOperationException("This Windows graphics driver did not deliver a filtered capture frame (native callbacks: " +
                    (Volatile.Read(ref nativeCallbackCount) - callbacksBefore) + ", callback thread: " + Volatile.Read(ref lastCallbackThread) +
                    ", capture thread: " + ownerThread + "). Disable TV-only filtering to use ordinary capture.");
        }

#if ENABLE_IL2CPP
        [AOT.MonoPInvokeCallback(typeof(ScaleCallback))]
#endif
        private static bool OnImage(IntPtr window, IntPtr sourcePixels, ImageHeader header,
            IntPtr targetPixels, ImageHeader targetHeader, Rect unclipped, Rect clipped, IntPtr dirty)
        {
            Interlocked.Increment(ref nativeCallbackCount);
            Volatile.Write(ref lastCallbackThread, Thread.CurrentThread.ManagedThreadId);
            // The callback's HWND can identify an internal native window instead of the
            // magnifier control. Like WebRTC, associate synchronous callbacks by thread.
            WindowsFilteredDesktopCapture owner = activeCapture;
            if (owner == null) return true;
            try
            {
                // Offset describes an image-file layout. srcdata already addresses pixels;
                // cbSize is their payload length (e.g. a 4K frame reports offset 52).
                if (sourcePixels == IntPtr.Zero || header.Width != (uint)(owner.source.Right - owner.source.Left) ||
                    header.Height != (uint)(owner.source.Bottom - owner.source.Top) ||
                    header.Format != callbackFormat || header.Stride != checked(header.Width * 4) ||
                    header.Size.ToUInt64() < (ulong)header.Stride * header.Height)
                    throw new InvalidOperationException("Unsupported filtered desktop pixel layout: " + header.Width + " x " +
                        header.Height + ", stride " + header.Stride + ", format " + header.Format +
                        ", offset " + header.Offset + ", bytes " + header.Size.ToUInt64() +
                        ", requested " + (owner.source.Right - owner.source.Left) + " x " +
                        (owner.source.Bottom - owner.source.Top) + ".");
                if (owner.destination != null)
                {
                    // The native callback reports the RGBA GUID above, but known red/green
                    // window tests and WebRTC's unchanged pixel copy confirm BGRA bytes on
                    // this backend. Its rows are top-down. Do not swap the red/blue bytes.
                    var info = MakeBitmapInfo((int)header.Width, -(int)header.Height);
                    int copied = StretchDIBits(owner.memoryDc, 0, 0, owner.Width, owner.Height,
                        0, 0, (int)header.Width, (int)header.Height, sourcePixels,
                        ref info, 0, 0x00CC0020);
                    if (copied == 0 || copied == -1) throw Failure("Scale filtered desktop frame");
                    if (!GdiFlush()) throw Failure("Flush filtered desktop frame");
                    Marshal.Copy(owner.bits, owner.destination, 0, owner.destination.Length);
                    for (int i = 3; i < owner.destination.Length; i += 4) owner.destination[i] = 255;
                }
                owner.callbackReceived = true;
            }
            catch (Exception error) { owner.callbackError = error; }
            return true; // Never let a managed exception cross the native callback boundary.
        }

#if ENABLE_IL2CPP
        [AOT.MonoPInvokeCallback(typeof(EnumWindowsCallback))]
#endif
        private static bool OnWindow(IntPtr window, IntPtr context)
        {
            var owner = (WindowsFilteredDesktopCapture)GCHandle.FromIntPtr(context).Target;
            uint process;
            Rect bounds;
            if (window != owner.host && IsWindowVisible(window) &&
                GetWindowThreadProcessId(window, out process) != 0 && process == owner.processId &&
                GetWindowRect(window, out bounds) && bounds.Right > owner.source.Left && bounds.Left < owner.source.Right &&
                bounds.Bottom > owner.source.Top && bounds.Top < owner.source.Bottom)
                owner.windows.Add(window);
            return true;
        }

        private static BitmapInfo MakeBitmapInfo(int width, int height)
        {
            return new BitmapInfo { Header = new BitmapHeader {
                Size = (uint)Marshal.SizeOf(typeof(BitmapHeader)), Width = width, Height = height, Planes = 1, BitsPerPixel = 32 } };
        }

        private static Exception Failure(string operation)
        {
            return new InvalidOperationException(operation + " failed (Windows error " + Marshal.GetLastWin32Error() + ").");
        }

        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct ImageHeader
        { public uint Width, Height; public Guid Format; public uint Stride, Offset; public UIntPtr Size; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
        {
            public uint Size; public int Width, Height; public ushort Planes, BitsPerPixel;
            public uint Compression, ImageSize; public int XPelsPerMeter, YPelsPerMeter; public uint Colors, ImportantColors;
        }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapHeader Header; public uint FirstColor; }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)] private delegate bool ScaleCallback(IntPtr window, IntPtr source, ImageHeader sourceHeader,
            IntPtr target, ImageHeader targetHeader, Rect unclipped, Rect clipped, IntPtr dirty);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)] private delegate bool EnumWindowsCallback(IntPtr window, IntPtr context);
        [DllImport("Magnification.dll", SetLastError = true)] private static extern bool MagInitialize();
        [DllImport("Magnification.dll", SetLastError = true)] private static extern bool MagUninitialize();
        [DllImport("Magnification.dll", SetLastError = true)] private static extern bool MagSetImageScalingCallback(IntPtr window, ScaleCallback callback);
        [DllImport("Magnification.dll", SetLastError = true)] private static extern bool MagSetWindowFilterList(IntPtr window, uint mode, int count, IntPtr[] windows);
        [DllImport("Magnification.dll", SetLastError = true)] private static extern bool MagSetWindowSource(IntPtr window, Rect source);
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindow(uint extended, string className, string title, uint style, int x, int y,
            int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after,
            int x, int y, int width, int height, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr context);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info,
            uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern int StretchDIBits(IntPtr dc, int x, int y, int width,
            int height, int sourceX, int sourceY, int sourceWidth, int sourceHeight, IntPtr source, ref BitmapInfo info, uint usage, uint operation);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern bool GdiFlush();
#endif
    }
}
