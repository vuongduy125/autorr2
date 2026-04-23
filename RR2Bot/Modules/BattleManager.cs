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

    private const string TplSpellReady    = @"battle\spell_ready.png";
    private const string TplVictory       = @"result\victory.png";
    private const string TplDefeat        = @"result\defeat.png";
    private const string TplFavoritesCard = @"community\favorites_tab.png";
    private const string TplOpenBtn       = @"community\open_btn.png";
    private const string TplBaseCoin             = @"base\base_coin_food.png";
    private const string TplBaseSword            = @"base\base_sword_icon.png";
    private const string TplCommunityTitle       = @"community\community_title.png";
    private const string TplFavoritePlayersTitle = @"community\favorite_players_title.png";
    private const string TplPrepareBattle        = @"prepare\prepare_battle_title.png";
    private const string TplPrepareAttackBtn     = @"prepare\prepare_battle_attack.png";
    private const string TplNotEnoughFood        = @"food\not_enough_food.png";
    private const string TplNotEnoughFoodCollect = @"food\not_enough_food_collect.png";
    private const string TplChamberFortune       = @"fortune\chamber_fortune.png";
    private const string TplFortuneChest         = @"fortune\fortune_chest.png";
    private const string TplFortuneGetIt         = @"fortune\fortune_get_it.png";
    private const string TplFortuneGiveUp        = @"fortune\fortune_give_up.png";
    private const string TplFortuneTapContinue   = @"fortune\fortune_tap_to_continue.png";
    private const string TplContinueBtn          = @"result\continue_btn.png";
    private const string TplAttackBtn             = @"battle\attack_btn.png";
    private const string TplAttackBtnDisable      = @"battle\attack_btn_disable.png";
    private const string TplPlayerListAttackBtn   = @"community\player_list_attack.png";
    private const string TplLose3Crown           = @"result\lose_3_crown.png";

    public bool DebugDetect { get; set; }

    private static readonly string ModelPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "best.onnx");

    private readonly YoloDetector? _yolo =
        File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "best.onnx"))
            ? new YoloDetector(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "best.onnx"))
            : null;

    public BattleManager(AdbController adb, BotConfig cfg, Action<string>? log = null)
    {
        _adb = adb;
        _cfg = cfg;
        _log = log;
    }

    // Helper: load + match + log conf nếu DebugDetect bật
    private (bool found, Point pt, double conf) Match(Bitmap bmp, string name, double threshold = 0.75, bool trimDark = false)
    {
        using var tpl = ImageMatcher.LoadTemplate(name, trimDark);
        if (tpl == null)
        {
            if (DebugDetect) Log($"[D] {name}: template missing");
            return (false, Point.Empty, 0);
        }
        var r = ImageMatcher.FindTemplate(bmp, tpl, threshold);
        if (DebugDetect) Log($"[D] {name}: conf={r.Confidence:F3} thr={threshold:F2} → {(r.Found ? "FOUND" : "miss")}");
        return r;
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

    private ScreenState _lastLoggedState = ScreenState.Unknown;

    public async Task RunAsync(CancellationToken ct)
    {
        Log(_yolo != null ? "BattleManager started (YOLO mode)." : "BattleManager started (template mode).");
        var troopLoop = Task.Run(() => TroopSummonLoopAsync(ct), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var screen = ScreenCapture.CaptureWindow(_cfg.WindowTitle);
                if (screen == null) { await Task.Delay(1000, ct); continue; }

                // Chạy YOLO 1 lần — dùng cho cả detect lẫn act
                var detections = _yolo?.Detect(screen, 0.30f) ?? [];
                if (DebugDetect && detections.Count > 0)
                    Log($"[D] YOLO: {string.Join(", ", detections.Select(d => $"{d.ClassName}({d.Confidence:F2})"))}");

                var state = DetectScreen(screen, detections);
                if (state != _lastLoggedState) { Log($"Screen: {state}"); _lastLoggedState = state; }
                await ActAsync(state, screen, detections, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"[Error] {ex.Message}"); }
        }
        await troopLoop.ConfigureAwait(false);
        _yolo?.Dispose();
        Log("BattleManager stopped.");
    }

    private async Task TroopSummonLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_lastLoggedState == ScreenState.InBattle)
                    SummonTroops();
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignored */ }
        }
    }

    // ── Detect screen ─────────────────────────────────────────────────────────

    private string _lastActivity = "";

    private static bool Has(List<YoloDetection> d, params string[] classes)
        => d.Any(x => classes.Contains(x.ClassName));

    private static YoloDetection? Get(List<YoloDetection> d, params string[] classes)
        => d.FirstOrDefault(x => classes.Contains(x.ClassName));

    private ScreenState DetectScreen(Bitmap bmp, List<YoloDetection> d)
    {
        // YOLO detect những class đã train — chỉ dùng kết quả khi có detection
        if (_yolo != null && d.Count > 0)
        {
            if (Has(d, "batter_rs_victory_crown", "batter_rs_lose", "batter_rs_continue")
                && !Has(d, "base_attack", "base_community", "base_food", "base_gold", "base_gem"))
                return ScreenState.BattleResult;
            if (Has(d, "inbatte_pause"))
                return ScreenState.InBattle;
            if (Has(d, "prepare4battle_label", "prepare4battle_attack"))
                return ScreenState.PrepareScreen;
            if (Has(d, "favorite_player_list_label"))
                return ScreenState.PlayerListScreen;
            if (Has(d, "community_label_favorites"))
                return ScreenState.FavoritesScreen;
            if (Has(d, "community_label"))
                return ScreenState.CommunityScreen;
            if (Has(d, "not_enough_food_label", "not_enough_food_getfood"))
                return ScreenState.NotEnoughFood;
            if (Has(d, "chamber_tap_2_continue"))
                return ScreenState.FortuneTapToContinue;
            if (Has(d, "chamber_getit", "chamber_giveup"))
                return ScreenState.FortuneItemPopup;
            if (Has(d, "chamber_chest"))
                return ScreenState.ChambersOfFortune;
            if (Has(d, "base_community", "base_gold", "base_attack", "base_food"))
                return ScreenState.AtBase;
        }

        // Template matching cho những màn chưa có YOLO data
        if (IsVictoryOrDefeat(bmp))      return ScreenState.BattleResult;
        if (IsInBattle(bmp))             return ScreenState.InBattle;
        if (IsPrepareScreen(bmp))        return ScreenState.PrepareScreen;
        if (IsPlayerListScreen(bmp))     return ScreenState.PlayerListScreen;
        if (IsFavoritesScreen(bmp))      return ScreenState.FavoritesScreen;
        if (IsCommunityScreen(bmp))      return ScreenState.CommunityScreen;
        if (IsHeroDead(bmp))             return ScreenState.HeroDead;
        if (IsNotEnoughFood(bmp))        return ScreenState.NotEnoughFood;
        if (IsFortuneTapToContinue(bmp)) return ScreenState.FortuneTapToContinue;
        if (IsFortuneItemPopup(bmp))     return ScreenState.FortuneItemPopup;
        if (IsChambersOfFortune(bmp))    return ScreenState.ChambersOfFortune;
        if (IsAtBase(bmp))               return ScreenState.AtBase;
        return ScreenState.Unknown;
    }

    // ── Act per screen ────────────────────────────────────────────────────────

    private void TapDetection(List<YoloDetection> d, Bitmap screen, params string[] classes)
    {
        var det = Get(d, classes);
        if (det != null) { _adb.TapScaled(det.Center.X, det.Center.Y, screen.Width, screen.Height); return; }
        // fallback: template matching
        TapTemplate(screen, classes.Select(c => c + ".png").ToArray());
    }

    private async Task ActAsync(ScreenState state, Bitmap screen, List<YoloDetection> d, CancellationToken ct)
    {
        switch (state)
        {
            case ScreenState.InBattle:
                await DoBattleAsync(screen, ct);
                break;

            case ScreenState.BattleResult:
                Log("Battle result → tapping CONTINUE.");
                var contBtn = Get(d, "batter_rs_continue");
                if (contBtn != null)
                    _adb.TapScaled(contBtn.Center.X, contBtn.Center.Y, screen.Width, screen.Height);
                else
                {
                    var (cf, cpt, cc) = Match(screen, TplContinueBtn, 0.40);
                    Log($"continue_btn conf={cc:F3}");
                    if (cf) _adb.TapScaled(cpt.X, cpt.Y, screen.Width, screen.Height);
                    else _adb.TapRatio(0.5, 0.88); // hardcoded bottom-center fallback
                }
                await Task.Delay(2000, ct);
                break;

            case ScreenState.PrepareScreen:
                Log("Prepare screen confirmed → tapping ATTACK!");
                var prepareBtn = Get(d, "prepare4battle_attack");
                if (prepareBtn != null) _adb.TapScaled(prepareBtn.Center.X, prepareBtn.Center.Y, screen.Width, screen.Height);
                else TapPrepareAttack(screen);
                _battleStartAt = DateTime.Now;
                await Task.Delay(2000, ct);
                break;

            case ScreenState.PlayerListScreen:
                Log("Player list confirmed → finding attack button.");
                TapFirstAttackButton(screen, d);
                await Task.Delay(2500, ct);
                break;

            case ScreenState.FavoritesScreen:
                Log("Favorites confirmed → tapping OPEN.");
                TapFavoritesOpen(screen);
                await Task.Delay(5000, ct);
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
                var foodBtn = Get(d, "not_enough_food_getfood");
                if (foodBtn != null) _adb.TapScaled(foodBtn.Center.X, foodBtn.Center.Y, screen.Width, screen.Height);
                else TapTemplate(screen, TplNotEnoughFoodCollect);
                await Task.Delay(2000, ct);
                break;

            case ScreenState.FortuneTapToContinue:
                Log("Fortune: tap to continue.");
                var tapContinue = Get(d, "chamber_tap_2_continue");
                if (tapContinue != null) _adb.TapScaled(tapContinue.Center.X, tapContinue.Center.Y, screen.Width, screen.Height);
                else TapTemplate(screen, TplFortuneTapContinue);
                await Task.Delay(1500, ct);
                break;

            case ScreenState.FortuneItemPopup:
                Log("Fortune: tapping GET IT / GIVE UP.");
                var fortuneBtn = Get(d, "chamber_getit", "chamber_giveup");
                if (fortuneBtn != null) _adb.TapScaled(fortuneBtn.Center.X, fortuneBtn.Center.Y, screen.Width, screen.Height);
                else TapTemplate(screen, TplFortuneGetIt, TplFortuneGiveUp);
                await Task.Delay(1500, ct);
                break;

            case ScreenState.ChambersOfFortune:
                Log("Fortune: picking a chest.");
                var chests = d.Where(x => x.ClassName == "chamber_chest").ToList();
                if (chests.Count > 0)
                {
                    var pick = chests[Random.Shared.Next(chests.Count)];
                    _adb.TapScaled(pick.Center.X, pick.Center.Y, screen.Width, screen.Height);
                }
                else TapFirstChest(screen);
                await Task.Delay(2000, ct);
                break;

            case ScreenState.Unknown:
                await Task.Delay(1500, ct);
                break;

            case ScreenState.AtBase:
            default:
                if (DateTime.Now - _lastAtBaseTap >= AtBaseCooldown)
                {
                    Log("At base → tapping community icon.");
                    var communityBtn = Get(d, "base_community");
                    if (communityBtn != null)
                        _adb.TapScaled(communityBtn.Center.X, communityBtn.Center.Y, screen.Width, screen.Height);
                    else
                        _adb.TapRatio(_cfg.CommunityIconXRatio, _cfg.CommunityIconYRatio);
                    _lastAtBaseTap    = DateTime.Now;
                    _lastCommunityTap = DateTime.Now;
                }
                await Task.Delay(2000, ct);
                break;
        }
    }

    // ── Screen detectors ──────────────────────────────────────────────────────

    private bool IsInBattle(Bitmap bmp)
        => Match(bmp, @"battle\pause_btn.png", 0.75).found;

    private bool IsVictoryOrDefeat(Bitmap bmp)
    {
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
        if (DebugDetect) Log($"[D] VictoryOrDefeat red={redCount}/5");
        if (redCount >= 3) return true;

        foreach (var name in new[] { TplVictory, TplDefeat, TplLose3Crown })
        {
            var (found, pt, _) = Match(bmp, name);
            if (!found) continue;
            _adb.TapScaled(pt.X, pt.Y, bmp.Width, bmp.Height);
            return true;
        }
        return false;
    }

    private bool IsPrepareScreen(Bitmap bmp)
    {
        var (found, _, _) = Match(bmp, TplPrepareAttackBtn);
        if (found) return true;
        var (f2, _, _) = Match(bmp, TplPrepareBattle);
        return f2;
    }

    private bool IsPlayerListScreen(Bitmap bmp)
        => Match(bmp, TplFavoritePlayersTitle, 0.65, trimDark: true).found;

    private bool IsFavoritesScreen(Bitmap bmp)
    {
        if (!IsCommunityOpen(bmp)) return false;
        return Match(bmp, TplFavoritesCard, 0.72).found;
    }

    private bool IsAtBase(Bitmap bmp)
        => Match(bmp, TplBaseCoin, 0.73).found;

    private bool IsCommunityOpen(Bitmap bmp)
        => Match(bmp, TplCommunityTitle).found;

    private bool IsCommunityScreen(Bitmap bmp)
        => IsCommunityOpen(bmp);

    // ── Tap OPEN button trên Favorites ────────────────────────────────────────

    private void TapPrepareAttack(Bitmap screen)
    {
        var (found, pt, _) = Match(screen, TplPrepareAttackBtn);
        if (found) { _adb.TapScaled(pt.X, pt.Y, screen.Width, screen.Height); return; }
        Log("ATTACK! not found → tapping hardcoded.");
        _adb.TapRatio(_cfg.AttackButtonXRatio, _cfg.AttackButtonYRatio);
    }

    private static bool IsAttackButtonActive(Bitmap bmp, int cx, int cy)
    {
        int sampleX = Math.Max(0, cx - 60);
        int sampleY = Math.Clamp(cy, 0, bmp.Height - 1);
        var c = bmp.GetPixel(sampleX, sampleY);
        return c.B > 130 && c.B > c.R + 20 && c.G > 100;
    }

    private void TapFirstAttackButton(Bitmap screen, List<YoloDetection> d)
    {
        // YOLO: lấy tất cả nút attack, filter active bằng màu nền
        if (_yolo != null)
        {
            var attacks = d.Where(x => x.ClassName == "favorite_player_list_attack")
                           .Where(x => IsAttackButtonActive(screen, x.Center.X, x.Center.Y))
                           .OrderBy(x => x.BBox.Y)
                           .ToList();
            if (attacks.Count > 0)
            {
                var pick = attacks.First();
                Log($"Tapping attack at ({pick.Center.X},{pick.Center.Y}).");
                _adb.TapScaled(pick.Center.X, pick.Center.Y, screen.Width, screen.Height);
                return;
            }
            Log("YOLO: no attack buttons → falling back to template.");
        }

        // Fallback template matching — dùng template nút kiếm trong player list
        using var tpl = ImageMatcher.LoadTemplate(TplPlayerListAttackBtn)
                     ?? ImageMatcher.LoadTemplate(TplAttackBtn);
        if (tpl == null) { Log("player_list_attack template missing."); return; }

        var (_, _, bestConf) = ImageMatcher.FindTemplate(screen, tpl, 0.01f);
        Log($"player_list_attack best conf={bestConf:F3} (thr=0.50)");

        var allPts = ImageMatcher.FindAllTemplates(screen, tpl, 0.50);
        if (allPts.Count == 0)
        {
            Log("Template miss → tapping hardcoded first row attack.");
            _adb.TapRatio(0.877, 0.280); // nút kiếm hàng đầu tiên
            return;
        }
        var pt = allPts.OrderBy(p => p.Y).First();
        Log($"Tapping attack at ({pt.X},{pt.Y}).");
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
            var (cf, cardPt, _) = ImageMatcher.FindTemplate(screen, cardTpl, 0.72);
            if (cf)
            {
                var below = opens.Where(p => p.Y > cardPt.Y).ToList();
                best = below.Count > 0
                    ? below.OrderBy(p => p.Y).First()
                    : opens.OrderBy(p => p.Y).First();
            }
            else
            {
                best = opens.OrderBy(p => p.Y).First();
            }
        }
        else
        {
            best = opens.OrderBy(p => p.Y).First();
        }

        _adb.TapScaled(best.X, best.Y, screen.Width, screen.Height);
    }

    // ── In-battle logic ───────────────────────────────────────────────────────

    private async Task DoBattleAsync(Bitmap screen, CancellationToken ct)
    {
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

        if (candidates.Count == 0) return (false, 0, 0);

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

        if (bars.Count == 0) return (false, 0, 0);

        double cx = w * 0.5, cy = h * 0.5;
        var best = bars.MinBy(c => Math.Pow(c.midX - cx, 2) + Math.Pow(c.midY - cy, 2));
        double rx = best.midX / (double)w;
        double ry = Math.Min((best.midY + 50.0) / h, 0.85);
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
        return best >= 8 && best < 50;
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
        return greenCount >= 2;
    }

    // ── Not Enough Food ──────────────────────────────────────────────────────

    private bool IsNotEnoughFood(Bitmap bmp)
        => Match(bmp, TplNotEnoughFoodCollect, 0.60).found
        || Match(bmp, TplNotEnoughFood, 0.60).found;

    private bool IsChambersOfFortune(Bitmap bmp)
        => Match(bmp, TplChamberFortune).found;

    private bool IsFortuneItemPopup(Bitmap bmp)
    {
        foreach (var name in new[] { TplFortuneGetIt, TplFortuneGiveUp })
            if (Match(bmp, name, 0.72).found) return true;
        return false;
    }

    private bool IsFortuneTapToContinue(Bitmap bmp)
        => Match(bmp, TplFortuneTapContinue).found;

    private void TapFirstChest(Bitmap screen)
    {
        using var tpl = ImageMatcher.LoadTemplate(TplFortuneChest);
        if (tpl == null) { Log("fortune_chest.png missing."); return; }
        var hits = ImageMatcher.FindAllTemplates(screen, tpl, 0.70);
        if (hits.Count == 0) return;
        var pick = hits[Random.Shared.Next(hits.Count)];
        _adb.TapScaled(pick.X, pick.Y, screen.Width, screen.Height);
    }

    private void TapTemplate(Bitmap screen, params string[] templateNames)
    {
        foreach (var name in templateNames)
        {
            var (found, pt, _) = Match(screen, name);
            if (!found) continue;
            _adb.TapScaled(pt.X, pt.Y, screen.Width, screen.Height);
            return;
        }
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
