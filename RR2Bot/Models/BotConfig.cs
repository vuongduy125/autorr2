namespace RR2Bot.Models;

public enum HeroDirection { Forward, Backward, Up, Down, UpRight, UpLeft }

public class BotConfig
{
    public string WindowTitle { get; set; } = "BlueStacks App Player";
    public string AdbExePath  { get; set; } = @"C:\adb\adb.exe";
    public string AdbHost     { get; set; } = "127.0.0.1";
    public int    AdbPort     { get; set; } = 5555;

    public double MatchThreshold      { get; set; } = 0.80;
    public double GoldMatchThreshold  { get; set; } = 0.65;

    // ── Joystick (ratio 0.0-1.0 của Android screen) ──────────────────────────
    public double JoystickXRatio     { get; set; } = 0.125; // 200/1600
    public double JoystickYRatio     { get; set; } = 0.889; // 800/900
    public double JoystickRadiusX    { get; set; } = 0.056; // 90/1600
    public double JoystickRadiusY    { get; set; } = 0.100; // 90/900

    // ── Spell buttons (ratio) ─────────────────────────────────────────────────
    public (double X, double Y)[] SpellButtonRatios { get; set; } =
    {
        (0.937, 0.166), // hero spell card top-right
        (0.923, 0.844), // chưởng (hero ability) bottom-right
    };

    // ── Troop buttons (ratio) ─────────────────────────────────────────────────
    public (double X, double Y)[] TroopButtonRatios { get; set; } =
    {
        (0.069, 0.828), // shield+sword — summon troops
        (0.069, 0.166), // kèn — rally troops
    };

    // ── Hero movement (swipe từ điểm giữa-trái sang phải = đi forward) ────────
    public double HeroMoveFromX      { get; set; } = 0.20;
    public double HeroMoveToX        { get; set; } = 0.45;
    public double HeroMoveY          { get; set; } = 0.50;
    public int    HeroMoveDurationMs { get; set; } = 800;

    // Tap target để hero pathfind đến — upper-right của màn hình
    public double HeroTargetXRatio { get; set; } = 0.82;
    public double HeroTargetYRatio { get; set; } = 0.18;

    // HP bar detection
    public double HpBarYRatio        { get; set; } = 0.30;  // y của thanh HP (≈280px/900)
    public double HpLowThresholdX    { get; set; } = 0.421; // 30% bar — nếu không xanh = HP thấp

    // ── Waypoints (sequence di chuyển hero) ──────────────────────────────────
    public WaypointAction[] Waypoints { get; set; } =
    {
        new() { Direction = HeroDirection.Forward,  DurationMs = 2000 },
        new() { Direction = HeroDirection.Forward,  DurationMs = 2000 },
        new() { Direction = HeroDirection.Forward,  DurationMs = 2000 },
    };

    // ── Gold collect button (ratio) ───────────────────────────────────────────
    public double GoldCollectXRatio { get; set; } = 0.031; // 50/1600
    public double GoldCollectYRatio { get; set; } = 0.556; // 500/900

    // ── Community / battle entry (ratio) ─────────────────────────────────────
    // Swipe phải để thấy Favorites
    public double CommunityIconXRatio { get; set; } = 0.957; // 1531/1600
    public double CommunityIconYRatio { get; set; } = 0.554; // 499/900
    public double FavoritesOpenXRatio   { get; set; } = 0.875; // nút OPEN của Favorites
    public double FavoritesOpenYRatio   { get; set; } = 0.780;
    public double CommunitySwipeStartXRatio { get; set; } = 0.90;
    public double CommunitySwipeEndXRatio   { get; set; } = 0.10;
    public double CommunitySwipeYRatio      { get; set; } = 0.500; // 450/900

    // ── Loop intervals ────────────────────────────────────────────────────────
    public int BaseLoopIntervalMs   { get; set; } = 5000;
    public int BattleLoopIntervalMs { get; set; } = 900;
}

public enum BotMode { BaseOnly, BattleOnly, Both }

public class WaypointAction
{
    public HeroDirection Direction { get; set; } = HeroDirection.Forward;
    public int DurationMs          { get; set; } = 1000;
}
