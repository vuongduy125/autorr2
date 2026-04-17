using System.Drawing;
using RR2Bot.Core;
using RR2Bot.Models;

namespace RR2Bot.Modules;

/// <summary>
/// Battle automation: di chuyển hero qua joystick swipe, dùng spell, triệu quân.
/// </summary>
public class BattleManager
{
    private readonly AdbController _adb;
    private readonly BotConfig _cfg;
    private Action<string>? _log;

    // Template tên — để vào thư mục Templates/
    private const string TplBattleUI   = "battle_ui.png";     // HUD chỉ xuất hiện trong battle
    private const string TplSpellReady = "spell_ready.png";   // spell đã cool down xong
    private const string TplHeroHealth = "hero_hp_low.png";   // máu hero thấp (tùy chọn)
    private const string TplVictory    = "victory.png";
    private const string TplDefeat     = "defeat.png";

    private readonly Random _rng = new();

    public BattleManager(AdbController adb, BotConfig cfg, Action<string>? log = null)
    {
        _adb = adb;
        _cfg = cfg;
        _log = log;
    }

    // ── Main loop ────────────────────────────────────────────────────────────

    private enum BotState { AtBase, EnteringBattle, InBattle }
    private BotState _state = BotState.AtBase;
    private DateTime _enteringBattleAt;

    // Waypoint tracking
    private int      _wpIndex    = 0;
    private DateTime _wpDeadline = DateTime.MinValue;

    // HUD exit hysteresis — require 2 consecutive misses before leaving InBattle
    private int _hudMissCount = 0;
    private DateTime _lastMoveTime = DateTime.MinValue;

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
                            if (_hudMissCount < 2)
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

                        // Kiểm tra Victory/Defeat trước
                        if (IsBattleOver(screen))
                        {
                            Log("Victory/Defeat detected → returning to base.");
                            screen.Dispose();
                            _state = BotState.AtBase;
                            await Task.Delay(3000, ct);
                            break;
                        }

                        bool enemyNearby = HasEnemyHpBar(screen);
                        bool hpLow = IsHpLow(screen);
                        Log($"Enemy nearby: {enemyNearby} | HP low: {hpLow}");

                        if (hpLow)
                        {
                            Log("HP low! Spamming spells.");
                            UseReadySpells(screen);
                            UseReadySpells(screen);
                        }
                        else if (enemyNearby)
                        {
                            Log("Fighting enemy.");
                            UseReadySpells(screen);
                        }
                        else
                        {
                            Log("Path clear → moving forward.");
                            MoveHeroByWaypoint();
                        }

                        SummonTroops();
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

    // ── Hero movement ────────────────────────────────────────────────────────

    /// <summary>
    /// Di chuyển hero theo hướng chỉ định bằng cách swipe joystick.
    /// </summary>
    private void MoveHeroByWaypoint()
    {
        _adb.TapRatio(_cfg.HeroTargetXRatio, _cfg.HeroTargetYRatio);
        Thread.Sleep(100);
        _adb.TapRatio(_cfg.HeroTargetXRatio, _cfg.HeroTargetYRatio);
    }

    public void MoveHero(HeroDirection dir = HeroDirection.Forward)
    {
        double fromX = _cfg.HeroMoveFromX;
        double toX   = _cfg.HeroMoveToX;
        double y     = _cfg.HeroMoveY;

        var (x1, y1, x2, y2) = dir switch
        {
            HeroDirection.Forward  => (fromX, y,    toX,   y),
            HeroDirection.Backward => (toX,   y,    fromX, y),
            HeroDirection.Up       => (fromX, y,    fromX, y - 0.15),
            HeroDirection.Down     => (fromX, y,    fromX, y + 0.15),
            HeroDirection.UpRight  => (fromX, y,    toX,   y - 0.10),
            HeroDirection.UpLeft   => (toX,   y,    fromX, y - 0.10),
            _ => (fromX, y, toX, y),
        };

        _adb.SwipeRatio(x1, y1, x2, y2, _cfg.HeroMoveDurationMs);
    }

    public void MoveHeroTo(double targetXRatio, double targetYRatio)
    {
        _adb.SwipeRatio(_cfg.JoystickXRatio, _cfg.JoystickYRatio, targetXRatio, targetYRatio, 200);
    }

    // ── Spell ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Dùng tất cả spell đang ready (dựa vào template matching hoặc click mù).
    /// </summary>
    public void UseReadySpells(Bitmap? screen = null)
    {
        foreach (var (x, y) in _cfg.SpellButtonRatios)
        {
            if (screen != null)
            {
                using var spellTpl = ImageMatcher.LoadTemplate(TplSpellReady);
                if (spellTpl != null)
                {
                    var (ready, _, _) = ImageMatcher.FindTemplate(screen, spellTpl, 0.75);
                    if (!ready) continue;
                }
            }

            _adb.TapRatio(x, y);
            Thread.Sleep(80);
        }
    }

    /// <summary>
    /// Dùng spell theo index (0-based).
    /// </summary>
    public void UseSpell(int index)
    {
        if (index < 0 || index >= _cfg.SpellButtonRatios.Length) return;
        var (x, y) = _cfg.SpellButtonRatios[index];
        _adb.TapRatio(x, y);
    }

    // ── Troop summon ──────────────────────────────────────────────────────────

    /// <summary>
    /// Triệu tất cả loại quân (click lần lượt các nút troop).
    /// </summary>
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

    // ── Detection ─────────────────────────────────────────────────────────────

    private bool IsBattleHudVisible(Bitmap? screen = null)
    {
        bool dispose = screen == null;
        var bmp = screen ?? ScreenCapture.CaptureWindow(_cfg.WindowTitle);
        if (bmp == null) return false;

        // Indicator 1: thanh mana xanh dương ở bottom
        int blueCount = 0;
        foreach (var xr in new[] { 0.05, 0.10, 0.15, 0.20, 0.25, 0.30 })
        {
            int px = (int)(bmp.Width  * xr);
            int py = (int)(bmp.Height * 0.94);
            if (px >= bmp.Width || py >= bmp.Height) continue;
            var c = bmp.GetPixel(px, py);
            if (c.B > 100 && c.R < 100 && c.B > c.R + 60) blueCount++;
        }
        bool manaVisible = blueCount >= 3;

        // Indicator 2: nút Pause (II) góc trên-trái — vòng tròn xanh dương
        bool pauseVisible = false;
        {
            int px = (int)(bmp.Width  * 0.043);
            int py = (int)(bmp.Height * 0.267);
            if (px < bmp.Width && py < bmp.Height)
            {
                var c = bmp.GetPixel(px, py);
                pauseVisible = c.B > 120 && c.R < 150 && c.B > c.G;
            }
        }

        bool inBattle = blueCount >= 2 || pauseVisible;
        Log($"Battle detect — mana:{blueCount}/6 pause:{pauseVisible} → inBattle={inBattle}");

        if (dispose) bmp.Dispose();
        return inBattle;
    }

    private bool IsInBattle(Bitmap screen)
    {
        // Nếu thấy community icon = đang ở base = chưa vào battle
        using var communityTpl = ImageMatcher.LoadTemplate("community_icon.png");
        if (communityTpl != null)
        {
            var (atBase, _, _) = ImageMatcher.FindTemplate(screen, communityTpl, 0.75);
            if (atBase) return false;
        }

        // Fallback: dùng battle_ui template nếu có
        using var battleTpl = ImageMatcher.LoadTemplate(TplBattleUI);
        if (battleTpl == null) return true;
        var (inBattle, _, _) = ImageMatcher.FindTemplate(screen, battleTpl, 0.75);
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

    // ── Battle entry ──────────────────────────────────────────────────────────

    private async Task<bool> EnterBattleAsync(CancellationToken ct)
    {
        Log("Not in battle — attempting to enter...");

        // Bước 1: tap icon community từ màn base
        var screen = ScreenCapture.CaptureWindow(_cfg.WindowTitle);
        if (screen == null) return false;

        screen.Dispose();
        Log($"Tapping community icon at ratio ({_cfg.CommunityIconXRatio}, {_cfg.CommunityIconYRatio}).");
        _adb.TapRatio(_cfg.CommunityIconXRatio, _cfg.CommunityIconYRatio);
        await Task.Delay(1500, ct);

        // Chờ community screen mở
        await Task.Delay(500, ct);

        // Bước 2: swipe đến Favorites
        double[] swipeStarts = { 0.85, 0.75 };
        double[] swipeEnds   = { 0.35, 0.30 };
        for (int i = 0; i < swipeStarts.Length; i++)
        {
            Log($"Swipe #{i + 1} to Favorites...");
            _adb.SwipeRatio(swipeStarts[i], _cfg.CommunitySwipeYRatio,
                            swipeEnds[i],   _cfg.CommunitySwipeYRatio, 500);
            await Task.Delay(600, ct);
        }

        // Bước 3: tìm và tap OPEN của Favorites
        using var cardTpl = ImageMatcher.LoadTemplate("favorites_card.png");
        using var openBtnTpl = ImageMatcher.LoadTemplate("open_btn.png");

        if (cardTpl == null || openBtnTpl == null)
        {
            Log("Missing favorites_card.png or open_btn.png — tapping hardcoded Favorites OPEN.");
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

        // Bước 4: chờ Favorite Players load → tap attack
        Log("Waiting 4s for player list...");
        await Task.Delay(4000, ct);
        await TapFirstAttackButtonAsync(ct);

        // Bước 5: Prepare for Battle → tap ATTACK!
        Log("Waiting 2s for Prepare screen...");
        await Task.Delay(2000, ct);
        Log("Tapping ATTACK! button.");
        _adb.TapRatio(0.79, 0.82);
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

    private bool HasEnemyHpBar(Bitmap screen)
    {
        // Thanh HP đỏ = địch (quân đỏ + cổng/barricade)
        int x1 = (int)(screen.Width  * 0.15), x2 = (int)(screen.Width  * 0.85);
        int y1 = (int)(screen.Height * 0.10), y2 = (int)(screen.Height * 0.55);

        for (int y = y1; y < y2; y += 3)
        {
            int streak = 0;
            for (int x = x1; x < x2; x++)
            {
                var c = screen.GetPixel(x, y);
                if (c.R > 170 && c.G < 90 && c.B < 90)
                    streak++;
                else
                    streak = 0;
                if (streak >= 8) return true;
            }
        }
        return false;
    }

    private bool IsHpLow(Bitmap screen)
    {
        // Scan toàn bộ y=10%-70% để tìm thanh HP (theo sau hero di chuyển)
        // HP bar đặc trưng: G cao, G >> R, G >> B (xanh lá thuần)
        double[] xSamples = { 0.27, 0.31, 0.35, 0.39, 0.43, 0.47 };
        int yStart = (int)(screen.Height * 0.18);
        int yEnd   = (int)(screen.Height * 0.55); // bỏ bottom 45% tránh nhầm mana bar

        int bestPy    = -1;
        int bestGreen = 0;

        for (int py = yStart; py <= yEnd; py += 4)
        {
            int green = 0;
            foreach (var xr in xSamples)
            {
                int px = (int)(screen.Width * xr);
                if (px >= screen.Width || py >= screen.Height) continue;
                var c = screen.GetPixel(px, py);
                if (c.G > 70 && c.G > c.R + 25 && c.G > c.B + 25) green++;
            }
            if (green > bestGreen) { bestGreen = green; bestPy = py; }
        }

        if (bestGreen == 0)
        {
            Log("HP bar not found → assuming OK");
            return false;
        }

        bool hpOk = bestGreen >= 3;
        Log($"HP bar at y={bestPy}({bestPy * 1.0 / screen.Height:F2}) green={bestGreen}/6 → hpOk={hpOk}");
        return !hpOk;
    }

    private void Log(string msg) => _log?.Invoke($"[Battle] {msg}");

    private static void SaveDebugScreen(Bitmap screen, string fileName)
    {
        var dir  = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Debug");
        Directory.CreateDirectory(dir);
        screen.Save(Path.Combine(dir, fileName));
    }
}

