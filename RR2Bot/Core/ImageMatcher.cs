using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace RR2Bot.Core;

public static class ImageMatcher
{
    private static readonly string TemplateDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates");

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Tìm template trong source bitmap.
    /// Trả về (found, centerPoint, confidence).
    /// </summary>
    public static (bool Found, Point Location, double Confidence) FindTemplate(
        Bitmap source,
        Bitmap template,
        double threshold = 0.80)
    {
        using var srcMat = BitmapToMat(source);
        using var tplMat = BitmapToMat(template);
        using var result = new Mat();

        CvInvoke.MatchTemplate(srcMat, tplMat, result, TemplateMatchingType.CcoeffNormed);

        double minVal = 0, maxVal = 0;
        Point minLoc = Point.Empty, maxLoc = Point.Empty;
        CvInvoke.MinMaxLoc(result, ref minVal, ref maxVal, ref minLoc, ref maxLoc);

        if (maxVal >= threshold)
        {
            var center = new Point(
                maxLoc.X + template.Width  / 2,
                maxLoc.Y + template.Height / 2);
            return (true, center, maxVal);
        }

        return (false, Point.Empty, maxVal);
    }

    /// <summary>
    /// Tìm tất cả vị trí template xuất hiện (dùng cho nhiều building/gold).
    /// </summary>
    public static List<Point> FindAllTemplates(
        Bitmap source,
        Bitmap template,
        double threshold = 0.80)
    {
        var results = new List<Point>();
        using var srcMat = BitmapToMat(source);
        using var tplMat = BitmapToMat(template);
        using var result = new Mat();

        CvInvoke.MatchTemplate(srcMat, tplMat, result, TemplateMatchingType.CcoeffNormed);

        // Lặp tìm các điểm đủ ngưỡng, suppress non-max
        using var thresh = new Mat();
        CvInvoke.Threshold(result, thresh, threshold, 1.0, ThresholdType.Binary);

        // Đọc raw bytes từ Mat (float32 = 4 bytes/pixel)
        int total = thresh.Rows * thresh.Cols;
        byte[] raw = new byte[total * sizeof(float)];
        Marshal.Copy(thresh.DataPointer, raw, 0, raw.Length);

        int halfW = template.Width  / 2;
        int halfH = template.Height / 2;

        for (int i = 0; i < total; i++)
        {
            float val = BitConverter.ToSingle(raw, i * sizeof(float));
            if (val > 0f)
            {
                int row = i / thresh.Cols;
                int col = i % thresh.Cols;
                results.Add(new Point(col + halfW, row + halfH));
            }
        }

        return results;
    }

    /// <summary>
    /// Load template từ thư mục Templates/.
    /// Trả về null nếu file không tồn tại.
    /// </summary>
    public static Bitmap? LoadTemplate(string fileName)
    {
        var path = Path.Combine(TemplateDir, fileName);
        if (!File.Exists(path)) return null;
        return new Bitmap(path);
    }

    // ── Conversion ────────────────────────────────────────────────────────────

    /// <summary>
    /// Chuyển Bitmap sang Mat (BGR, 24bpp) không cần gói Emgu.CV.Bitmap.
    /// </summary>
    public static Mat BitmapToMat(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bmpData = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var mat = new Mat(bmp.Height, bmp.Width, DepthType.Cv8U, 3);
            // Copy từng row vì stride có thể lớn hơn width*3
            int matStep  = mat.Step;
            int bmpStride = bmpData.Stride;
            int rowBytes  = bmp.Width * 3;

            byte[] buffer = new byte[bmp.Height * rowBytes];
            for (int row = 0; row < bmp.Height; row++)
            {
                Marshal.Copy(
                    bmpData.Scan0 + row * bmpStride,
                    buffer, row * rowBytes, rowBytes);
            }

            Marshal.Copy(buffer, 0, mat.DataPointer, buffer.Length);
            return mat;
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }
    }
}
