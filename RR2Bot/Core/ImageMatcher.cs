using System.Buffers;
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

    private static readonly Dictionary<string, Bitmap?> _templateCache = new();

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
        int borderSize = 5,
        bool brightMaskOnly = false,
        int brightThreshold = 160)
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

            if (brightMaskOnly)
            {
                using var mask = MakeBrightMask(tplScaled, brightThreshold);
                CvInvoke.MatchTemplate(srcMat, tplScaled, result, TemplateMatchingType.CcorrNormed, mask);
            }
            else if (borderMatchOnly && sw > borderSize * 2 + 2 && sh > borderSize * 2 + 2)
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
    /// <summary>
    /// Mask giữ lại pixel sáng (text trắng) trong template, bỏ pixel tối (background động).
    /// </summary>
    private static Mat MakeBrightMask(Mat tpl, int threshold)
    {
        using var gray = new Mat();
        CvInvoke.CvtColor(tpl, gray, ColorConversion.Bgr2Gray);
        var mask = new Mat();
        CvInvoke.Threshold(gray, mask, threshold, 255, ThresholdType.Binary);
        return mask;
    }

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

        using var srcMat = BitmapToMat(source);
        using var tplMat = BitmapToMat(template);
        using var result = new Mat();

        CvInvoke.MatchTemplate(srcMat, tplMat, result, TemplateMatchingType.CcoeffNormed);

        int total = result.Rows * result.Cols;
        byte[] raw = new byte[total * sizeof(float)];
        Marshal.Copy(result.DataPointer, raw, 0, raw.Length);

        int halfW = template.Width  / 2;
        int halfH = template.Height / 2;

        // Gom tất cả điểm vượt ngưỡng
        var candidates = new List<(int col, int row, float val)>();
        for (int i = 0; i < total; i++)
        {
            float val = BitConverter.ToSingle(raw, i * sizeof(float));
            if (val >= (float)threshold)
                candidates.Add((i % result.Cols, i / result.Cols, val));
        }

        // NMS: suppress các điểm trong vùng template size, chỉ giữ max
        candidates.Sort((a, b) => b.val.CompareTo(a.val));
        var kept = new List<Point>();
        int suppressW = template.Width, suppressH = template.Height;
        foreach (var (col, row, _) in candidates)
        {
            var pt = new Point(col + halfW, row + halfH);
            if (kept.Any(k => Math.Abs(k.X - pt.X) < suppressW && Math.Abs(k.Y - pt.Y) < suppressH))
                continue;
            kept.Add(pt);
        }

        return kept;
    }

    /// <summary>
    /// Load template từ thư mục Templates/, cache vào RAM.
    /// Caller nhận clone — dispose bình thường, không ảnh hưởng cache.
    /// </summary>
    public static Bitmap? LoadTemplate(string fileName, bool trimDark = false)
    {
        if (!_templateCache.TryGetValue(fileName, out var cached))
        {
            var path = Path.Combine(TemplateDir, fileName);
            cached = File.Exists(path) ? new Bitmap(path) : null;
            _templateCache[fileName] = cached;
        }
        if (cached == null) return null;
        var bmp = (Bitmap)cached.Clone();
        return trimDark ? TrimDarkBorder(bmp) : bmp;
    }

    /// <summary>
    /// Crop bỏ các hàng/cột border tối (brightness < threshold) — giữ lại vùng text sáng.
    /// </summary>
    private static Bitmap TrimDarkBorder(Bitmap bmp, int brightnessThreshold = 80)
    {
        int left = bmp.Width, right = 0, top = bmp.Height, bottom = 0;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.R > brightnessThreshold || c.G > brightnessThreshold || c.B > brightnessThreshold)
            {
                if (x < left)   left   = x;
                if (x > right)  right  = x;
                if (y < top)    top    = y;
                if (y > bottom) bottom = y;
            }
        }
        if (left > right || top > bottom) return bmp;
        var rect = new Rectangle(left, top, right - left + 1, bottom - top + 1);
        var cropped = bmp.Clone(rect, bmp.PixelFormat);
        bmp.Dispose();
        return cropped;
    }

    /// <summary>
    /// Xóa cache (gọi khi cần reload templates từ disk).
    /// </summary>
    public static void ClearTemplateCache()
    {
        foreach (var bmp in _templateCache.Values)
            bmp?.Dispose();
        _templateCache.Clear();
    }

    // ── Conversion ────────────────────────────────────────────────────────────

    /// <summary>
    /// Chuyển Bitmap sang Mat (BGR, 24bpp) không cần gói Emgu.CV.Bitmap.
    /// Dùng ArrayPool để tránh alloc lớn mỗi lần gọi.
    /// </summary>
    public static Mat BitmapToMat(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bmpData = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var mat       = new Mat(bmp.Height, bmp.Width, DepthType.Cv8U, 3);
            int bmpStride = bmpData.Stride;
            int rowBytes  = bmp.Width * 3;
            int totalBytes = bmp.Height * rowBytes;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(totalBytes);
            try
            {
                for (int row = 0; row < bmp.Height; row++)
                    Marshal.Copy(bmpData.Scan0 + row * bmpStride, buffer, row * rowBytes, rowBytes);
                Marshal.Copy(buffer, 0, mat.DataPointer, totalBytes);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return mat;
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }
    }
}
