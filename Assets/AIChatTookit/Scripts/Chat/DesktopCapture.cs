using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Windows 桌面截图工具——给 Agent Loop 的视觉感知用。
///
/// 设计要点：
/// 1. 纯 P/Invoke + Texture2D，不依赖 System.Drawing
/// 2. StretchBlt 直接缩放到目标尺寸，避免大 bitmap 中转
/// 3. 三种捕获模式：跟随前台窗口所在显示器(默认) / 主屏 / 指定显示器索引
/// 4. 输出 base64 data-URL，可直接放进 OpenAI 多模态消息的 image_url 字段
///
/// 调用必须在 Unity 主线程(用了 Texture2D)。捕获 1280x720 大约 50-150ms，调用方自行节流。
/// </summary>
public static class DesktopCapture
{
    public enum CaptureMode { ActiveWindow, Primary, Specific }

    #region Win32 P/Invoke

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr hdcDst, int x, int y, int cx, int cy,
        IntPtr hdcSrc, int x1, int y1, int cxSrc, int cySrc, uint rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        [Out] byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint MONITOR_DEFAULTTOPRIMARY = 1;
    const uint SRCCOPY = 0x00CC0020;
    const int HALFTONE = 4;
    const uint DIB_RGB_COLORS = 0;
    const uint BI_RGB = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256 * 4)]
        public byte[] bmiColors;
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr dwData);

    #endregion

    /// <summary>
    /// 捕获桌面并返回 base64 编码的 JPEG data-URL，可直接喂给多模态 LLM。
    /// 失败返回 null。
    /// </summary>
    /// <param name="mode">捕获模式：ActiveWindow=跟随前台窗口所在显示器；Primary=主屏；Specific=按 monitorIndex 指定</param>
    /// <param name="monitorIndex">仅 Specific 模式生效，0 表示第一台显示器</param>
    /// <param name="maxDimension">输出图像最长边像素(等比缩放)；典型值 1024-1280</param>
    /// <param name="jpegQuality">JPEG 质量 [1, 100]，建议 70-85 平衡体积与画质</param>
    public static string CaptureToBase64Jpeg(CaptureMode mode, int monitorIndex, int maxDimension, int jpegQuality)
    {
        if (Application.platform != RuntimePlatform.WindowsPlayer
            && Application.platform != RuntimePlatform.WindowsEditor)
        {
            Debug.LogWarning("[DesktopCapture] 仅支持 Windows 平台");
            return null;
        }

        RECT srcRect;
        if (!TryGetCaptureRect(mode, monitorIndex, out srcRect))
        {
            Debug.LogWarning("[DesktopCapture] 无法确定捕获区域");
            return null;
        }
        int srcW = srcRect.right - srcRect.left;
        int srcH = srcRect.bottom - srcRect.top;
        if (srcW <= 0 || srcH <= 0) return null;

        //等比缩放到目标尺寸——StretchBlt 让 GDI 直接做缩放，避免捕获 4K 全分辨率再 CPU 缩
        int dstW, dstH;
        ComputeFitSize(srcW, srcH, maxDimension, out dstW, out dstH);

        IntPtr screenDC = GetDC(IntPtr.Zero);
        IntPtr memDC = CreateCompatibleDC(screenDC);
        IntPtr hBitmap = CreateCompatibleBitmap(screenDC, dstW, dstH);
        IntPtr oldObj = SelectObject(memDC, hBitmap);

        SetStretchBltMode(memDC, HALFTONE);  //质量更好的下采样滤波
        bool blitOk = StretchBlt(memDC, 0, 0, dstW, dstH,
            screenDC, srcRect.left, srcRect.top, srcW, srcH, SRCCOPY);

        byte[] bgra = null;
        if (blitOk)
        {
            //正 height = bottom-up DIB——Unity 的 Texture2D 原点在左下,byte 第 0 行
            //会被映射到纹理底部。如果写 -dstH(top-down),EncodeToJPG 输出反而上下颠倒。
            BITMAPINFO bi = new BITMAPINFO();
            bi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bi.bmiHeader.biWidth = dstW;
            bi.bmiHeader.biHeight = dstH;
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = BI_RGB;
            bi.bmiColors = new byte[256 * 4];

            bgra = new byte[dstW * dstH * 4];
            int scanLines = GetDIBits(memDC, hBitmap, 0, (uint)dstH, bgra, ref bi, DIB_RGB_COLORS);
            if (scanLines == 0) bgra = null;
        }

        //清理 GDI 资源
        SelectObject(memDC, oldObj);
        DeleteObject(hBitmap);
        DeleteDC(memDC);
        ReleaseDC(IntPtr.Zero, screenDC);

        if (bgra == null)
        {
            Debug.LogWarning("[DesktopCapture] StretchBlt 或 GetDIBits 失败");
            return null;
        }

        //BGRA → RGBA(交换 B/R)，否则 EncodeToJPG 会颜色错位
        SwapBlueRed(bgra);

        //Texture2D + JPEG 编码
        Texture2D tex = new Texture2D(dstW, dstH, TextureFormat.RGBA32, false);
        try
        {
            tex.LoadRawTextureData(bgra);
            tex.Apply(false, false);
            byte[] jpeg = tex.EncodeToJPG(Mathf.Clamp(jpegQuality, 1, 100));
            string b64 = Convert.ToBase64String(jpeg);
            return "data:image/jpeg;base64," + b64;
        }
        finally
        {
            UnityEngine.Object.Destroy(tex);
        }
    }

    /// <summary>
    /// 同步算出捕获区域对应的桌面坐标矩形。
    /// </summary>
    static bool TryGetCaptureRect(CaptureMode mode, int monitorIndex, out RECT outRect)
    {
        outRect = default;

        if (mode == CaptureMode.ActiveWindow)
        {
            IntPtr hwnd = GetForegroundWindow();
            IntPtr hMon = (hwnd != IntPtr.Zero)
                ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
                : MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
            return TryGetMonitorRect(hMon, out outRect);
        }
        if (mode == CaptureMode.Primary)
        {
            IntPtr hMon = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
            return TryGetMonitorRect(hMon, out outRect);
        }
        // Specific：枚举显示器，按索引取
        var collected = new System.Collections.Generic.List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT lprc, IntPtr d) =>
        {
            collected.Add(lprc);
            return true;
        }, IntPtr.Zero);
        if (collected.Count == 0) return false;
        int idx = Mathf.Clamp(monitorIndex, 0, collected.Count - 1);
        outRect = collected[idx];
        return true;
    }

    static bool TryGetMonitorRect(IntPtr hMon, out RECT outRect)
    {
        outRect = default;
        if (hMon == IntPtr.Zero) return false;
        var mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf<MONITORINFO>();
        if (!GetMonitorInfo(hMon, ref mi)) return false;
        outRect = mi.rcMonitor;
        return true;
    }

    static void ComputeFitSize(int srcW, int srcH, int maxDim, out int dstW, out int dstH)
    {
        if (maxDim <= 0 || (srcW <= maxDim && srcH <= maxDim))
        {
            dstW = srcW; dstH = srcH; return;
        }
        float ratio = (float)maxDim / Mathf.Max(srcW, srcH);
        dstW = Mathf.Max(1, Mathf.RoundToInt(srcW * ratio));
        dstH = Mathf.Max(1, Mathf.RoundToInt(srcH * ratio));
    }

    /// <summary>BGRA → RGBA 原地交换 B 和 R 通道</summary>
    static void SwapBlueRed(byte[] bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte b = bgra[i];
            bgra[i] = bgra[i + 2];
            bgra[i + 2] = b;
        }
    }
}
