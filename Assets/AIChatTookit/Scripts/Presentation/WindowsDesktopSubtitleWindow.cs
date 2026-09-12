using System;
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
#endif

namespace NeEEvA.Presentation
{
    /// <summary>
    /// A separate Windows subtitle surface. Use and dispose on Unity's main thread.
    /// Its system window procedure has no managed callback and does not survive its owner.
    /// </summary>
    public sealed class WindowsDesktopSubtitleWindow : IDesktopSubtitleWindow
    {
        public WindowsDesktopSubtitleWindow(bool recognition = false)
        {
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
            this.recognition = recognition;
#endif
        }

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
        private readonly bool recognition;
        private readonly uint processId = Native.GetCurrentProcessId();
        private readonly int threadId = Thread.CurrentThread.ManagedThreadId;
        private IntPtr handle, unityWindow, memoryDc, bitmap, previousBitmap, bits, font, speakerFont;
        private int surfaceWidth, surfaceHeight, fontPixels, speakerFontPixels;
        private byte[] pixels;
        private string lastOriginal, lastTranslation, lastSpeaker;
        private float lastScale;
        private int lastX, lastY, lastWidth, lastFontSize, lastAlpha = -1;
        private bool visible, disposed;

        public static bool IsSupported { get { return true; } }
        public IntPtr Handle { get { return handle; } }

        public bool IsUnityInBackground
        {
            get
            {
                IntPtr foreground = Native.GetForegroundWindow();
                if (foreground == IntPtr.Zero) return false;
                uint foregroundProcess;
                Native.GetWindowThreadProcessId(foreground, out foregroundProcess);
                if (foregroundProcess == processId)
                {
                    if (foreground != handle) unityWindow = Native.GetAncestor(foreground, 2);
                    return false;
                }
                return foregroundProcess != 0;
            }
        }

        /// <summary>Shows the supplied current subtitle page; empty text or zero opacity hides it.</summary>
        public void Present(string original, string translation, int fontSize, float opacity, string speaker = "")
        {
            VerifyThread();
            if (disposed) throw new ObjectDisposedException(nameof(WindowsDesktopSubtitleWindow));
            original = Normalize(original);
            translation = Normalize(translation);
            speaker = Normalize(speaker).Replace('\n', ' ');
            if (recognition)
            {
                original = original.Replace('\n', ' ');
                translation = string.Empty;
            }
            int alpha = float.IsNaN(opacity) ? 0 : (int)Math.Round(Math.Max(0, Math.Min(1, opacity)) * 255);
            if ((original.Length == 0 && translation.Length == 0) || alpha == 0)
            {
                Hide();
                return;
            }

            Native.RECT work;
            float scale;
            GetDesktopLayout(out work, out scale);
            int width = Math.Max(1, Math.Min(Scale(recognition ? 468 : 960, scale),
                (int)((work.right - work.left) * (recognition ? .35 : .8))));
            int pixelFont = Scale(Math.Max(16, Math.Min(recognition ? 22 : 48, fontSize)), scale);
            bool redraw = original != lastOriginal || translation != lastTranslation || speaker != lastSpeaker ||
                width != lastWidth || pixelFont != lastFontSize || scale != lastScale;
            EnsureWindow();
            if (redraw) Render(original, translation, width, pixelFont, scale, speaker);

            int x = work.left + (recognition ? Scale(32, scale) : ((work.right - work.left) - surfaceWidth) / 2);
            int y = Math.Max(work.top, work.bottom - surfaceHeight - Scale(recognition ? 48 : 130, scale));
            if (redraw || alpha != lastAlpha || x != lastX || y != lastY)
            {
                Native.POINT destination = new Native.POINT(x, y), source = new Native.POINT(0, 0);
                Native.SIZE size = new Native.SIZE(surfaceWidth, surfaceHeight);
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION { SourceConstantAlpha = (byte)alpha, AlphaFormat = 1 };
                Check(Native.UpdateLayeredWindow(handle, IntPtr.Zero, ref destination, ref size, memoryDc, ref source, 0, ref blend, 2), "UpdateLayeredWindow");
                lastOriginal = original;
                lastTranslation = translation;
                lastSpeaker = speaker;
                lastScale = scale;
                lastWidth = width;
                lastFontSize = pixelFont;
                lastX = x;
                lastY = y;
                lastAlpha = alpha;
            }
            // SWP_NOACTIVATE plus a NOACTIVATE window never steals focus from the user's desktop app.
            if (!visible || !Native.IsWindowVisible(handle))
            {
                Check(Native.SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x0040), "SetWindowPos");
                visible = true;
            }
        }

        public void Hide()
        {
            VerifyThread();
            if (handle != IntPtr.Zero && visible) Native.ShowWindow(handle, 0);
            visible = false;
        }

        public void Dispose()
        {
            VerifyThread();
            if (disposed) return;
            disposed = true;
            if (handle != IntPtr.Zero) Native.DestroyWindow(handle);
            handle = IntPtr.Zero;
            visible = false;
            ReleaseSurface();
            if (font != IntPtr.Zero) Native.DeleteObject(font);
            if (speakerFont != IntPtr.Zero) Native.DeleteObject(speakerFont);
            font = IntPtr.Zero;
            speakerFont = IntPtr.Zero;
            if (memoryDc != IntPtr.Zero) Native.DeleteDC(memoryDc);
            memoryDc = IntPtr.Zero;
        }

        private void VerifyThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != threadId)
                throw new InvalidOperationException("Desktop subtitles must be updated and disposed on their creating thread.");
        }

        private void EnsureWindow()
        {
            if (handle != IntPtr.Zero) return;
            // WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW.
            // A null owner keeps this window visible when Unity is minimized. The STATIC class
            // uses Windows' native window procedure, so assembly reload cannot leave a callback.
            handle = Native.CreateWindowExW(0x00080000 | 0x00000020 | 0x08000000 | 0x00000080,
                "STATIC", recognition ? "NeEEvA Desktop Recognition" : "NeEEvA Desktop Subtitles", 0x80000000, 0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (handle == IntPtr.Zero) throw Error("CreateWindowExW");
        }

        private void GetDesktopLayout(out Native.RECT work, out float scale)
        {
            if (unityWindow == IntPtr.Zero || !Native.IsWindow(unityWindow))
            {
                using (Process process = Process.GetCurrentProcess()) unityWindow = process.MainWindowHandle;
                if (unityWindow == handle) unityWindow = IntPtr.Zero;
            }
            IntPtr monitor = Native.MonitorFromWindow(unityWindow, 2);
            Native.MONITORINFO info = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            Check(Native.GetMonitorInfoW(monitor, ref info), "GetMonitorInfoW");
            work = info.rcWork;
            uint dpi = 96;
            try
            {
                uint value = Native.GetDpiForWindow(unityWindow);
                if (value != 0) dpi = value;
            }
            catch (EntryPointNotFoundException) { /* Windows before 10 Anniversary Update. */ }
            scale = Math.Max(.75f, Math.Min(4f, dpi / 96f));
        }

        private void Render(string original, string translation, int width, int pixelFont, float scale, string speaker)
        {
            if (memoryDc == IntPtr.Zero)
            {
                memoryDc = Native.CreateCompatibleDC(IntPtr.Zero);
                if (memoryDc == IntPtr.Zero) throw Error("CreateCompatibleDC");
            }
            if (font == IntPtr.Zero || fontPixels != pixelFont)
            {
                IntPtr next = Native.CreateFontW(-pixelFont, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Microsoft YaHei UI");
                if (next == IntPtr.Zero) throw Error("CreateFontW");
                if (font != IntPtr.Zero) Native.DeleteObject(font);
                font = next;
                fontPixels = pixelFont;
            }
            IntPtr oldFont = Native.SelectObject(memoryDc, font);
            try
            {
                int paddingX = Scale(recognition ? 14 : 28, scale), paddingY = Scale(recognition ? 8 : 17, scale), gap = Scale(8, scale);
                int textWidth = Math.Max(1, width - paddingX * 2);
                int lineHeight = Measure("Agあ国", textWidth, recognition).bottom;
                // ASR keeps the complete current hypothesis, clipped to its latest end rather
                // than paging or appending fragments. Corrections replace the previous text.
                bool alignTail = recognition && original.Length > 0 && Measure(original, textWidth, true).right > textWidth;
                if (!recognition) original = FitTail(original, textWidth, lineHeight * 2);
                translation = FitTail(translation, textWidth, lineHeight * 2);
                int sourceHeight = original.Length == 0 ? 0 : recognition ? lineHeight : Measure(original, textWidth).bottom;
                int translationHeight = translation.Length == 0 ? 0 : Measure(translation, textWidth).bottom;
                int between = sourceHeight > 0 && translationHeight > 0 ? gap : 0;
                int bodyHeight = Math.Max(recognition ? Scale(42, scale) : 1, paddingY * 2 + sourceHeight + translationHeight + between);
                int headerHeight = 0;
                if (speaker.Length > 0)
                {
                    int labelPixels = Scale(16, scale);
                    if (speakerFont == IntPtr.Zero || speakerFontPixels != labelPixels)
                    {
                        IntPtr next = Native.CreateFontW(-labelPixels, 0, 0, 0, 500, 0, 0, 0, 1, 0, 0, 4, 0, "Microsoft YaHei UI");
                        if (next == IntPtr.Zero) throw Error("CreateFontW speaker");
                        if (speakerFont != IntPtr.Zero) Native.DeleteObject(speakerFont);
                        speakerFont = next;
                        speakerFontPixels = labelPixels;
                    }
                    Native.SelectObject(memoryDc, speakerFont);
                    headerHeight = Measure("Agあ国", textWidth, true).bottom + Scale(6, scale);
                    Native.SelectObject(memoryDc, font);
                }
                int sourceY = headerHeight + (recognition ? (bodyHeight - sourceHeight) / 2 : paddingY);
                int translationY = headerHeight + paddingY + sourceHeight + between;
                int height = headerHeight + bodyHeight;
                EnsureSurface(width, height);
                Array.Clear(pixels, 0, pixels.Length);
                Marshal.Copy(pixels, 0, bits, pixels.Length);
                Native.SetBkMode(memoryDc, 1);
                Native.SetTextColor(memoryDc, 0x00ffffff);
                if (sourceHeight > 0) Draw(original, new Native.RECT(paddingX, sourceY, width - paddingX, sourceY + sourceHeight), recognition, alignTail);
                if (translationHeight > 0) Draw(translation, new Native.RECT(paddingX, translationY, width - paddingX, translationY + translationHeight));
                if (headerHeight > 0)
                {
                    Native.SelectObject(memoryDc, speakerFont);
                    Native.RECT label = new Native.RECT(Scale(4, scale), 0, width - Scale(4, scale), headerHeight - Scale(6, scale));
                    if (Native.DrawTextW(memoryDc, speaker, speaker.Length, ref label,
                        0x0020u | 0x0800u | 0x8000u | (recognition ? 0u : 0x0001u)) == 0) throw Error("DrawTextW speaker");
                    Native.SelectObject(memoryDc, font);
                }
                // GDI does not write a meaningful alpha channel. Use white-on-black glyphs as
                // a grayscale coverage mask, then compose the required premultiplied BGRA DIB.
                Native.GdiFlush();
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                byte[] headingMask = new byte[width * headerHeight];
                for (int i = 0; i < headingMask.Length; i++)
                    headingMask[i] = Math.Max(pixels[i * 4], Math.Max(pixels[i * 4 + 1], pixels[i * 4 + 2]));
                float radius = Scale(16, scale);
                for (int y = 0; y < height; y++)
                {
                    bool secondary = recognition || (sourceHeight > 0 && translationHeight > 0 && y >= translationY);
                    int red = secondary ? 183 : 240, green = secondary ? 211 : 241, blue = secondary ? 201 : 234;
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width + x) * 4;
                        if (y < headerHeight)
                        {
                            float heading = headingMask[y * width + x] / 255f;
                            float shadow = HeadingShadow(headingMask, x, y, width, headerHeight, Scale(1, scale)) * (1 - heading);
                            pixels[i] = (byte)Math.Round(blue * heading + 10 * shadow);
                            pixels[i + 1] = (byte)Math.Round(green * heading + 10 * shadow);
                            pixels[i + 2] = (byte)Math.Round(red * heading + 10 * shadow);
                            pixels[i + 3] = (byte)Math.Round((heading + shadow) * 255);
                            continue;
                        }
                        float corner = CornerCoverage(x, y - headerHeight, width, bodyHeight, radius);
                        float glyph = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) / 255f * corner;
                        float background = .84f * corner * (1f - glyph);
                        pixels[i] = (byte)Math.Round(blue * glyph + 29 * background);
                        pixels[i + 1] = (byte)Math.Round(green * glyph + 27 * background);
                        pixels[i + 2] = (byte)Math.Round(red * glyph + 19 * background);
                        pixels[i + 3] = (byte)Math.Round((glyph + background) * 255);
                    }
                }
                Marshal.Copy(pixels, 0, bits, pixels.Length);
            }
            finally { Native.SelectObject(memoryDc, oldFont); }
        }

        private static float HeadingShadow(byte[] mask, int x, int y, int width, int height, int radius)
        {
            // A small outline keeps names readable over light desktop backgrounds while the
            // rest of the label row remains transparent, rather than becoming a second card.
            float coverage = 0;
            for (int dy = -1; dy <= 1; dy++)
            {
                int sampleY = y + dy * radius;
                if (sampleY < 0 || sampleY >= height) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int sampleX = x + dx * radius;
                    if (sampleX < 0 || sampleX >= width) continue;
                    coverage = Math.Max(coverage, mask[sampleY * width + sampleX] / 255f * .8f);
                }
            }
            return coverage;
        }

        private string FitTail(string value, int width, int maxHeight)
        {
            if (value.Length == 0 || Fits(value, width, maxHeight)) return value;
            // Keep the current end of a streaming sentence, without cutting a surrogate pair
            // or combining sequence. The leading ellipsis makes the omitted prefix explicit.
            int[] starts = StringInfo.ParseCombiningCharacters(value);
            int low = 0, high = starts.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                string candidate = "…" + value.Substring(starts[middle]);
                if (Fits(candidate, width, maxHeight)) high = middle;
                else low = middle + 1;
            }
            return low >= starts.Length ? "…" : "…" + value.Substring(starts[low]);
        }

        private bool Fits(string value, int width, int maxHeight)
        {
            Native.RECT measured = Measure(value, width);
            return measured.right <= width && measured.bottom <= maxHeight;
        }

        private Native.RECT Measure(string value, int width, bool singleLine = false)
        {
            Native.RECT rect = new Native.RECT(0, 0, width, 0);
            if (Native.DrawTextW(memoryDc, value, value.Length, ref rect, (singleLine ? 0x0020u : 0x0010u) | 0x0400u | 0x0800u) == 0)
                throw Error("DrawTextW measurement");
            return rect;
        }

        private void Draw(string value, Native.RECT rect, bool singleLine = false, bool alignTail = false)
        {
            uint flags = singleLine ? 0x0020u | (alignTail ? 0x0002u : 0u) : 0x0001u | 0x0010u | 0x2000u;
            if (Native.DrawTextW(memoryDc, value, value.Length, ref rect, flags | 0x0800) == 0)
                throw Error("DrawTextW");
        }

        private void EnsureSurface(int width, int height)
        {
            if (bitmap != IntPtr.Zero && width == surfaceWidth && height == surfaceHeight) return;
            ReleaseSurface();
            Native.BITMAPINFO info = new Native.BITMAPINFO
            {
                header = new Native.BITMAPINFOHEADER
                {
                    biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32
                }
            };
            bitmap = Native.CreateDIBSection(memoryDc, ref info, 0, out bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw Error("CreateDIBSection");
            previousBitmap = Native.SelectObject(memoryDc, bitmap);
            surfaceWidth = width;
            surfaceHeight = height;
            pixels = new byte[checked(width * height * 4)];
        }

        private void ReleaseSurface()
        {
            if (bitmap != IntPtr.Zero)
            {
                if (previousBitmap != IntPtr.Zero) Native.SelectObject(memoryDc, previousBitmap);
                Native.DeleteObject(bitmap);
            }
            bitmap = previousBitmap = bits = IntPtr.Zero;
            pixels = null;
            surfaceWidth = surfaceHeight = 0;
        }

        private static float CornerCoverage(int x, int y, int width, int height, float radius)
        {
            float dx = Math.Max(0, radius - Math.Min(x + .5f, width - x - .5f));
            float dy = Math.Max(0, radius - Math.Min(y + .5f, height - y - .5f));
            if (dx == 0 || dy == 0) return 1;
            return Math.Max(0, Math.Min(1, radius + .5f - (float)Math.Sqrt(dx * dx + dy * dy)));
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Replace("\0", string.Empty).Trim();
        }

        private static int Scale(int value, float scale) { return Math.Max(1, (int)Math.Round(value * scale)); }
        private static Win32Exception Error(string operation) { return new Win32Exception(Marshal.GetLastWin32Error(), "Desktop subtitles: " + operation + " failed."); }
        private static void Check(bool success, string operation) { if (!success) throw Error(operation); }

        private static class Native
        {
            [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int x, y; public POINT(int x, int y) { this.x = x; this.y = y; } }
            [StructLayout(LayoutKind.Sequential)] internal struct SIZE { public int width, height; public SIZE(int width, int height) { this.width = width; this.height = height; } }
            [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int left, top, right, bottom; public RECT(int left, int top, int right, int bottom) { this.left = left; this.top = top; this.right = right; this.bottom = bottom; } }
            [StructLayout(LayoutKind.Sequential)] internal struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
            [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
            [StructLayout(LayoutKind.Sequential)] internal struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
            [StructLayout(LayoutKind.Sequential)] internal struct BITMAPINFO { public BITMAPINFOHEADER header; public uint colors; }

            [DllImport("kernel32.dll")] internal static extern uint GetCurrentProcessId();
            [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
            [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr hwnd);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr hwnd);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(IntPtr hwnd);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(IntPtr hwnd, int command);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr destinationDc, ref POINT destination, ref SIZE size, IntPtr sourceDc, ref POINT source, uint colorKey, ref BLENDFUNCTION blend, uint flags);
            [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
            [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern int DrawTextW(IntPtr dc, string text, int length, ref RECT rect, uint format);
            [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
            [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(IntPtr dc);
            [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
            [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(IntPtr gdiObject);
            [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint characterSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
            [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
            [DllImport("gdi32.dll")] internal static extern int SetBkMode(IntPtr dc, int mode);
            [DllImport("gdi32.dll")] internal static extern uint SetTextColor(IntPtr dc, uint color);
            [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GdiFlush();
        }
#else
        public static bool IsSupported { get { return false; } }
        public IntPtr Handle { get { return IntPtr.Zero; } }
        public bool IsUnityInBackground { get { return false; } }
        public void Present(string original, string translation, int fontSize, float opacity, string speaker = "") { }
        public void Hide() { }
        public void Dispose() { }
#endif
    }
}
