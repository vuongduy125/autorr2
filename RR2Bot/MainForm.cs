using System.Collections.Concurrent;
using RR2Bot.Core;
using RR2Bot.Models;
using RR2Bot.Modules;

namespace RR2Bot;

public partial class MainForm : Form
{
    private readonly BotConfig _cfg = new();
    private AdbController? _adb;
    private BattleManager? _battleMgr;

    private CancellationTokenSource? _cts;
    private Task? _botTask;

    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer _logFlushTimer;
    private readonly ConcurrentQueue<(string msg, Color color)> _logQueue = new();

    private readonly CheckBox _chkDebug;

    public MainForm()
    {
        InitializeComponent();

        // Thêm checkbox Debug conf vào form
        _chkDebug = new CheckBox
        {
            Text     = "Debug conf",
            AutoSize = true,
            ForeColor = Color.Orange,
        };
        _chkDebug.CheckedChanged += (_, _) =>
        {
            if (_battleMgr != null) _battleMgr.DebugDetect = _chkDebug.Checked;
        };
        grpControl.Controls.Add(_chkDebug);
        _chkDebug.Location = new Point(btnStop.Right + 16, btnStop.Top + 9);

        _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();

        _logFlushTimer = new System.Windows.Forms.Timer { Interval = 700 };
        _logFlushTimer.Tick += LogFlushTimer_Tick;
        _logFlushTimer.Start();
    }

    // ── Button handlers ──────────────────────────────────────────────────────

    private async void BtnStart_Click(object? sender, EventArgs e)
    {
        btnStart.Enabled = false;
        AppendLog("Connecting ADB...");

        _adb = new AdbController();
        bool connected = await Task.Run(() => _adb.Connect(_cfg.AdbHost, _cfg.AdbPort, _cfg.AdbExePath));
        if (!connected)
        {
            AppendLog("[Warn] ADB not connected — tap/swipe will be skipped.", Color.Orange);
        }
        else
        {
            await Task.Run(() => _adb.ReadScreenSize());
            AppendLog($"Android screen: {_adb.ScreenWidth}x{_adb.ScreenHeight}");
        }

        var mode = rbBoth.Checked ? BotMode.Both
                 : rbBaseOnly.Checked ? BotMode.BaseOnly
                 : BotMode.BattleOnly;
        SetRunning(true);
        AppendLog($"Bot started — mode: {mode}");

        _cts     = new CancellationTokenSource();
        _botTask = RunBotAsync(mode, _cts.Token);
    }

    private async void BtnStop_Click(object? sender, EventArgs e)
    {
        btnStop.Enabled  = false;
        btnStart.Enabled = false;
        AppendLog("Stopping...");

        _cts?.Cancel();
        if (_botTask != null)
        {
            try { await _botTask; } catch { /* ignored */ }
        }

        _adb?.Dispose();
        _adb = null;
        SetRunning(false);
        AppendLog("Bot stopped.", Color.Orange);
    }

    // ── Bot runner ────────────────────────────────────────────────────────────

    private async Task RunBotAsync(BotMode mode, CancellationToken ct)
    {
        var tasks = new List<Task>();
        var adb   = _adb!;

        if (mode is BotMode.BaseOnly or BotMode.Both)
        {
            var baseMgr = new BaseManager(adb, _cfg, msg => AppendLog(msg));
            tasks.Add(Task.Run(async () => await baseMgr.RunAsync(ct), ct));
        }

        if (mode is BotMode.BattleOnly or BotMode.Both)
        {
            _battleMgr = new BattleManager(adb, _cfg, msg => AppendLog(msg))
            {
                DebugDetect = _chkDebug.Checked
            };
            tasks.Add(Task.Run(async () => await _battleMgr.RunAsync(ct), ct));
        }

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { /* normal stop */ }
        catch (Exception ex) { AppendLog($"[Fatal] {ex.Message}", Color.Red); }
    }

    // ── Status strip ─────────────────────────────────────────────────────────

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        bool adbOk    = _adb?.IsConnected ?? false;
        bool windowOk = ScreenCapture.IsWindowOpen(_cfg.WindowTitle);

        tsslAdb.Text      = adbOk    ? "Connected"  : "Not connected";
        tsslAdb.ForeColor = adbOk    ? Color.LimeGreen : Color.OrangeRed;
        tsslWindow.Text   = windowOk ? "Found"      : "Not found";
        tsslWindow.ForeColor = windowOk ? Color.LimeGreen : Color.OrangeRed;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetRunning(bool running)
    {
        btnStart.Enabled = !running;
        btnStop.Enabled  = running;
        rbBaseOnly.Enabled   = !running;
        rbBattleOnly.Enabled = !running;
        rbBoth.Enabled       = !running;
        lblStatus.Text   = running ? "Running..." : "Idle";
        lblStatus.ForeColor = running ? Color.LimeGreen : Color.Gray;
    }

    private void AppendLog(string msg, Color? color = null)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        _logQueue.Enqueue(($"[{ts}] {msg}", color ?? Color.LightGreen));
    }

    private void LogFlushTimer_Tick(object? sender, EventArgs e)
    {
        if (_logQueue.IsEmpty) return;

        rtbLog.SuspendLayout();
        while (_logQueue.TryDequeue(out var entry))
        {
            rtbLog.SelectionStart  = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor  = entry.color;
            rtbLog.AppendText(entry.msg + Environment.NewLine);
        }

        rtbLog.ResumeLayout();

        // Trim log: xóa nửa đầu khi quá dài, tránh assign Lines[] (chậm)
        if (rtbLog.TextLength > 60_000)
        {
            int cut = rtbLog.Text.IndexOf('\n', 20_000);
            if (cut > 0)
            {
                rtbLog.Select(0, cut + 1);
                rtbLog.SelectedText = "";
            }
        }

        rtbLog.ScrollToCaret();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        _adb?.Dispose();
        base.OnFormClosing(e);
    }
}
