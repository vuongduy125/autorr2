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

    private static readonly double[] ScaleLevels = { 1.0, 0.9, 0.8, 1.1, 1.2 };

    /// <summary>
    /// Tìm template trong source bitmap, thử nhiều scale.
    /// borderMatchOnly=true: chỉ match phần viền ngoài, bỏ qua fill bên trong (dùng cho HP bar).
    /// </summary>
    public static (bool Found, Point Location, double Confidence) FindTemplate(
        Bitmap source,
        Bitmap template,
        double threshold = 0.80,
        bool borderMatchOnly = false,
        int borderSize = 5)
    {
        using var srcMat = BitmapToMat(source);

        double bestConf = 0;
        Point  bestLoc  = Point.Empty;
        int    bestW = template.Width, bestH = template.Height;

        foreach (double scale in ScaleLevels)
        {
            int sw = (int)(template.Width  * scale);
            int sh = (int)(template.Height * scale);
            if (sw < 4 || sh < 4 || sw > source.Width || sh > source.Height) continue;

            using var tplScaled = scale == 1.0
                ? BitmapToMat(template)
                : ResizeMat(BitmapToMat(template), sw, sh);
            using var result = new Mat();

            if (borderMatchOnly && sw > borderSize * 2 + 2 && sh > borderSize * 2 + 2)
            {
                int bs = Math.Max(1, (int)(borderSize * scale));
                using var mask = MakeBorderMask(sw, sh, bs);
                CvInvoke.MatchTemplate(srcMat, tplScaled, result, TemplateMatchingType.CcorrNormed, mask);
            }
            else
            {
                CvInvoke.MatchTemplate(srcMat, tplScaled, result, TemplateMatchingType.CcoeffNormed);
            }

            double minVal = 0, maxVal = 0;
            Point minLoc = Point.Empty, maxLoc = Point.Empty;
            CvInvoke.MinMaxLoc(result, ref minVal, ref maxVal, ref minLoc, ref maxLoc);

            if (maxVal > bestConf)
            {
                bestConf = maxVal;
                bestLoc  = maxLoc;
                bestW = sw; bestH = sh;
            }

            if (bestConf >= threshold) break;
        }

        if (bestConf >= threshold)
        {
            var center = new Point(bestLoc.X + bestW / 2, bestLoc.Y + bestH / 2);
            return (true, center, bestConf);
        }

        return (false, Point.Empty, bestConf);
    }

    /// <summary>
    /// Tạo mask chỉ giữ viền ngoài borderSize px, bên trong = 0 (bỏ qua).
    /// </summary>
    private static Mat MakeBorderMask(int w, int h, int borderSize)
    {
        var mask = new Mat(h, w, DepthType.Cv8U, 1);
        mask.SetTo(new MCvScalar(255));

        // Xóa vùng bên trong (chỉ giữ viền)
        int innerX = borderSize, innerY = borderSize;
        int innerW = w - borderSize * 2, innerH = h - borderSize * 2;
        if (innerW > 0 && innerH > 0)
        {
            var roi = new System.Drawing.Rectangle(innerX, innerY, innerW, innerH);
            using var inner = new Mat(mask, roi);
            inner.SetTo(new MCvScalar(0));
        }
        return mask;
    }

    private static Mat ResizeMat(Mat src, int w, int h)
    {
        var dst = new Mat();
        CvInvoke.Resize(src, dst, new System.Drawing.Size(w, h));
        src.Dispose();
        return dst;
    }

    /// <summary>
    /// Tìm tất cả vị trí template xuất hiện (dùng cho nhiều building/gold).
    /// </summary>
    public static List<Point> FindAllTemplates(
        Bitmap source,
        Bitmap template,
        double threshold = 0.80)
    {
        if (template.Width > source.Width || template.Height > source.Height)
            return new List<Point>();

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
