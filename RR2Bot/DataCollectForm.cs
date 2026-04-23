using RR2Bot.Core;
using RR2Bot.Models;

namespace RR2Bot;

public class DataCollectForm : Form
{
    private static readonly string[] Screens =
    [
        "at_base", "in_battle", "battle_result", "prepare_screen",
        "player_list", "favorites_screen", "community_screen",
        "not_enough_food", "chambers_fortune", "fortune_item",
        "fortune_tap_continue", "unknown"
    ];

    private readonly BotConfig _cfg;
    private readonly ComboBox _cmbScreen;
    private readonly Button _btnToggle;
    private readonly Label _lblCount;
    private readonly System.Windows.Forms.Timer _timer;
    private int _count;
    private bool _running;

    private static readonly string OutputRoot =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TrainingData");

    public DataCollectForm(BotConfig cfg)
    {
        _cfg = cfg;

        Text            = "Data Collector";
        Size            = new Size(340, 160);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Color.FromArgb(30, 30, 30);
        ForeColor       = Color.WhiteSmoke;
        Font            = new Font("Segoe UI", 9f);

        var lblScreen = new Label { Text = "Screen:", Location = new Point(12, 16), AutoSize = true };

        _cmbScreen = new ComboBox
        {
            Location      = new Point(70, 12),
            Size          = new Size(200, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor     = Color.FromArgb(45, 45, 45),
            ForeColor     = Color.WhiteSmoke,
            FlatStyle     = FlatStyle.Flat,
        };
        _cmbScreen.Items.AddRange(Screens);
        _cmbScreen.SelectedIndex = 0;

        _btnToggle = new Button
        {
            Text      = "▶ Auto",
            Location  = new Point(12, 50),
            Size      = new Size(90, 32),
            BackColor = Color.FromArgb(34, 139, 34),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        _btnToggle.FlatAppearance.BorderSize = 0;
        _btnToggle.Click += BtnToggle_Click;

        var btnSnap = new Button
        {
            Text      = "📷 Snap",
            Location  = new Point(110, 50),
            Size      = new Size(90, 32),
            BackColor = Color.FromArgb(60, 60, 100),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        btnSnap.FlatAppearance.BorderSize = 0;
        btnSnap.Click += (_, _) => CaptureScreen();

        _lblCount = new Label
        {
            Text      = "Captured: 0",
            Location  = new Point(210, 58),
            AutoSize  = true,
            ForeColor = Color.LightGreen,
        };

        Controls.AddRange(new Control[] { lblScreen, _cmbScreen, _btnToggle, btnSnap, _lblCount });

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += Timer_Tick;
    }

    private void BtnToggle_Click(object? sender, EventArgs e)
    {
        _running = !_running;
        if (_running)
        {
            _count = 0;
            _cmbScreen.Enabled = false;
            _btnToggle.Text      = "■ Stop";
            _btnToggle.BackColor = Color.FromArgb(178, 34, 34);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _cmbScreen.Enabled = true;
            _btnToggle.Text      = "▶ Start";
            _btnToggle.BackColor = Color.FromArgb(34, 139, 34);
        }
    }

    private void Timer_Tick(object? sender, EventArgs e) => CaptureScreen();

    private void CaptureScreen()
    {
        var screen = ScreenCapture.CaptureWindow(_cfg.WindowTitle);
        if (screen == null) return;

        var state = (string)_cmbScreen.SelectedItem!;
        var dir   = Path.Combine(OutputRoot, state);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        screen.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        screen.Dispose();

        _count++;
        _lblCount.Text = $"Captured: {_count}";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        base.OnFormClosing(e);
    }
}
