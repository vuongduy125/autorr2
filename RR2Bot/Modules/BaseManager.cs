using System.Drawing;
using RR2Bot.Core;
using RR2Bot.Models;

namespace RR2Bot.Modules;

/// <summary>
/// Base management: thu vàng, thu lương thực, upgrade tòa nhà.
/// Dùng template matching thuần — không cần logic phức tạp.
/// </summary>
public class BaseManager
{
    private readonly AdbController _adb;
    private readonly BotConfig _cfg;
    private Action<string>? _log;

    // ── Template file names (để trong thư mục Templates/) ────────────────────
    // Thêm / bớt tuỳ theo resource bạn muốn thu
    private static readonly string[] CollectTemplates =
    {
        "gold_collect.png",      // biểu tượng vàng nổi trên mỏ vàng
        "food_collect.png",      // biểu tượng lương thực
    };

    private static readonly string[] UpgradeTemplates =
    {
        "builder_idle.png",      // builder không có việc
        "upgrade_btn.png",       // nút Upgrade màu xanh
    };

    private static readonly string[] ConfirmTemplates =
    {
        "confirm_upgrade.png",   // nút xác nhận upgrade
    };

    // ────────────────────────────────────────────────────────────────────────

    public BaseManager(AdbController adb, BotConfig cfg, Action<string>? log = null)
    {
        _adb = adb;
        _cfg = cfg;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log("BaseManager started.");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var screen = ScreenCapture.CaptureWindow(_cfg.WindowTitle);
                if (screen == null)
                {
                    Log("Window not found, waiting...");
                    await Task.Delay(3000, ct);
                    continue;
                }

                CollectResources(screen);
                screen.Dispose();

                await Task.Delay(1000, ct);

                screen = ScreenCapture.CaptureWindow(_cfg.WindowTitle);
                if (screen != null)
                {
                    TryUpgrade(screen);
                    screen.Dispose();
                }

                await Task.Delay(_cfg.BaseLoopIntervalMs, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"[Error] {ex.Message}"); }
        }
        Log("BaseManager stopped.");
    }

    // ── Thu tài nguyên ───────────────────────────────────────────────────────

    private void CollectResources(Bitmap screen)
    {
        using var tpl = ImageMatcher.LoadTemplate("gold_collect.png");
        if (tpl == null)
        {
            _adb.TapRatio(_cfg.GoldCollectXRatio, _cfg.GoldCollectYRatio);
            Log("Tapped gold collect button (no template).");
            return;
        }

        var (found, pt, confidence) = ImageMatcher.FindTemplate(screen, tpl, _cfg.GoldMatchThreshold);
        Log($"Gold template confidence: {confidence:F2} (threshold: {_cfg.GoldMatchThreshold})");
        if (found)
        {
            _adb.TapScaled(pt.X, pt.Y, screen.Width, screen.Height);
            Log("Gold collected.");
        }
    }

    // ── Upgrade tòa nhà ──────────────────────────────────────────────────────

    private void TryUpgrade(Bitmap screen)
    {
        // Tìm builder rảnh
        using var builderTpl = ImageMatcher.LoadTemplate(UpgradeTemplates[0]);
        if (builderTpl == null) return;

        var (builderFound, builderPt, _) = ImageMatcher.FindTemplate(
            screen, builderTpl, _cfg.MatchThreshold);
        if (!builderFound) return;

        Log("Idle builder found, looking for upgrade targets...");

        // Click vào một tòa nhà có thể upgrade (template nút upgrade)
        using var upgTpl = ImageMatcher.LoadTemplate(UpgradeTemplates[1]);
        if (upgTpl == null) return;

        var (upgFound, upgPt, _) = ImageMatcher.FindTemplate(
            screen, upgTpl, _cfg.MatchThreshold);
        if (!upgFound) return;

        _adb.TapScaled(upgPt.X, upgPt.Y, screen.Width, screen.Height);
        Thread.Sleep(600);

        // Xác nhận nếu có popup
        var screen2 = ScreenCapture.CaptureWindow(_cfg.WindowTitle);
        if (screen2 == null) return;

        foreach (var cfmName in ConfirmTemplates)
        {
            using var cfmTpl = ImageMatcher.LoadTemplate(cfmName);
            if (cfmTpl == null) continue;

            var (cfmFound, cfmPt, _) = ImageMatcher.FindTemplate(
                screen2, cfmTpl, _cfg.MatchThreshold);
            if (cfmFound)
            {
                _adb.TapScaled(cfmPt.X, cfmPt.Y, screen2.Width, screen2.Height);
                Log("Upgrade confirmed.");
                break;
            }
        }
        screen2.Dispose();
    }

    private void Log(string msg) => _log?.Invoke($"[Base] {msg}");
}
