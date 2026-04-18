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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint SRCCOPY = 0x00CC0020;

    #endregion

    /// <summary>
    /// Chụp client area của cửa sổ theo tên, tự scale theo DPI của monitor chứa cửa sổ.
    /// </summary>
    public static Bitmap? CaptureWindow(string windowTitle)
    {
        var hWnd = FindWindow(null, windowTitle);
        if (hWnd == IntPtr.Zero) return null;

        GetClientRect(hWnd, out RECT rect);
        int logicalW = rect.Right  - rect.Left;
        int logicalH = rect.Bottom - rect.Top;
        if (logicalW <= 0 || logicalH <= 0) return null;

        var bmp = new Bitmap(logicalW, logicalH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        var hdcDest = g.GetHdc();
        var hdcSrc  = GetDC(hWnd);

        BitBlt(hdcDest, 0, 0, logicalW, logicalH, hdcSrc, 0, 0, SRCCOPY);

        ReleaseDC(hWnd, hdcSrc);
        g.ReleaseHdc(hdcDest);
        return bmp;
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
