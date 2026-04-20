using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RR2Bot.Core;

public static class ScreenCapture
{
    #region Win32

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
        IntPtr hdcSrc, int xSrc, int ySrc, uint dwRop);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint SRCCOPY = 0x00CC0020;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const int LOGPIXELSX = 88;

    #endregion

    /// <summary>
    /// Chụp client area của cửa sổ, scale theo DPI thực của monitor → trả về physical pixels.
    /// </summary>
    private const int TargetW = 1600;
    private const int TargetH = 900;

    /// <summary>
    /// Chụp client area và normalize về 1600×900 để pixel logic + template matching
    /// hoạt động nhất quán trên mọi máy bất kể DPI hay kích thước BlueStacks.
    /// </summary>
    public static Bitmap? CaptureWindow(string windowTitle)
    {
        var hWnd = FindWindow(null, windowTitle);
        if (hWnd == IntPtr.Zero) return null;

        GetClientRect(hWnd, out RECT rect);
        int srcW = rect.Right  - rect.Left;
        int srcH = rect.Bottom - rect.Top;
        if (srcW <= 0 || srcH <= 0) return null;

        // Chụp ở kích thước logical của cửa sổ
        var raw = new Bitmap(srcW, srcH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(raw))
        {
            var hdcDest = g.GetHdc();
            var hdcSrc  = GetDC(hWnd);
            BitBlt(hdcDest, 0, 0, srcW, srcH, hdcSrc, 0, 0, SRCCOPY);
            ReleaseDC(hWnd, hdcSrc);
            g.ReleaseHdc(hdcDest);
        }

        LastCaptureInfo = $"raw={srcW}×{srcH}";

        // Normalize về 1600×900 nếu kích thước khác
        if (srcW == TargetW && srcH == TargetH) return raw;

        LastCaptureInfo += $" → normalized {TargetW}×{TargetH}";
        var normalized = new Bitmap(TargetW, TargetH, PixelFormat.Format32bppArgb);
        using (var ng = Graphics.FromImage(normalized))
        {
            ng.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            ng.DrawImage(raw, 0, 0, TargetW, TargetH);
        }
        raw.Dispose();
        return normalized;
    }

    /// <summary>
    /// Thông tin kích thước lần chụp gần nhất — dùng để log/debug.
    /// </summary>
    public static string LastCaptureInfo { get; private set; } = "";

    private static float GetDpiScale(IntPtr hWnd)
    {
        var hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
            return dpiX / 96f;

        // Fallback: đọc từ screen DC nếu GetDpiForMonitor không khả dụng
        var hdc = GetDC(IntPtr.Zero);
        int dpi = GetDeviceCaps(hdc, LOGPIXELSX);
        ReleaseDC(IntPtr.Zero, hdc);
        return dpi / 96f;
    }

    /// <summary>
    /// Kiểm tra cửa sổ có đang mở không.
    /// </summary>
    public static bool IsWindowOpen(string windowTitle)
    {
        var hWnd = FindWindow(null, windowTitle);
        return hWnd != IntPtr.Zero;
    }
}
