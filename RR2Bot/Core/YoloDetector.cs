using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace RR2Bot.Core;

public record YoloDetection(string ClassName, float Confidence, RectangleF BBox)
{
    public Point Center => new((int)(BBox.X + BBox.Width / 2), (int)(BBox.Y + BBox.Height / 2));
}

public class YoloDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string[] _classNames;
    private readonly int _inputSize;

    // Order matches new clean Roboflow dataset (48 classes, alphabetical)
    public static readonly string[] ClassNames =
    [
        "base_alliance",               // 0
        "base_attack",                 // 1
        "base_collect_resources",      // 2
        "base_community",              // 3
        "base_food",                   // 4
        "base_gem",                    // 5
        "base_gift",                   // 6
        "base_gold",                   // 7
        "base_pearl",                  // 8
        "base_quest",                  // 9
        "base_upgrade",                // 10
        "batter_rs_lose",              // 11
        "battle_result_continue",      // 12
        "battle_result_label",         // 13
        "battle_result_lose",          // 14
        "battle_result_retreat",       // 15 NEW
        "battle_result_win",           // 16
        "chamber_chest",               // 17
        "chamber_getit",               // 18 NEW
        "chamber_giveup",              // 19
        "chamber_label",               // 20
        "chamber_sell",                // 21 NEW
        "chamber_tap_2_continue",      // 22
        "community_label",             // 23
        "community_label_favorites",   // 24
        "community_label_friends",     // 25
        "community_label_google_play", // 26
        "community_label_history",     // 27
        "community_label_insta-troops",// 28
        "community_label_leaderboard", // 29
        "favorite_player_list_attack", // 30
        "favorite_player_list_label",  // 31
        "inbatte_ally_heal",           // 32
        "inbatte_enemy_castle",        // 33
        "inbatte_enemy_heal",          // 34
        "inbatte_gold_bar",            // 35
        "inbatte_hero_heal",           // 36
        "inbatte_hero_skill",          // 37
        "inbatte_honk",                // 38
        "inbatte_mana_bar",            // 39
        "inbatte_pause",               // 40
        "inbatte_summon",              // 41
        "not_enough_food_collect",     // 42
        "not_enough_food_getfood",     // 43
        "not_enough_food_label",       // 44
        "prepare4battle_attack",       // 45
        "prepare4battle_label",        // 46
        "unknown_x_exit",              // 47
    ];

    public YoloDetector(string modelPath, int inputSize = 512)
    {
        _session    = new InferenceSession(modelPath);
        _classNames = ClassNames;
        _inputSize  = inputSize;

        // Warm-up: force JIT compile trước khi dùng thật
        using var dummy = new Bitmap(inputSize, inputSize);
        Detect(dummy, 0.99f);
    }

    public List<YoloDetection> Detect(Bitmap bmp, float confThreshold = 0.5f)
    {
        var tensor = Preprocess(bmp, out float scale, out int padX, out int padY);

        using var results = _session.Run(
            [NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), tensor)]);

        var output = results[0].AsTensor<float>();
        return Postprocess(output, confThreshold, scale, padX, padY, bmp.Width, bmp.Height);
    }

    private DenseTensor<float> Preprocess(Bitmap bmp, out float scale, out int padX, out int padY)
    {
        scale = Math.Min((float)_inputSize / bmp.Width, (float)_inputSize / bmp.Height);
        int newW = (int)(bmp.Width  * scale);
        int newH = (int)(bmp.Height * scale);
        padX = (_inputSize - newW) / 2;
        padY = (_inputSize - newH) / 2;

        var tensor = new DenseTensor<float>([1, 3, _inputSize, _inputSize]);

        // Fill letterbox gray
        for (int c = 0; c < 3; c++)
            for (int y = 0; y < _inputSize; y++)
                for (int x = 0; x < _inputSize; x++)
                    tensor[0, c, y, x] = 0.5f;

        using var resized = new Bitmap(newW, newH);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(bmp, 0, 0, newW, newH);
        }

        var rect    = new Rectangle(0, 0, newW, newH);
        var bmpData = resized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        int stride  = bmpData.Stride;
        byte[] px   = new byte[newH * stride];
        Marshal.Copy(bmpData.Scan0, px, 0, px.Length);
        resized.UnlockBits(bmpData);

        for (int y = 0; y < newH; y++)
        for (int x = 0; x < newW; x++)
        {
            int i = y * stride + x * 3;
            tensor[0, 0, y + padY, x + padX] = px[i + 2] / 255f; // R
            tensor[0, 1, y + padY, x + padX] = px[i + 1] / 255f; // G
            tensor[0, 2, y + padY, x + padX] = px[i + 0] / 255f; // B
        }

        return tensor;
    }

    private List<YoloDetection> Postprocess(Tensor<float> output, float confThreshold,
        float scale, int padX, int padY, int origW, int origH)
    {
        int numAnchors = output.Dimensions[2];
        int numClasses = output.Dimensions[1] - 4;

        var candidates = new List<YoloDetection>();

        for (int i = 0; i < numAnchors; i++)
        {
            float maxScore = 0; int bestClass = 0;
            for (int c = 0; c < numClasses; c++)
            {
                float s = output[0, 4 + c, i];
                if (s > maxScore) { maxScore = s; bestClass = c; }
            }
            if (maxScore < confThreshold) continue;

            float cx = output[0, 0, i];
            float cy = output[0, 1, i];
            float w  = output[0, 2, i];
            float h  = output[0, 3, i];

            float x1 = Math.Clamp((cx - w / 2 - padX) / scale, 0, origW);
            float y1 = Math.Clamp((cy - h / 2 - padY) / scale, 0, origH);
            float x2 = Math.Clamp((cx + w / 2 - padX) / scale, 0, origW);
            float y2 = Math.Clamp((cy + h / 2 - padY) / scale, 0, origH);

            string name = bestClass < _classNames.Length ? _classNames[bestClass] : $"cls{bestClass}";
            candidates.Add(new YoloDetection(name, maxScore, new RectangleF(x1, y1, x2 - x1, y2 - y1)));
        }

        return ApplyNms(candidates, 0.45f);
    }

    private static List<YoloDetection> ApplyNms(List<YoloDetection> dets, float iouThr)
    {
        dets.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        var kept       = new List<YoloDetection>();
        var suppressed = new bool[dets.Count];

        for (int i = 0; i < dets.Count; i++)
        {
            if (suppressed[i]) continue;
            kept.Add(dets[i]);
            for (int j = i + 1; j < dets.Count; j++)
            {
                if (dets[i].ClassName != dets[j].ClassName) continue;
                if (IoU(dets[i].BBox, dets[j].BBox) > iouThr) suppressed[j] = true;
            }
        }
        return kept;
    }

    private static float IoU(RectangleF a, RectangleF b)
    {
        var inter = RectangleF.Intersect(a, b);
        if (inter.IsEmpty) return 0;
        float interArea = inter.Width * inter.Height;
        float unionArea = a.Width * a.Height + b.Width * b.Height - interArea;
        return unionArea <= 0 ? 0 : interArea / unionArea;
    }

    public void Dispose() => _session.Dispose();
}
