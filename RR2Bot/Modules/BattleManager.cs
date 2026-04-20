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
    private readonly Action<string>? _log;

    private const string TplSpellReady    = "spell_ready.png";
    private const string TplVictory       = "victory.png";
    private const string TplDefeat        = "defeat.png";
    private const string TplFavoritesCard = "favorites_card.png";
    private const string TplOpenBtn       = "open_btn.png";
    private const string TplBaseCoin             = "base_coin_gold.png";
    private const string TplBaseSword            = "base_sword_icon.png";
    private const string TplCommunityTitle       = "community_title.png";
    private const string TplFavoritePlayersTitle = "favorite_players_title.png";
    private const string TplPrepareBattle        = "prepare_battle_title.png";
    private const string TplPrepareAttackBtn     = "prepare_battle_attack.png";
    private const string TplNotEnoughFood        = "not_enough_food.png";
    private const string TplNotEnoughFoodCollect = "not_enough_food_collect.png";
    private const string TplChamberFortune       = "chamber_fortune.png";
    private const string TplFortuneChest         = "fortune_chest.png";
    private const string TplFortuneGetIt         = "fortune_get_it.png";
    private const string TplFortuneGiveUp        = "fortune_give_up.png";
    private const string TplFortuneTapContinue   = "fortune_tap_to_continue.png";

    public BattleManager(AdbController adb, BotConfig cfg, Action<string>? log = null)
    {
        _adb = adb;
        _cfg = cfg;
        _log = log;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mỗi vòng loop: detect screen → confirm → act.
    // Không giả định đang ở đâu — luôn đọc màn hình trước.
    // ─────────────────────────────────────────────────────────────────────────

    private enum ScreenState
    {
        InBattle,            // mana bar visible
        BattleResult,        // victory/defeat screen
        PrepareScreen,       // "Prepare for Battle" — nút ATTACK! orange
        PlayerListScreen,    // danh sách người chơi để tấn công
        FavoritesScreen,     // tab Favorites với OPEN button
        CommunityScreen,     // community overlay đang mở
        HeroDead,            // hero chết → retreat
        NotEnoughFood,       // popup not enough food → collect
        ChambersOfFortune,   // chọn rương
        FortuneItemPopup,    // popup NEW ITEM → GET IT
        FortuneTapToContinue,// tap to continue sau khi nhận item
        AtBase,              // base confirmed
        Unknown,             // không nhận ra — chờ
    }

    private DateTime _battleStartAt;
    private DateTime _lastAtBaseTap        = DateTime.MinValue;
    private DateTime _lastCommunityTap     = DateTime.MinValue;
    private static readonly TimeSpan AtBaseCooldown      = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CommunityOpenWindow = TimeSpan.FromSeconds(12);

    // ── Main loop ─────────────────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        Log("BattleManager started.");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var screen = ScreenCapture.CaptureWindow(_cfg.WindowTitle);
                if (screen == null) { await Task.Delay(1000, ct); continue; }

                var state = DetectScreen(screen);
                Log($"Screen: {state}");
                await ActAsync(state, screen, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"[Error] {ex.Message}"); }
        }
        Log("BattleManager stopped.");
    }

    // ── Detect screen ─────────────────────────────────────────────────────────

    private ScreenState DetectScreen(Bitmap bmp)
    {
        if (IsVictoryOrDefeat(bmp))  return ScreenState.BattleResult;
        if (IsInBattle(bmp))         return ScreenState.InBattle;
        if (IsPrepareScreen(bmp))    return ScreenState.PrepareScreen;
        if (IsPlayerListScreen(bmp)) return ScreenState.PlayerListScreen;
        if (IsFavoritesScreen(bmp))  return ScreenState.FavoritesScreen;
        if (IsCommunityScreen(bmp))  return ScreenState.CommunityScreen;
        if (IsHeroDead(bmp))             return ScreenState.HeroDead;
        if (IsNotEnoughFood(bmp))        return ScreenState.NotEnoughFood;
        if (IsFortuneTapToContinue(bmp)) return ScreenState.FortuneTapToContinue;
        if (IsFortuneItemPopup(bmp))     return ScreenState.FortuneItemPopup;
        if (IsChambersOfFortune(bmp))    return ScreenState.ChambersOfFortune;
        if (IsAtBase(bmp))               return ScreenState.AtBase;
        return ScreenState.Unknown;
    }

    // ── Act per screen ────────────────────────────────────────────────────────

    private async Task ActAsync(ScreenState state, Bitmap screen, CancellationToken ct)
    {
        switch (state)
        {
            case ScreenState.InBattle:
                await DoBattleAsync(screen, ct);
                break;

            case ScreenState.BattleResult:
                Log("Battle result → tapping CONTINUE.");
                TapTemplate(screen, "continue_btn.png");
                await Task.Delay(2000, ct);
                break;

            case ScreenState.PrepareScreen:
                Log("Prepare screen confirmed → tapping ATTACK!");
                TapPrepareAttack(screen);
                _battleStartAt = DateTime.Now;
                await Task.Delay(2000, ct);
                break;

            case ScreenState.PlayerListScreen:
                Log("Player list confirmed → finding attack button.");
                TapFirstAttackButton(screen);
                await Task.Delay(2500, ct);
                break;

            case ScreenState.FavoritesScreen:
                Log("Favorites confirmed → tapping OPEN.");
                TapFavoritesOpen(screen);
                await Task.Delay(5000, ct); // chờ player list load
                break;

            case ScreenState.CommunityScreen:
                Log("Community screen → swiping to Favorites.");
                _adb.SwipeRatio(0.95, _cfg.CommunitySwipeYRatio, 0.05, _cfg.CommunitySwipeYRatio, 900);
                await Task.Delay(1000, ct);
                break;

            case ScreenState.HeroDead:
                Log("Hero dead → tapping RETREAT.");
                _adb.TapRatio(0.615, 0.788);
                await Task.Delay(2000, ct);
                break;

            case ScreenState.NotEnoughFood:
                Log("Not enough food → tapping COLLECT.");
                TapTemplate(screen, TplNotEnoughFoodCollect);
                await Task.Delay(2000, ct);
                break;

            case ScreenState.FortuneTapToContinue:
                Log("Fortune: tap to continue.");
                TapTemplate(screen, TplFortuneTapContinue);
                await Task.Delay(1500, ct);
                break;

            case ScreenState.FortuneItemPopup:
                Log("Fortune: tapping GET IT / GIVE UP.");
                TapTemplate(screen, TplFortuneGetIt, TplFortuneGiveUp);
                await Task.Delay(1500, ct);
                break;

            case ScreenState.ChambersOfFortune:
                Log("Fortune: picking a chest.");
                TapFirstChest(screen);
                await Task.Delay(2000, ct);
                break;

            case ScreenState.Unknown:
                Log("Unknown screen — waiting.");
                await Task.Delay(1500, ct);
                break;

            case ScreenState.AtBase:
            default:
                if (DateTime.Now - _lastAtBaseTap >= AtBaseCooldown)
                {
                    Log("At base → tapping community icon.");
                    _adb.TapRatio(_cfg.CommunityIconXRatio, _cfg.CommunityIconYRatio);
                    _lastAtBaseTap    = DateTime.Now;
                    _lastCommunityTap = DateTime.Now;
                }
                else
                {
                    Log("At base → waiting cooldown.");
                }
                await Task.Delay(2000, ct);
                break;
        }
    }

    // ── Screen detectors ──────────────────────────────────────────────────────

    private bool IsInBattle(Bitmap bmp)
    {
        var px = LockPixels(bmp, out int stride);

        // Mana bar: blue pixels tại y=94%
        int blueCount = 0;
        foreach (var xr in new[] { 0.05, 0.10, 0.15, 0.20, 0.25, 0.30 })
        {
            int x = (int)(bmp.Width * xr), y = (int)(bmp.Height * 0.94);
            if (x >= bmp.Width || y >= bmp.Height) continue;
            int i = y * stride + x * 4;
            byte b = px[i], r = px[i + 2];
            if (b > 100 && r < 100 && b > r + 60) blueCount++;
        }

        // Nút Pause góc trên-trái
        bool pause = false;
        {
            int x = (int)(bmp.Width * 0.043), y = (int)(bmp.Height * 0.267);
            if (x < bmp.Width && y < bmp.Height)
            {
                int i = y * stride + x * 4;
                pause = px[i] > 120 && px[i + 2] < 150 && px[i] > px[i + 1];
            }
        }

        // Sword icon (troop button) bottom-left
        bool sword = false;
        {
            int x = (int)(bmp.Width * 0.069), y = (int)(bmp.Height * 0.828);
            if (x < bmp.Width && y < bmp.Height)
            {
                int i = y * stride + x * 4;
                sword = px[i] > 100 && px[i] > px[i + 2] + 30 && px[i] > px[i + 1] + 20;
            }
        }

        bool result = blueCount >= 1 || pause || sword;
        Log($"IsInBattle — mana:{blueCount}/6 pause:{pause} sword:{sword} → {result}");
        return result;
    }

    private bool IsVictoryOrDefeat(Bitmap bmp)
    {
        // Nền đỏ bên phải = victory screen (R>180, G<60, B<60)
        var px = LockPixels(bmp, out int stride);
        int redCount = 0;
        foreach (var (xr, yr) in new[] { (0.65, 0.20), (0.75, 0.35), (0.80, 0.50), (0.70, 0.60), (0.60, 0.30) })
        {
            int x = (int)(bmp.Width * xr), y = (int)(bmp.Height * yr);
            if (x >= bmp.Width || y >= bmp.Height) continue;
            int i = y * stride + x * 4;
            byte b = px[i], g = px[i + 1], r = px[i + 2];
            if (r > 180 && g < 60 && b < 60) redCount++;
        }
        if (redCount >= 3) { Log($"VictoryScreen: red background detected ({redCount}/5)"); return true; }

        foreach (var name in new[] { TplVictory, TplDefeat, "lose_3_crown.png" })
        {
            using var tpl = ImageMatcher.LoadTemplate(name);
            if (tpl == null) continue;
            var (found, pt, conf) = ImageMatcher.FindTemplate(bmp, tpl, 0.75);
            Log($"IsVictoryOrDefeat {name}: conf={conf:F2} found={found}");
            if (!found) continue;
            _adb.TapScaled(pt.X, pt.Y, bmp.Width, bmp.Height);
            return true;
        }
        return false;
    }

    private bool IsPrepareScreen(Bitmap bmp)
    {
        using var tpl = ImageMatcher.LoadTemplate(TplPrepareAttackBtn);
        if (tpl == null)
        {
            using var tpl2 = ImageMatcher.LoadTemplate(TplPrepareBattle);
            if (tpl2 == null) return false;
            var (f2, _, c2) = ImageMatcher.FindTemplate(bmp, tpl2, 0.75);
            Log($"IsPrepareScreen (title): conf={c2:F2} found={f2}");
            return f2;
        }
        var (found, _, conf) = ImageMatcher.FindTemplate(bmp, tpl, 0.75);
        Log($"IsPrepareScreen (attack): conf={conf:F2} found={found}");
        return found;
    }

    private bool IsPlayerListScreen(Bitmap bmp)
    {
        using var tpl = ImageMatcher.LoadTemplate(TplFavoritePlayersTitle);
        if (tpl == null) return false;
        var (found, _, conf) = ImageMatcher.FindTemplate(bmp, tpl, 0.75);
        Log($"IsPlayerListScreen: conf={conf:F2} found={found}");
        return found;
    }

    private bool IsFavoritesScreen(Bitmap bmp)
    {
        if (!IsCommunityOpen(bmp)) return false;
        using var tpl = ImageMatcher.LoadTemplate(TplFavoritesCard);
        if (tpl == null) return false;
        var (found, _, conf) = ImageMatcher.FindTemplate(bmp, tpl, 0.78);
        Log($"IsFavoritesScreen: conf={conf:F2} found={found}");
        return found;
    }

    private bool IsAtBase(Bitmap bmp)
    {
        bool coinFound = false, swordFound = false;
        using var coinTpl = ImageMatcher.LoadTemplate(TplBaseCoin);
        if (coinTpl != null)
        {
            var (f, _, conf) = ImageMatcher.FindTemplate(bmp, coinTpl, 0.70);
            coinFound = f;
            Log($"IsAtBase coin: conf={conf:F2} found={f}");
        }
        using var swordTpl = ImageMatcher.LoadTemplate(TplBaseSword);
        if (swordTpl != null)
        {
            var (f, _, conf) = ImageMatcher.FindTemplate(bmp, swordTpl, 0.70);
            swordFound = f;
            Log($"IsAtBase sword: conf={conf:F2} found={f}");
        }
        return coinFound && swordFound;
    }

    private bool IsCommunityOpen(Bitmap bmp)
    {
        using var tpl = ImageMatcher.LoadTemplate(TplCommunityTitle);
        if (tpl == null) return false;
        var (found, _, conf) = ImageMatcher.FindTemplate(bmp, tpl, 0.75);
        Log($"IsCommunityOpen: conf={conf:F2} found={found}");
        return found;
    }

    private bool IsCommunityScreen(Bitmap bmp)
        => IsCommunityOpen(bmp);

    // ── Tap OPEN button trên Favorites ────────────────────────────────────────

    private void TapPrepareAttack(Bitmap screen)
    {
        using var tpl = ImageMatcher.LoadTemplate(TplPrepareAttackBtn);
        if (tpl != null)
        {
            var (found, pt, conf) = ImageMatcher.FindTemplate(screen, tpl, 0.75);
            if (found) { Log($"ATTACK! at {pt} conf={conf:F2} → tapping."); _adb.TapScaled(pt.X, pt.Y, screen.Width, screen.Height); return; }
        }
        Log("ATTACK! not found → tapping hardcoded.");
        _adb.TapRatio(_cfg.AttackButtonXRatio, _cfg.AttackButtonYRatio);
    }

    private void TapFirstAttackButton(Bitmap screen)
    {
        using var tpl = ImageMatcher.LoadTemplate("attack_btn.png");
        if (tpl == null) { Log("attack_btn.png missing."); return; }

        var (found, pt, conf) = ImageMatcher.FindTemplate(screen, tpl, 0.72);
        Log($"attack_btn: conf={conf:F2} found={found} at {pt}");

        if (!found)
        {
            // Kiểm tra có phải bị disable không
            using var disTpl = ImageMatcher.LoadTemplate("attack_btn_disable.png");
            if (disTpl != null)
            {
                var (disFound, _, disConf) = ImageMatcher.FindTemplate(screen, disTpl, 0.72);
                Log($"attack_btn_disable: conf={disConf:F2} found={disFound}");
                if (disFound) { Log("Attack disabled → going back."); _adb.TapRatio(0.877, 0.120); return; }
            }
            Log("Attack button not found — skipping.");
            return;
        }

        _adb.TapScaled(pt.X, pt.Y, screen.Width, screen.Height);
    }

    private void TapFavoritesOpen(Bitmap screen)
    {
        using var openTpl = ImageMatcher.LoadTemplate(TplOpenBtn);
        using var cardTpl = ImageMatcher.LoadTemplate(TplFavoritesCard);

        if (openTpl == null)
        {
            Log("No open_btn.png → tapping hardcoded.");
            _adb.TapRatio(_cfg.FavoritesOpenXRatio, _cfg.FavoritesOpenYRatio);
            return;
        }

        var opens = ImageMatcher.FindAllTemplates(screen, openTpl, 0.75);
        if (opens.Count == 0)
        {
            Log("OPEN button not found → tapping hardcoded.");
            _adb.TapRatio(_cfg.FavoritesOpenXRatio, _cfg.FavoritesOpenYRatio);
            return;
        }

        Point best;
        if (cardTpl != null)
        {
            var (cardFound, cardPt, _) = ImageMatcher.FindTemplate(screen, cardTpl, 0.55);
            best = cardFound
                ? opens.OrderBy(p => Math.Pow(p.X - cardPt.X, 2) + Math.Pow(p.Y - cardPt.Y, 2)).First()
                : opens.OrderByDescending(p => p.X).First();
        }
        else
        {
            best = opens.OrderByDescending(p => p.X).First();
        }

        Log($"Tapping OPEN at {best}.");
        _adb.TapScaled(best.X, best.Y, screen.Width, screen.Height);
    }

    // ── In-battle logic ───────────────────────────────────────────────────────

    private async Task DoBattleAsync(Bitmap screen, CancellationToken ct)
    {
        SummonTroops();

        bool hpLow   = IsHpLow(screen);
        bool inGrace = (DateTime.Now - _battleStartAt).TotalSeconds < 5;
        var (enemyFound, ex, ey) = inGrace ? (false, 0.0, 0.0) : FindEnemyHpBar(screen);

        if (hpLow)
        {
            Log("HP low! Retreating.");
            UseReadySpells(screen);
            _adb.TapRatio(0.39, 0.64);
        }
        else if (enemyFound)
        {
            Log($"Enemy at ({ex:F2},{ey:F2}) → moving in.");
            MoveToward(ex, ey);
            UseReadySpells(screen);
        }
        else
        {
            double jx = _cfg.HeroTargetXRatio + (Random.Shared.NextDouble() - 0.5) * 0.08;
            double jy = _cfg.HeroTargetYRatio + (Random.Shared.NextDouble() - 0.5) * 0.08;
            Log($"Clear{(inGrace ? " (grace)" : "")} → moving forward ({jx:F2},{jy:F2}).");
            MoveToward(jx, jy);
        }

        await Task.Delay(_cfg.BattleLoopIntervalMs, ct);
    }

    // ── Spell / Troop ─────────────────────────────────────────────────────────

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

    public void SummonTroops()
    {
        foreach (var (x, y) in _cfg.TroopButtonRatios)
        {
            _adb.TapRatio(x, y);
            Thread.Sleep(60);
        }
    }

    // ── Hero movement ─────────────────────────────────────────────────────────

    private void MoveToward(double targetX, double targetY)
        => _adb.LongPress((int)(targetX * _adb.ScreenWidth), (int)(targetY * _adb.ScreenHeight), 1500);

    // ── Enemy HP bar (pixel scan) ─────────────────────────────────────────────

    private (bool found, double x, double y) FindEnemyHpBar(Bitmap screen)
    {
        var px = LockPixels(screen, out int stride);
        int w = screen.Width, h = screen.Height;
        int x1 = (int)(w * 0.10), x2 = (int)(w * 0.90);
        int y1 = (int)(h * 0.10), y2 = (int)(h * 0.60);

        var candidates = new List<(int midX, int midY, int streak)>();
        for (int y = y1; y < y2; y++)
        {
            int streak = 0, streakStart = 0, rowBase = y * stride;
            for (int x = x1; x < x2; x++)
            {
                int i = rowBase + x * 4;
                byte b = px[i], g = px[i + 1], r = px[i + 2];
                if (r > 160 && g < 55 && b < 55) { if (streak == 0) streakStart = x; streak++; }
                else { if (streak >= 8) candidates.Add(((streakStart + x) / 2, y, streak)); streak = 0; }
            }
            if (streak >= 8) candidates.Add(((streakStart + x2) / 2, y, streak));
        }

        if (candidates.Count == 0) { Log("Enemy: not found"); return (false, 0, 0); }

        // Lọc spell effects (dày dọc > 8px)
        var bars = new List<(int midX, int midY, int streak)>();
        foreach (var c in candidates)
        {
            int contig = 1;
            for (int dy = -1; dy >= -10; dy--)
            {
                int sy = c.midY + dy; if (sy < 0) break;
                int ci = sy * stride + c.midX * 4;
                if (px[ci + 2] > 150 && px[ci + 1] < 80 && px[ci] < 80) contig++; else break;
            }
            for (int dy = 1; dy <= 10; dy++)
            {
                int sy = c.midY + dy; if (sy >= h) break;
                int ci = sy * stride + c.midX * 4;
                if (px[ci + 2] > 150 && px[ci + 1] < 80 && px[ci] < 80) contig++; else break;
            }
            if (contig <= 8) bars.Add(c);
        }

        if (bars.Count == 0) { Log($"Enemy: {candidates.Count} candidates all filtered"); return (false, 0, 0); }

        double cx = w * 0.5, cy = h * 0.5;
        var best = bars.MinBy(c => Math.Pow(c.midX - cx, 2) + Math.Pow(c.midY - cy, 2));
        double rx = best.midX / (double)w;
        double ry = Math.Min((best.midY + 50.0) / h, 0.85);
        Log($"Enemy: found streak={best.streak} count={bars.Count} at ({rx:F2},{ry:F2})");
        return (true, rx, ry);
    }

    // ── Hero HP (pixel scan) ──────────────────────────────────────────────────

    private bool IsHpLow(Bitmap screen)
    {
        var px = LockPixels(screen, out int stride);
        int w = screen.Width, h = screen.Height;
        int x1 = (int)(w * 0.28), x2 = (int)(w * 0.72);
        int y1 = (int)(h * 0.18), y2 = (int)(h * 0.50);

        int best = 0;
        for (int y = y1; y < y2; y++)
        {
            int streak = 0, rowBase = y * stride;
            for (int x = x1; x < x2; x++)
            {
                int i = rowBase + x * 4;
                byte b = px[i], g = px[i + 1], r = px[i + 2];
                if (g > 65 && g > r + 20 && g > b + 20) streak++;
                else { if (streak > best) best = streak; streak = 0; }
            }
            if (streak > best) best = streak;
        }

        // <8 = không thấy bar; 8-50 = HP thấp; >50 = HP ổn
        bool low = best >= 8 && best < 50;
        Log($"HP streak={best}px → low={low}");
        return low;
    }

    // ── Hero Dead ────────────────────────────────────────────────────────────

    private bool IsHeroDead(Bitmap bmp)
    {
        // Nút RESURRECT xanh lá sáng ở bottom-center (~0.38, 0.77)
        var px = LockPixels(bmp, out int stride);
        int greenCount = 0;
        foreach (var (xr, yr) in new[] { (0.36, 0.76), (0.38, 0.77), (0.40, 0.78) })
        {
            int x = (int)(bmp.Width * xr), y = (int)(bmp.Height * yr);
            if (x >= bmp.Width || y >= bmp.Height) continue;
            int i = y * stride + x * 4;
            byte b = px[i], g = px[i + 1], r = px[i + 2];
            if (g > 150 && r < 100 && b < 100) greenCount++;
        }
        bool found = greenCount >= 2;
        if (found) Log($"IsHeroDead: RESURRECT green detected ({greenCount}/3)");
        return found;
    }

    // ── Not Enough Food ──────────────────────────────────────────────────────

    private bool IsNotEnoughFood(Bitmap bmp)
    {
        using var tpl = ImageMatcher.LoadTemplate(TplNotEnoughFood);
        if (tpl == null) return false;
        var (found, _, conf) = ImageMatcher.FindTemplate(bmp, tpl, 0.75);
        Log($"IsNotEnoughFood: conf={conf:F2} found={found}");
        return found;
    }

    // ── Chamber of Fortune ───────────────────────────────────────────────────

    private bool IsChambersOfFortune(Bitmap bmp)
    {
        using var tpl = ImageMatcher.LoadTemplate(TplChamberFortune);
        if (tpl == null) return false;
        var (found, _, conf) = ImageMatcher.FindTemplate(bmp, tpl, 0.75);
        Log($"IsChambersOfFortune: conf={conf:F2} found={found}");
        return found;
    }

    private bool IsFortuneItemPopup(Bitmap bmp)
    {
        foreach (var name in new[] { TplFortuneGetIt, TplFortuneGiveUp })
        {
            using var tpl = ImageMatcher.LoadTemplate(name);
            if (tpl == null) continue;
            var (found, _, conf) = ImageMatcher.FindTemplate(bmp, tpl, 0.75);
            Log($"IsFortuneItemPopup {name}: conf={conf:F2} found={found}");
            if (found) return true;
        }
        return false;
    }

    private bool IsFortuneTapToContinue(Bitmap bmp)
    {
        using var tpl = ImageMatcher.LoadTemplate(TplFortuneTapContinue);
        if (tpl == null) return false;
        var (found, _, conf) = ImageMatcher.FindTemplate(bmp, tpl, 0.75);
        Log($"IsFortuneTapToContinue: conf={conf:F2} found={found}");
        return found;
    }

    private void TapFirstChest(Bitmap screen)
    {
        using var tpl = ImageMatcher.LoadTemplate(TplFortuneChest);
        if (tpl == null) { Log("fortune_chest.png missing."); return; }
        var hits = ImageMatcher.FindAllTemplates(screen, tpl, 0.70);
        Log($"Fortune chests found: {hits.Count}");
        if (hits.Count == 0) return;
        var pick = hits[Random.Shared.Next(hits.Count)];
        Log($"Picking chest at {pick}.");
        _adb.TapScaled(pick.X, pick.Y, screen.Width, screen.Height);
    }

    private void TapTemplate(Bitmap screen, params string[] templateNames)
    {
        foreach (var name in templateNames)
        {
            using var tpl = ImageMatcher.LoadTemplate(name);
            if (tpl == null) continue;
            var (found, pt, conf) = ImageMatcher.FindTemplate(screen, tpl, 0.75);
            Log($"TapTemplate {name}: conf={conf:F2} found={found}");
            if (!found) continue;
            _adb.TapScaled(pt.X, pt.Y, screen.Width, screen.Height);
            return;
        }
        Log($"TapTemplate: none found among [{string.Join(", ", templateNames)}]");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
