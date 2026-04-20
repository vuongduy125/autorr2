using SharpAdbClient;
using SharpAdbClient.DeviceCommands;

namespace RR2Bot.Core;

public class AdbController : IDisposable
{
    private readonly AdbClient _client = new();
    private DeviceData? _device;
    private bool _connected;

    // ── Kết nối ──────────────────────────────────────────────────────────────

    public bool Connect(string host = "127.0.0.1", int port = 5555, string adbExePath = "adb.exe")
    {
        try
        {
            AdbServer.Instance.StartServer(adbExePath, restartServerIfNewer: false);

            // Connect tới BlueStacks qua TCP
            _client.Connect(new System.Net.DnsEndPoint(host, port));
            System.Threading.Thread.Sleep(800);

            var devices = _client.GetDevices();
            _device = devices.FirstOrDefault();
            _connected = _device != null;
            return _connected;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ADB] Connect error: {ex.Message}");
            return false;
        }
    }

    public bool IsConnected => _connected && _device != null;

    // Android screen resolution — đọc sau khi connect
    public int ScreenWidth  { get; private set; } = 1600;
    public int ScreenHeight { get; private set; } = 900;

    public void ReadScreenSize()
    {
        var output = ShellOutput("wm size");
        // "Physical size: 1600x900"
        var match = System.Text.RegularExpressions.Regex.Match(output, @"(\d+)x(\d+)");
        if (match.Success)
        {
            ScreenWidth  = int.Parse(match.Groups[1].Value);
            ScreenHeight = int.Parse(match.Groups[2].Value);
        }
        System.Diagnostics.Debug.WriteLine($"[ADB] wm size raw='{output}' → {ScreenWidth}×{ScreenHeight}");
    }

    // Scale tọa độ từ không gian capture window sang Android screen
    public (int x, int y) ScaleToAndroid(int captureX, int captureY, int captureW, int captureH)
    {
        int ax = (int)(captureX * (double)ScreenWidth  / captureW);
        int ay = (int)(captureY * (double)ScreenHeight / captureH);
        return (ax, ay);
    }

    // ── Lệnh cơ bản ──────────────────────────────────────────────────────────

    public void Tap(int x, int y)
    {
        System.Diagnostics.Debug.WriteLine($"[ADB] Tap ({x},{y}) on {ScreenWidth}×{ScreenHeight}");
        Shell($"input tap {x} {y}");
    }

    // Tap với tọa độ từ capture bitmap — tự scale sang Android space
    public void TapScaled(int captureX, int captureY, int captureW, int captureH)
    {
        var (ax, ay) = ScaleToAndroid(captureX, captureY, captureW, captureH);
        Tap(ax, ay);
    }

    // Tap/Swipe bằng ratio (0.0-1.0) — hoạt động đúng trên mọi resolution
    public void TapRatio(double xRatio, double yRatio)
        => Tap((int)(ScreenWidth * xRatio), (int)(ScreenHeight * yRatio));

    // Tap tính từ góc phải (xOffset tính từ phải sang, yOffset từ trên xuống)
    public void TapFromRight(int xOffset, int yOffset)
        => Tap(ScreenWidth - xOffset, yOffset);

    // Tap tính từ góc phải dưới
    public void TapFromBottomRight(int xOffset, int yOffset)
        => Tap(ScreenWidth - xOffset, ScreenHeight - yOffset);

    public void SwipeRatio(double x1R, double y1R, double x2R, double y2R, int durationMs = 300)
        => Swipe((int)(ScreenWidth * x1R), (int)(ScreenHeight * y1R),
                 (int)(ScreenWidth * x2R), (int)(ScreenHeight * y2R), durationMs);

    public void Swipe(int x1, int y1, int x2, int y2, int durationMs = 200)
        => Shell($"input swipe {x1} {y1} {x2} {y2} {durationMs}");

    public void LongPress(int x, int y, int durationMs = 1000)
        => Shell($"input swipe {x} {y} {x} {y} {durationMs}");

    public void KeyEvent(int keyCode)
        => Shell($"input keyevent {keyCode}");

    // ── Helper ───────────────────────────────────────────────────────────────

    private void Shell(string cmd)
    {
        if (_device == null) return;
        try { _client.ExecuteRemoteCommand(cmd, _device, null); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ADB] Shell error: {ex.Message}"); }
    }

    private string ShellOutput(string cmd)
    {
        if (_device == null) return "";
        try
        {
            var receiver = new SharpAdbClient.ConsoleOutputReceiver();
            _client.ExecuteRemoteCommand(cmd, _device, receiver);
            return receiver.ToString();
        }
        catch { return ""; }
    }

    public void Dispose() => _connected = false;
}
