using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RR2Bot.Core;
using RR2Bot.Models;

namespace RR2Bot.Modules;

public class BattleManager
{
    private readonly AdbController _adb;
    private readonly BotConfig _cfg;
    private Action<string>? _log;

    private const string TplSpellReady = "spell_ready.png";
    private const string TplVictory    = "victory.png";
    private const string TplDefeat     = "defeat.png";

    public BattleManager(AdbController adb, BotConfig cfg, Action<string>? log = null)
    {
        _adb = adb;
        _cfg = cfg;
        _log = log;
    }

    // ── State machine ─────────────────────────────────────────────────────────

    private enum BotState { AtBase, EnteringBattle, InBattle }
    private BotState _state = BotState.AtBase;
    private DateTime _enteringBattleAt;

    private int _hudMissCount = 0;
    private DateTime _battleStartAt;

    public async Task RunAsync(CancellationToken ct)
    {
        Log("BattleManager started.");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                switch (_state)
                {
                    case BotState.AtBase:
                        Log("State: AtBase → entering battle...");
                        bool entered = await EnterBattleAsync(ct);
                        if (entered)
                        {
                            _state = BotState.EnteringBattle;
                            _enteringBattleAt = DateTime.Now;
                            Log("Waiting for battle HUD...");
                        }
                        else
                        {
                            Log("EnterBattle failed, retrying in 5s...");
                            await Task.Delay(5000, ct);
                        }
                        break;

                    case BotState.EnteringBattle:
                        await Task.Delay(1000, ct);
                        if (IsBattleHudVisible())
                        {
                            Log("Battle HUD detected → starting actions.");
                            _hudMissCount = 0;
                            _battleStartAt = DateTime.Now;
                            _state = BotState.InBattle;
                        }
                        else if ((DateTime.Now - _enteringBattleAt).TotalSeconds > 30)
                        {
                            Log("Timeout waiting for battle HUD → back to AtBase.");
                            _state = BotState.AtBase;
                        }
                        break;

                    case BotState.InBattle:
                        var screen = ScreenCapture.CaptureWindow(_cfg.WindowTitle);
                        if (screen == null) { await Task.Delay(1000, ct); break; }

                        if (!IsBattleHudVisible(screen))
                        {
                            _hudMissCount++;
                            if (_hudMissCount < 4)
                            {
                                Log($"Battle HUD miss #{_hudMissCount} — waiting to confirm...");
                                screen.Dispose();
                                await Task.Delay(_cfg.BattleLoopIntervalMs, ct);
                                break;
                            }
                            Log("Battle HUD gone (confirmed) → battle over, returning to base.");
                            screen.Dispose();
                            _hudMissCount = 0;
                            _state = BotState.AtBase;
                            await Task.Delay(3000, ct);
                            break;
                        }
                        _hudMissCount = 0;

                        if (IsBattleOver(screen))
                        {
                            Log("Victory/Defeat detected → returning to base.");
                            screen.Dispose();
                            _state = BotState.AtBase;
                            await Task.Delay(3000, ct);
                            break;
                        }

                        bool hpLow = IsHpLow(screen);
                        bool inGrace = (DateTime.Now - _battleStartAt).TotalSeconds < 5;
                        var (enemyFound, ex, ey) = inGrace ? (false, 0.0, 0.0) : FindEnemyHpBar(screen);

                        if (hpLow)
                        {
                            Log("HP low! Retreating.");
                            SummonTroops();
                            UseReadySpells(screen);
                            _adb.TapRatio(0.39, 0.64);
                        }
                        else if (enemyFound)
                        {
                            Log($"Enemy at ({ex:F2},{ey:F2}) — moving in, spamming spells.");
                            SummonTroops();
                            MoveToward(ex, ey);
                            UseReadySpells(screen);
                        }
                        else
                        {
                            double jx = _cfg.HeroTargetXRatio + (Random.Shared.NextDouble() - 0.5) * 0.08;
                            double jy = _cfg.HeroTargetYRatio + (Random.Shared.NextDouble() - 0.5) * 0.08;
                            Log($"No enemy{(inGrace ? " (grace)" : "")} — moving forward ({jx:F2},{jy:F2}).");
                            MoveToward(jx, jy);
                        }
                        screen.Dispose();
                        await Task.Delay(_cfg.BattleLoopIntervalMs, ct);
                        break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"[Error] {ex.Message}"); }
        }
        Log("BattleManager stopped.");
    }

    // ── Hero movement ─────────────────────────────────────────────────────────

    private void MoveToward(double targetX, double targetY)
        => _adb.TapRatio(targetX, targetY);

    public void MoveHero(HeroDirection dir = HeroDirection.Forward)
    {
        double cx = _cfg.JoystickXRatio;
        double cy = _cfg.JoystickYRatio;
        double rx = _cfg.JoystickRadiusX;
        double ry = _cfg.JoystickRadiusY;

        var (toX, toY) = dir switch
        {
            HeroDirection.Forward  => (cx + rx, cy),
            HeroDirection.Backward => (cx - rx, cy),
            HeroDirection.Up       => (cx,      cy - ry),
            HeroDirection.Down     => (cx,      cy + ry),
            HeroDirection.UpRight  => (cx + rx, cy - ry),
            HeroDirection.UpLeft   => (cx - rx, cy - ry),
            _ => (cx + rx, cy),
        };

        _adb.SwipeRatio(cx, cy, toX, toY, _cfg.HeroMoveDurationMs);
    }

    public void MoveHeroTo(double targetXRatio, double targetYRatio)
        => _adb.SwipeRatio(_cfg.JoystickXRatio, _cfg.JoystickYRatio, targetXRatio, targetYRatio, 200);

    // ── Spells ────────────────────────────────────────────────────────────────

    // Global gate: nếu spell_ready.png tồn tại, chỉ cast khi tìm thấy trên màn hình.
    // Nếu không có template → spam tất cả slot (game tự bỏ qua slot đang cooldown).
    public void UseReadySpells(Bitmap? screen = null)
    {
        if (screen != null)
        {
            using var gate = ImageMatcher.LoadTemplate(TplSpellReady);
            if (gate != null)
            {
                var (anyReady, _, _) = ImageMatcher.FindTemplate(screen, gate, 0.75);
                if (!anyReady) return;
            }
        }

        foreach (var (x, y) in _cfg.SpellButtonRatios)
        {
            _adb.TapRatio(x, y);
            Thread.Sleep(80);
        }
    }

    public void UseSpell(int index)
    {
        if (index < 0 || index >= _cfg.SpellButtonRatios.Length) return;
        var (x, y) = _cfg.SpellButtonRatios[index];
        _adb.TapRatio(x, y);
    }

    // ── Troops ────────────────────────────────────────────────────────────────

    public void SummonTroops()
    {
        foreach (var (x, y) in _cfg.TroopButtonRatios)
        {
            _adb.TapRatio(x, y);
            Thread.Sleep(60);
        }
    }

    public void SummonTroop(int index)
    {
        if (index < 0 || index >= _cfg.TroopButtonRatios.Length) return;
        var (x, y) = _cfg.TroopButtonRatios[index];
        _adb.TapRatio(x, y);
    }

    // ── Detection (LockBits — không dùng GetPixel) ───────────────────────────

    private bool IsBattleHudVisible(Bitmap? screen = null)
    {
        bool dispose = screen == null;
        var bmp = screen ?? ScreenCapture.CaptureWindow(_cfg.WindowTitle);
        if (bmp == null) return false;

        var pixels = LockPixels(bmp, out int stride);

        // Mana bar: 6 điểm mẫu dọc theo y=94% — màu xanh dương
        int blueCount = 0;
        foreach (var xr in new[] { 0.05, 0.10, 0.15, 0.20, 0.25, 0.30 })
        {
            int px = (int)(bmp.Width  * xr);
            int py = (int)(bmp.Height * 0.94);
            if (px >= bmp.Width || py >= bmp.Height) continue;
            int i = py * stride + px * 4;
            byte b = pixels[i], r = pixels[i + 2];
            if (b > 100 && r < 100 && b > r + 60) blueCount++;
        }

        // Nút Pause (II) góc trên-trái
        bool pauseVisible = false;
        {
            int px = (int)(bmp.Width  * 0.043);
            int py = (int)(bmp.Height * 0.267);
            if (px < bmp.Width && py < bmp.Height)
            {
                int i = py * stride + px * 4;
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                pauseVisible = b > 120 && r < 150 && b > g;
            }
        }

        // Sword icon bottom-left — active hoặc disabled đều = đang trong battle
        bool swordVisible = false;
        {
            int px = (int)(bmp.Width  * 0.069);
            int py = (int)(bmp.Height * 0.828);
            if (px < bmp.Width && py < bmp.Height)
            {
                int i = py * stride + px * 4;
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                swordVisible = b > 100 && b > r + 30 && b > g + 20;
            }
        }

        bool swordDisabled = false;
        if (!swordVisible)
        {
            using var tpl = ImageMatcher.LoadTemplate("sword_icon_disable.png");
            if (tpl != null)
            {
                var (found, _, _) = ImageMatcher.FindTemplate(bmp, tpl, 0.75);
                swordDisabled = found;
            }
        }

        bool inBattle = blueCount >= 1 || pauseVisible || swordVisible || swordDisabled;
        Log($"Battle detect — mana:{blueCount}/6 pause:{pauseVisible} sword:{swordVisible} swordOff:{swordDisabled} → inBattle={inBattle}");

        if (dispose) bmp.Dispose();
        return inBattle;
    }

    private bool IsBattleOver(Bitmap screen)
    {
        foreach (var name in new[] { TplVictory, TplDefeat })
        {
            using var tpl = ImageMatcher.LoadTemplate(name);
            if (tpl == null) continue;
            var (found, pt, _) = ImageMatcher.FindTemplate(screen, tpl, 0.75);
            if (found)
            {
                Log($"Detected: {Path.GetFileNameWithoutExtension(name)}. Tapping OK...");
                _adb.TapScaled(pt.X, pt.Y, screen.Width, screen.Height);
                return true;
            }
        }
        return false;
    }

    // Tìm HP bar địch gần nhất (hero ở center 0.5,0.5) bằng pixel scan
    private (bool found, double x, double y) FindEnemyHpBar(Bitmap screen)
    {
        var pixels = LockPixels(screen, out int stride);
        int w = screen.Width, h = screen.Height;
        int x1 = (int)(w * 0.10), x2 = (int)(w * 0.90);
        int y1 = (int)(h * 0.10), y2 = (int)(h * 0.60);

        var candidates = new List<(int midX, int midY, int streak)>();

        for (int y = y1; y < y2; y++)
        {
            int streak = 0, streakStart = 0;
            int rowBase = y * stride;
            for (int x = x1; x < x2; x++)
            {
                int i = rowBase + x * 4;
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (r > 160 && g < 55 && b < 55)
                {
                    if (streak == 0) streakStart = x;
                    streak++;
                }
                else
                {
                    if (streak >= 8) candidates.Add(((streakStart + x) / 2, y, streak));
                    streak = 0;
                }
            }
            if (streak >= 8) candidates.Add(((streakStart + x2) / 2, y, streak));
        }

        if (candidates.Count == 0)
        {
            Log("Enemy HP bar: found=False streak=0");
            return (false, 0, 0);
        }

        // Lọc bỏ spell effects: HP bar thật chỉ dày 1-5 row liên tiếp.
        // Đếm run liên tiếp chứa midY — lửa/spell có gap hoặc run dài hơn nhiều.
        var realBars = new List<(int midX, int midY, int streak)>();
        var dbgSb = new System.Text.StringBuilder();
        foreach (var c in candidates)
        {
            int contig = 1;
            for (int dy = -1; dy >= -10; dy--)
            {
                int sy = c.midY + dy;
                if (sy < 0) break;
                int ci = sy * stride + c.midX * 4;
                byte cb = pixels[ci], cg = pixels[ci + 1], cr = pixels[ci + 2];
                if (cr > 150 && cg < 80 && cb < 80) contig++; else break;
            }
            for (int dy = 1; dy <= 10; dy++)
            {
                int sy = c.midY + dy;
                if (sy >= h) break;
                int ci = sy * stride + c.midX * 4;
                byte cb = pixels[ci], cg = pixels[ci + 1], cr = pixels[ci + 2];
                if (cr > 150 && cg < 80 && cb < 80) contig++; else break;
            }
            dbgSb.Append($"({c.midX},{c.midY},s{c.streak},v{contig}) ");
            if (contig <= 8) realBars.Add(c);
        }
        if (candidates.Count > 0) Log($"Candidates: {dbgSb}");

        if (realBars.Count == 0)
        {
            Log($"Enemy HP bar: found=False (all {candidates.Count} filtered as spell effects)");
            return (false, 0, 0);
        }

        // Chọn địch gần trung tâm nhất (hero ở 0.5, 0.5)
        double cx = w * 0.5, cy = h * 0.5;
        var best = realBars.MinBy(c => Math.Pow(c.midX - cx, 2) + Math.Pow(c.midY - cy, 2));
        double rx = best.midX / (double)w;
        double ry = Math.Min((best.midY + 50.0) / h, 0.85);
        Log($"Enemy HP bar: found=True streak={best.streak} bars={realBars.Count} at ({rx:F2},{ry:F2})");

        SaveEnemyDebug(screen, [.. realBars.Select(c => (c.midX, c.midY))], best.midX, best.midY);
        return (true, rx, ry);
    }

    private static void SaveEnemyDebug(Bitmap screen, List<(int x, int y)> allBars, int bestX, int bestY)
    {
        using var dbg = new Bitmap(screen);
        using var g = Graphics.FromImage(dbg);
        // Vẽ tất cả candidate (vàng)
        foreach (var (x, y) in allBars)
            g.DrawEllipse(Pens.Yellow, x - 8, y - 8, 16, 16);
        // Vẽ best (đỏ, to hơn)
        g.DrawEllipse(Pens.Red, bestX - 14, bestY - 14, 28, 28);
        g.DrawLine(Pens.Red, bestX - 14, bestY, bestX + 14, bestY);
        g.DrawLine(Pens.Red, bestX, bestY - 14, bestX, bestY + 14);
        SaveDebugScreen(dbg, "enemy_detect.png");
    }

    // Detect chòi/chướng ngại vật: trả về tọa độ ratio của obstacle (center HP bar)
    // null nếu không tìm thấy
    private static (double x, double y)? FindObstacleOnScreen(Bitmap screen)
    {
        var pixels = LockPixels(screen, out int stride);
        int w = screen.Width, h = screen.Height;
        int x1 = (int)(w * 0.10), x2 = (int)(w * 0.90);
        int y1 = (int)(h * 0.08), y2 = (int)(h * 0.25);
        for (int y = y1; y < y2; y++)
        {
            int streak = 0, streakStart = 0, rowBase = y * stride;
            for (int x = x1; x < x2; x++)
            {
                int i = rowBase + x * 4;
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (r > 160 && g < 60 && b < 60)
                {
                    if (streak == 0) streakStart = x;
                    streak++;
                    if (streak >= 45)
                    {
                        double cx = (streakStart + x) / 2.0 / w;
                        // Tap thấp hơn HP bar một chút để nhắm vào thân obstacle
                        double cy = Math.Min((y + 60.0) / h, 0.85);
                        return (cx, cy);
                    }
                }
                else streak = 0;
            }
        }
        return null;
    }

    private bool IsHpLow(Bitmap screen)
    {
        var pixels = LockPixels(screen, out int stride);
        int w = screen.Width, h = screen.Height;

        // Hero HP bar = vivid green streak trực tiếp trên đầu hero (camera theo hero ≈ center)
        // Cỏ: xanh mờ hơn, r và b cao hơn → dùng g>140 && r<80 && b<80 để phân biệt
        int x1 = (int)(w * 0.28), x2 = (int)(w * 0.72);
        int y1 = (int)(h * 0.28), y2 = (int)(h * 0.50);

        int bestStreak = 0;
        for (int y = y1; y < y2; y++)
        {
            int streak = 0, rowBase = y * stride;
            for (int x = x1; x < x2; x++)
            {
                int i = rowBase + x * 4;
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (g > 140 && r < 80 && b < 80) streak++;
                else { if (streak > bestStreak) bestStreak = streak; streak = 0; }
            }
            if (streak > bestStreak) bestStreak = streak;
        }

        // Full bar ≈ 140px; <30px ≈ dưới 20% HP → retreat
        Log($"Hero HP green streak={bestStreak}px → hpLow={bestStreak > 0 && bestStreak < 30}");
        return bestStreak > 0 && bestStreak < 30;
    }

    // ── Battle entry ──────────────────────────────────────────────────────────

    private async Task<bool> EnterBattleAsync(CancellationToken ct)
    {
        Log("Not in battle — attempting to enter...");

        Log($"Tapping community icon at ({_cfg.CommunityIconXRatio}, {_cfg.CommunityIconYRatio}).");
        _adb.TapRatio(_cfg.CommunityIconXRatio, _cfg.CommunityIconYRatio);
        await Task.Delay(2000, ct);

        double[] swipeStarts = { 0.85, 0.75 };
        double[] swipeEnds   = { 0.35, 0.30 };
        for (int i = 0; i < swipeStarts.Length; i++)
        {
            Log($"Swipe #{i + 1} to Favorites...");
            _adb.SwipeRatio(swipeStarts[i], _cfg.CommunitySwipeYRatio,
                            swipeEnds[i],   _cfg.CommunitySwipeYRatio, 500);
            await Task.Delay(600, ct);
        }

        using var cardTpl    = ImageMatcher.LoadTemplate("favorites_card.png");
        using var openBtnTpl = ImageMatcher.LoadTemplate("open_btn.png");

        if (cardTpl == null || openBtnTpl == null)
        {
            Log("Missing templates — tapping hardcoded Favorites OPEN.");
            _adb.TapRatio(_cfg.FavoritesOpenXRatio, _cfg.FavoritesOpenYRatio);
        }
        else
        {
            var s = ScreenCapture.CaptureWindow(_cfg.WindowTitle);
            if (s == null) return false;

            var (cardFound, cardPt, cardConf) = ImageMatcher.FindTemplate(s, cardTpl, 0.60);
            SaveDebugScreen(s, "01_after_swipe.png");
            Log($"Favorites card: conf={cardConf:F2} found={cardFound}");

            var openPositions = ImageMatcher.FindAllTemplates(s, openBtnTpl, 0.75);
            int bsw = s.Width, bsh = s.Height;
            s.Dispose();

            if (openPositions.Count == 0)
            {
                Log("No OPEN button found — tapping hardcoded Favorites OPEN.");
                _adb.TapRatio(_cfg.FavoritesOpenXRatio, _cfg.FavoritesOpenYRatio);
            }
            else
            {
                Point best = cardFound
                    ? openPositions.OrderBy(p => Math.Pow(p.X - cardPt.X, 2) + Math.Pow(p.Y - cardPt.Y, 2)).First()
                    : openPositions.OrderByDescending(p => p.X).First();
                Log($"Tapping OPEN at {best}.");
                _adb.TapScaled(best.X, best.Y, bsw, bsh);
            }
        }

        Log("Waiting 4s for player list...");
        await Task.Delay(4000, ct);
        await TapFirstAttackButtonAsync(ct);

        Log("Waiting 2s for Prepare screen...");
        await Task.Delay(2000, ct);
        Log("Tapping ATTACK! button.");
        _adb.TapRatio(_cfg.AttackButtonXRatio, _cfg.AttackButtonYRatio);
        return true;
    }

    private async Task TapFirstAttackButtonAsync(CancellationToken ct)
    {
        var screen = ScreenCapture.CaptureWindow(_cfg.WindowTitle);
        if (screen != null)
        {
            SaveDebugScreen(screen, "04_before_attack_tap.png");
            screen.Dispose();
        }

        Log("Tapping attack button at (0.875, 0.280).");
        _adb.TapRatio(0.875, 0.280);
        await Task.Delay(1000, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Lock toàn bộ bitmap thành byte array BGRA — nhanh hơn GetPixel hàng trăm lần.
    private static byte[] LockPixels(Bitmap bmp, out int stride)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        stride = data.Stride;
        byte[] buf = new byte[bmp.Height * stride];
        Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(data);
        return buf;
    }

    private void Log(string msg) => _log?.Invoke($"[Battle] {msg}");

    private static void SaveDebugScreen(Bitmap screen, string fileName)
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Debug");
        Directory.CreateDirectory(dir);
        screen.Save(Path.Combine(dir, fileName));
    }
}
