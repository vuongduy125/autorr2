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

    private const string TplSpellReady  = @"battle\spell_ready.png";
    private const string TplFortuneChest = @"fortune\fortune_chest.png";

    public bool DebugDetect { get; set; }

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

    // ─────────────────────────────────────────────────────────────────────────
    // Mỗi vòng loop: detect screen → confirm → act.
    // Không giả định đang ở đâu — luôn đọc màn hình trước.
    // ─────────────────────────────────────────────────────────────────────────

    private enum ScreenState
    {
        InBattle,             // mana bar visible
        BattleResult,         // victory/defeat screen
        PrepareScreen,        // "Prepare for Battle" — nút ATTACK! orange
        PlayerListScreen,     // danh sách người chơi để tấn công (fallback)
        FavoritesScreen,      // player list trong Favorites đã load
        CommunityScreen,      // community overlay đang mở
        HeroDead,             // hero chết → retreat
        NotEnoughFood,        // popup not enough food → collect
        ChambersOfFortune,    // chọn rương
        FortuneItemPopup,     // popup NEW ITEM → GET IT
        FortuneTapToContinue, // tap to continue sau khi nhận item
        AtBase,               // base confirmed
        Unknown,              // không nhận ra — chờ
    }

    private DateTime _lastEnemySnap = DateTime.MinValue;
    private static readonly string SnapDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "snaps");

    private DateTime _battleStartAt;
    private DateTime _lastAtBaseTap    = DateTime.MinValue;
    private bool     _communityHasSwiped = false;

    // Enemy position smoothing (EMA) to prevent jitter from flickering YOLO detections
    private double _smoothEX = 0.5, _smoothEY = 0.5;
    private bool   _hasSmoothedEnemy = false;
    private int    _enemyLostFrames  = 0;
    private const int EnemyGraceFrames = 4;

    // Movement loop target (updated by YOLO loop, executed by MoveLoopAsync)
    private volatile bool   _moveEnabled = false;
    private double _moveTargetX = 0.5, _moveTargetY = 0.5;


    private static readonly TimeSpan AtBaseCooldown = TimeSpan.FromSeconds(8);

    // ── Main loop ─────────────────────────────────────────────────────────────

    private volatile ScreenState _lastLoggedState = ScreenState.Unknown;
    private volatile bool _inBattle = false;
    private List<YoloDetection> _latestDetections = [];
    private int _latestScreenW, _latestScreenH;

    public async Task RunAsync(CancellationToken ct)
    {
        Log(_yolo != null ? "BattleManager started (YOLO mode)." : "BattleManager started (template mode).");
        var troopLoop = Task.Run(() => TroopSummonLoopAsync(ct), ct);
        var moveLoop  = Task.Run(() => MoveLoopAsync(ct), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var screen = ScreenCapture.CaptureWindow(_cfg.WindowTitle);
                if (screen == null) { await Task.Delay(1000, ct); continue; }

                // Chạy YOLO 1 lần — dùng cho cả detect lẫn act
                var detections = _yolo?.Detect(screen, 0.30f) ?? [];
                _latestDetections = detections;
                _latestScreenW = screen.Width;
                _latestScreenH = screen.Height;
                if (DebugDetect && detections.Count > 0)
                    Log($"[D] YOLO: {string.Join(", ", detections.Select(d => $"{d.ClassName}({d.Confidence:F2})"))}");

                var state = DetectScreen(detections);
                if (state != _lastLoggedState) { Log($"Screen: {state}"); _lastLoggedState = state; }
                await ActAsync(state, screen, detections, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"[Error] {ex.Message}"); }
        }
        await troopLoop.ConfigureAwait(false);
        await moveLoop.ConfigureAwait(false);
        _yolo?.Dispose();
        Log("BattleManager stopped.");
    }

    private async Task TroopSummonLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_inBattle)
                    SummonTroops(_latestDetections, _latestScreenW, _latestScreenH);
                await Task.Delay(600, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignored */ }
        }
    }

    private async Task MoveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_moveEnabled)
                    _adb.LongPress((int)(_moveTargetX * _adb.ScreenWidth),
                                   (int)(_moveTargetY * _adb.ScreenHeight), 200);
                else
                    await Task.Delay(50, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignored */ }
        }
    }

    // ── Detect screen ─────────────────────────────────────────────────────────

    private static bool Has(List<YoloDetection> d, params string[] classes)
        => d.Any(x => classes.Contains(x.ClassName));

    private static YoloDetection? Get(List<YoloDetection> d, params string[] classes)
        => d.FirstOrDefault(x => classes.Contains(x.ClassName));

    private ScreenState DetectScreen(List<YoloDetection> d)
    {
        if (_yolo == null || d.Count == 0) return ScreenState.Unknown;

        var isBase = Has(d, "base_attack", "base_community", "base_food", "base_gold", "base_gem");

        if (Has(d, "battle_result_label", "battle_result_win", "battle_result_lose", "battle_result_continue", "battle_result_retreat", "batter_rs_lose")
            && !isBase)
            return ScreenState.BattleResult;
        if (Has(d, "inbatte_pause") && !isBase)
            return ScreenState.InBattle;
        if (Has(d, "favorite_player_list_label", "favorite_player_list_attack")
            && !isBase
            && !Has(d, "community_label", "community_label_favorites"))
            return ScreenState.FavoritesScreen;
        if (Has(d, "prepare4battle_label", "prepare4battle_attack")
            && !Has(d, "favorite_player_list_attack", "favorite_player_list_label"))
            return ScreenState.PrepareScreen;
        if (Has(d, "community_label_favorites") || Has(d, "community_label"))
            return ScreenState.CommunityScreen;
        if (Has(d, "not_enough_food_label", "not_enough_food_getfood", "not_enough_food_collect"))
            return ScreenState.NotEnoughFood;
        if (Has(d, "chamber_tap_2_continue") && !isBase)
            return ScreenState.FortuneTapToContinue;
        if (Has(d, "chamber_getit", "chamber_giveup", "chamber_sell") && !isBase)
            return ScreenState.FortuneItemPopup;
        if (Has(d, "chamber_chest") && !isBase)
            return ScreenState.ChambersOfFortune;
        if (isBase)
            return ScreenState.AtBase;

        return ScreenState.Unknown;
    }

    // ── Act per screen ────────────────────────────────────────────────────────

    private async Task ActAsync(ScreenState state, Bitmap screen, List<YoloDetection> d, CancellationToken ct)
    {
        switch (state)
        {
            case ScreenState.InBattle:
                _inBattle = true;
                var retreatMidBattle = Get(d, "battle_result_retreat");
                if (retreatMidBattle != null)
                {
                    Log("Hero died → tapping RETREAT.");
                    _adb.TapScaled(retreatMidBattle.Center.X, retreatMidBattle.Center.Y, screen.Width, screen.Height);
                    await Task.Delay(2000, ct);
                    break;
                }
                await DoBattleAsync(screen, ct);
                break;

            case ScreenState.BattleResult:
                _inBattle = false;
                _moveEnabled = false;
                var contBtn    = Get(d, "battle_result_continue");
                var retreatBtn = Get(d, "battle_result_retreat");
                if (contBtn != null)
                {
                    Log("Battle result → tapping CONTINUE.");
                    _adb.TapScaled(contBtn.Center.X, contBtn.Center.Y, screen.Width, screen.Height);
                }
                else if (retreatBtn != null)
                {
                    Log("Battle result → hero died, tapping RETREAT.");
                    _adb.TapScaled(retreatBtn.Center.X, retreatBtn.Center.Y, screen.Width, screen.Height);
                }
                else
                {
                    Log("Battle result → no button found, tapping fallback.");
                    _adb.TapRatio(0.5, 0.88);
                }
                await Task.Delay(2000, ct);
                break;

            case ScreenState.PrepareScreen:
                Log("Prepare screen → tapping ATTACK!");
                var prepareBtn = Get(d, "prepare4battle_attack");
                if (prepareBtn != null)
                    _adb.TapScaled(prepareBtn.Center.X, prepareBtn.Center.Y, screen.Width, screen.Height);
                else
                    _adb.TapRatio(_cfg.AttackButtonXRatio, _cfg.AttackButtonYRatio);
                _battleStartAt = DateTime.Now;
                await Task.Delay(2000, ct);
                break;

            case ScreenState.PlayerListScreen:
            case ScreenState.FavoritesScreen:
                Log("Favorites player list → tapping first attack.");
                TapFirstAttackButton(screen, d);
                await Task.Delay(2500, ct);
                break;

            case ScreenState.CommunityScreen:
                if (!_communityHasSwiped)
                {
                    Log("Community → swiping 2x ~1.5 screens to Favorites.");
                    _adb.SwipeRatio(0.95, _cfg.CommunitySwipeYRatio, 0.05, _cfg.CommunitySwipeYRatio, 500);
                    await Task.Delay(400, ct);
                    _adb.SwipeRatio(0.95, _cfg.CommunitySwipeYRatio, 0.05, _cfg.CommunitySwipeYRatio, 500);
                    _communityHasSwiped = true;
                    await Task.Delay(1500, ct);
                    break;
                }
                var favTab = Get(d, "community_label_favorites");
                if (favTab != null)
                {
                    // community_label_favorites là title text của card — OPEN button ở đáy card (~83% height)
                    int openX = favTab.Center.X;
                    int openY = (int)(screen.Height * 0.83);
                    Log($"Community → tapping OPEN at ({openX},{openY}).");
                    _adb.TapScaled(openX, openY, screen.Width, screen.Height);
                }
                else
                    Log("Community → Favorites card not found after swipe.");
                await Task.Delay(1500, ct);
                break;

            case ScreenState.HeroDead:
                Log("Hero dead → tapping RETREAT.");
                _adb.TapRatio(0.615, 0.788);
                await Task.Delay(2000, ct);
                break;

            case ScreenState.NotEnoughFood:
                Log("Not enough food → tapping COLLECT.");
                var foodBtn = Get(d, "not_enough_food_getfood", "not_enough_food_collect");
                if (foodBtn != null)
                    _adb.TapScaled(foodBtn.Center.X, foodBtn.Center.Y, screen.Width, screen.Height);
                else
                {
                    var exitFallback = Get(d, "unknown_x_exit");
                    if (exitFallback != null)
                    {
                        Log("Food button not found → tapping X to close popup.");
                        _adb.TapScaled(exitFallback.Center.X, exitFallback.Center.Y, screen.Width, screen.Height);
                    }
                    else
                    {
                        Log("Food button not found → tapping fallback COLLECT position.");
                        _adb.TapRatio(0.61, 0.57);
                    }
                }
                await Task.Delay(2000, ct);
                break;

            case ScreenState.FortuneTapToContinue:
                Log("Fortune: tap to continue.");
                var tapContinue = Get(d, "chamber_tap_2_continue");
                if (tapContinue != null)
                    _adb.TapScaled(tapContinue.Center.X, tapContinue.Center.Y, screen.Width, screen.Height);
                else
                    Log("chamber_tap_2_continue not found.");
                await Task.Delay(1500, ct);
                break;

            case ScreenState.FortuneItemPopup:
                Log("Fortune: tapping GET IT / GIVE UP.");
                var fortuneBtn = Get(d, "chamber_getit", "chamber_giveup", "chamber_sell");
                if (fortuneBtn != null)
                    _adb.TapScaled(fortuneBtn.Center.X, fortuneBtn.Center.Y, screen.Width, screen.Height);
                else
                    Log("Fortune button not found.");
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
                using (var snap = ScreenCapture.CaptureWindow(_cfg.WindowTitle))
                {
                    if (snap != null)
                    {
                        var snapD = _yolo?.Detect(snap, 0.30f) ?? [];
                        var cont = Get(snapD, "chamber_tap_2_continue");
                        if (cont != null)
                        {
                            Log("Fortune: tap to continue after chest.");
                            _adb.TapScaled(cont.Center.X, cont.Center.Y, snap.Width, snap.Height);
                        }
                    }
                }
                break;

            case ScreenState.Unknown:
                var exitBtn = Get(d, "unknown_x_exit");
                if (exitBtn != null)
                {
                    Log("Unknown screen → tapping X exit.");
                    _adb.TapScaled(exitBtn.Center.X, exitBtn.Center.Y, screen.Width, screen.Height);
                }
                await Task.Delay(1500, ct);
                break;

            case ScreenState.AtBase:
            default:
                _communityHasSwiped = false;
                if (DateTime.Now - _lastAtBaseTap >= AtBaseCooldown)
                {
                    Log("At base → tapping community icon.");
                    var communityBtn = Get(d, "base_community");
                    if (communityBtn != null)
                        _adb.TapScaled(communityBtn.Center.X, communityBtn.Center.Y, screen.Width, screen.Height);
                    else
                        _adb.TapRatio(_cfg.CommunityIconXRatio, _cfg.CommunityIconYRatio);
                    _lastAtBaseTap = DateTime.Now;
                }
                await Task.Delay(2000, ct);
                break;
        }
    }

    // ── Attack button helper ──────────────────────────────────────────────────

    private void TapFirstAttackButton(Bitmap screen, List<YoloDetection> d)
    {
        var attacks = d.Where(x => x.ClassName == "favorite_player_list_attack" && x.Confidence >= 0.7f)
                       .ToList();
        if (attacks.Count > 0)
        {
            // Enabled button has gold swords (warm pixels); disabled is silver/gray
            const int WarmThreshold = 80;
            var enabled = attacks.Where(x => CountWarmPixels(screen, x.BBox) >= WarmThreshold)
                                 .OrderByDescending(x => x.Confidence)
                                 .ToList();
            if (enabled.Count > 0)
            {
                var pick = enabled.First();
                Log($"Tapping attack at ({pick.Center.X},{pick.Center.Y}).");
                _adb.TapScaled(pick.Center.X, pick.Center.Y, screen.Width, screen.Height);
                return;
            }
            Log("All visible attack buttons disabled → scrolling down for more players.");
            _adb.SwipeRatio(0.5, 0.70, 0.5, 0.30, 400);
            return;
        }
        Log("No attack buttons found → closing list.");
        _adb.TapRatio(0.877, 0.120);
    }

    private static int CountWarmPixels(Bitmap bmp, RectangleF bbox)
    {
        var px = LockPixels(bmp, out int stride);
        int x1 = Math.Max(0, (int)bbox.X),         y1 = Math.Max(0, (int)bbox.Y);
        int x2 = Math.Min(bmp.Width,  (int)bbox.Right), y2 = Math.Min(bmp.Height, (int)bbox.Bottom);
        int count = 0;
        for (int y = y1; y < y2; y++)
        for (int x = x1; x < x2; x++)
        {
            int i = y * stride + x * 4;
            byte b = px[i], g = px[i + 1], r = px[i + 2];
            // Warm/golden: R high, G moderate, B low
            if (r > 180 && g > 90 && b < 80 && r > g + 50) count++;
        }
        return count;
    }

    // ── In-battle logic ───────────────────────────────────────────────────────

    private async Task DoBattleAsync(Bitmap screen, CancellationToken ct)
    {
        bool hpLow   = IsHpLow(screen);
        bool inGrace = (DateTime.Now - _battleStartAt).TotalSeconds < 5;

        YoloDetection? enemy = null;
        if (!inGrace && _latestScreenW > 0)
            enemy = _latestDetections
                .Where(x => x.ClassName == "inbatte_enemy_heal"
                         && x.Confidence >= 0.35f
                         && x.Center.Y / (double)_latestScreenH > 0.12)
                .OrderByDescending(x => x.Confidence)
                .FirstOrDefault();
        bool enemyFound = enemy != null;
        double ex = enemy != null ? enemy.Center.X / (double)_latestScreenW : 0.0;
        double ey = enemy != null ? enemy.Center.Y / (double)_latestScreenH : 0.0;

        if (enemyFound && (DateTime.Now - _lastEnemySnap).TotalSeconds >= 1)
        {
            _lastEnemySnap = DateTime.Now;
            Directory.CreateDirectory(SnapDir);
            AutoSaveSnap(screen, _latestDetections);
        }

        if (hpLow)
        {
            _moveEnabled = false;
            Log("HP low! Retreating.");
            UseReadySpells(screen);
            _adb.TapRatio(0.39, 0.64);
        }
        else if (enemyFound)
        {
            const double alpha = 0.35;
            if (!_hasSmoothedEnemy) { _smoothEX = ex; _smoothEY = ey; }
            else { _smoothEX = alpha * ex + (1 - alpha) * _smoothEX; _smoothEY = alpha * ey + (1 - alpha) * _smoothEY; }
            _hasSmoothedEnemy = true;
            _enemyLostFrames  = 0;
            _moveEnabled = false;
            Log($"ENEMY AT ({ex:F2},{ey:F2}) — holding position, using spells.");
            UseReadySpells(screen);
        }
        else if (_hasSmoothedEnemy && _enemyLostFrames < EnemyGraceFrames)
        {
            _enemyLostFrames++;
            _moveEnabled = false;
            Log($"ENEMY GRACE {_enemyLostFrames}/{EnemyGraceFrames} — holding position.");
            UseReadySpells(screen);
        }
        else
        {
            _hasSmoothedEnemy = false;
            _moveTargetX = _cfg.HeroTargetXRatio + (Random.Shared.NextDouble() - 0.5) * 0.08;
            _moveTargetY = _cfg.HeroTargetYRatio + (Random.Shared.NextDouble() - 0.5) * 0.08;
            _moveEnabled = true;
        }

        await Task.Delay(_cfg.BattleLoopIntervalMs, ct);
    }

    // ── Spell / Troop ─────────────────────────────────────────────────────────

    public void UseReadySpells(Bitmap? screen = null)
    {
        var skills = _latestDetections.Where(x => x.ClassName == "inbatte_hero_skill").ToList();
        if (skills.Count > 0 && _latestScreenW > 0)
        {
            foreach (var skill in skills)
            {
                _adb.TapScaled(skill.Center.X, skill.Center.Y, _latestScreenW, _latestScreenH);
                Thread.Sleep(80);
            }
            return;
        }

        // fallback: template gate + hardcoded ratios
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

    public void SummonTroops(List<YoloDetection> d, int screenW, int screenH)
    {
        var summons = d.Where(x => x.ClassName == "inbatte_summon").ToList();
        if (summons.Count > 0)
        {
            var pick = summons[Random.Shared.Next(summons.Count)];
            _adb.TapScaled(pick.Center.X, pick.Center.Y, screenW, screenH);
            return;
        }
        foreach (var (x, y) in _cfg.TroopButtonRatios)
        {
            _adb.TapRatio(x, y);
            Thread.Sleep(60);
        }
    }


    private void MoveToward(double targetX, double targetY)
        => _adb.LongPress((int)(targetX * _adb.ScreenWidth), (int)(targetY * _adb.ScreenHeight), 1200);

    // ── Enemy HP bar (pixel scan) ─────────────────────────────────────────────

    private (bool found, double x, double y) FindEnemyHpBar(Bitmap screen)
    {
        var px = LockPixels(screen, out int stride);
        int w = screen.Width, h = screen.Height;
        int x1 = (int)(w * 0.40), x2 = (int)(w * 0.90);
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

    // ── Auto snap ─────────────────────────────────────────────────────────────

    private void AutoSaveSnap(Bitmap screen, List<YoloDetection> detections)
    {
        try
        {
            using var bmp = new Bitmap(screen);
            using var g   = Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            foreach (var det in detections)
            {
                Color c = det.ClassName switch
                {
                    "inbatte_enemy_heal" => Color.Red,
                    "inbatte_hero_skill" => Color.Yellow,
                    "inbatte_summon"     => Color.Cyan,
                    "inbatte_pause"      => Color.LimeGreen,
                    _                    => Color.Gray,
                };
                using var pen   = new Pen(c, 2);
                using var brush = new SolidBrush(Color.FromArgb(200, c));
                g.DrawRectangle(pen, det.BBox.X, det.BBox.Y, det.BBox.Width, det.BBox.Height);
                g.DrawString($"{det.ClassName} {det.Confidence:F2}",
                    new Font("Consolas", 7f), brush, det.BBox.X, det.BBox.Y - 14);
            }
            string path = Path.Combine(SnapDir, $"snap_{DateTime.Now:HHmmss}.png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Log($"Snap → {path}");
        }
        catch (Exception ex) { Log($"Snap error: {ex.Message}"); }
    }

    // ── Fortune chest fallback ────────────────────────────────────────────────

    private void TapFirstChest(Bitmap screen)
    {
        using var tpl = ImageMatcher.LoadTemplate(TplFortuneChest);
        if (tpl == null) { Log("fortune_chest.png missing."); return; }
        var hits = ImageMatcher.FindAllTemplates(screen, tpl, 0.70);
        if (hits.Count == 0) return;
        var pick = hits[Random.Shared.Next(hits.Count)];
        _adb.TapScaled(pick.X, pick.Y, screen.Width, screen.Height);
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
}
