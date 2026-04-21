using System.Drawing;
using System.Drawing.Imaging;
using Tesseract;

namespace RR2Bot.Core;

public static class OcrReader
{
    private static readonly string TessDataDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

    private static TesseractEngine? _engine;

    private static TesseractEngine? GetEngine()
    {
        if (_engine != null) return _engine;
        if (!Directory.Exists(TessDataDir)) return null;
        try
        {
            _engine = new TesseractEngine(TessDataDir, "eng", EngineMode.LstmOnly);
            _engine.SetVariable("tessedit_char_whitelist",
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789?! ");
            return _engine;
        }
        catch { return null; }
    }

    /// <summary>
    /// Đọc text trong vùng tỉ lệ (xr1,yr1)→(xr2,yr2) của bitmap.
    /// Trả về chuỗi lowercase đã trim, hoặc "" nếu không đọc được.
    /// </summary>
    public static string ReadRegion(Bitmap bmp, double xr1, double yr1, double xr2, double yr2)
    {
        var engine = GetEngine();
        if (engine == null) return "";

        int x = (int)(bmp.Width  * xr1);
        int y = (int)(bmp.Height * yr1);
        int w = (int)(bmp.Width  * (xr2 - xr1));
        int h = (int)(bmp.Height * (yr2 - yr1));
        if (w <= 0 || h <= 0) return "";

        using var crop = bmp.Clone(new Rectangle(x, y, w, h), bmp.PixelFormat);
        using var pix  = BitmapToPix(crop);
        using var page = engine.Process(pix, PageSegMode.SingleBlock);
        return page.GetText().Trim().ToLowerInvariant();
    }

    /// <summary>Kiểm tra vùng có chứa keyword không (case-insensitive).</summary>
    public static bool RegionContains(Bitmap bmp, double xr1, double yr1, double xr2, double yr2,
        params string[] keywords)
    {
        var text = ReadRegion(bmp, xr1, yr1, xr2, yr2);
        if (string.IsNullOrEmpty(text)) return false;
        return keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static Pix BitmapToPix(Bitmap bmp)
    {
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        return Pix.LoadFromMemory(ms.ToArray());
    }

    public static void Dispose()
    {
        _engine?.Dispose();
        _engine = null;
    }
}
