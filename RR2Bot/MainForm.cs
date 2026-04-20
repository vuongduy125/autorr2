using System.Collections.Concurrent;
using RR2Bot.Core;
using RR2Bot.Models;
using RR2Bot.Modules;

namespace RR2Bot;

public partial class MainForm : Form
{
    private readonly BotConfig _cfg = new();
    private AdbController? _adb;

    private CancellationTokenSource? _cts;
    private Task? _botTask;

    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer _logFlushTimer;
    private readonly ConcurrentQueue<(string msg, Color color)> _logQueue = new();

    public MainForm()
    {
        InitializeComponent();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();

        _logFlushTimer = new System.Windows.Forms.Timer { Interval = 700 };
        _logFlushTimer.Tick += LogFlushTimer_Tick;
        _logFlushTimer.Start();
    }

    // ── Button handlers ──────────────────────────────────────────────────────

    private void BtnStart_Click(object? sender, EventArgs e)
    {
        _adb = new AdbController();
        if (!_adb.Connect(_cfg.AdbHost, _cfg.AdbPort, _cfg.AdbExePath))
        {
            AppendLog("[Warn] ADB not connected — tap/swipe will be skipped.", Color.Orange);
        }
        else
        {
            _adb.ReadScreenSize();
            AppendLog($"Android screen: {_adb.ScreenWidth}x{_adb.ScreenHeight}");
        }

        var mode = (BotMode)cmbMode.SelectedIndex;
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
            tasks.Add(baseMgr.RunAsync(ct));
        }

        if (mode is BotMode.BattleOnly or BotMode.Both)
        {
            var battleMgr = new BattleManager(adb, _cfg, msg => AppendLog(msg));
            tasks.Add(battleMgr.RunAsync(ct));
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
        cmbMode.Enabled  = !running;
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

        // Trim to last 500 lines to avoid memory growth
        const int maxLines = 500;
        if (rtbLog.Lines.Length > maxLines)
        {
            var kept = rtbLog.Lines[^maxLines..];
            rtbLog.Lines = kept;
        }

        rtbLog.ResumeLayout();
        rtbLog.ScrollToCaret();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        _adb?.Dispose();
        base.OnFormClosing(e);
    }
}
