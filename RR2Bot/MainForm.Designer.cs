namespace RR2Bot;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null!;

    // Controls
    private GroupBox grpControl = null!;
    private Button btnStart = null!;
    private Button btnStop = null!;
    private ComboBox cmbMode = null!;
    private Label lblMode = null!;
    private Label lblStatus = null!;
    private RichTextBox rtbLog = null!;
    private StatusStrip statusStrip = null!;
    private ToolStripStatusLabel tsslAdb = null!;
    private ToolStripStatusLabel tsslWindow = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null) components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        grpControl  = new GroupBox();
        btnStart    = new Button();
        btnStop     = new Button();
        cmbMode     = new ComboBox();
        lblMode     = new Label();
        lblStatus   = new Label();
        rtbLog      = new RichTextBox();
        statusStrip = new StatusStrip();
        tsslAdb     = new ToolStripStatusLabel();
        tsslWindow  = new ToolStripStatusLabel();

        // ── Form ──────────────────────────────────────────────────────────────
        Text            = "RR2 Bot";
        Size            = new Size(800, 560);
        MinimumSize     = new Size(700, 480);
        StartPosition   = FormStartPosition.CenterScreen;
        Font            = new Font("Segoe UI", 9f);
        BackColor       = Color.FromArgb(30, 30, 30);
        ForeColor       = Color.WhiteSmoke;

        // ── GroupBox control ──────────────────────────────────────────────────
        grpControl.Text      = "Control";
        grpControl.ForeColor = Color.WhiteSmoke;
        grpControl.Location  = new Point(12, 12);
        grpControl.Size      = new Size(760, 80);
        grpControl.Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        // Mode label + combo
        lblMode.Text     = "Mode:";
        lblMode.Location = new Point(12, 28);
        lblMode.AutoSize = true;

        cmbMode.Location      = new Point(55, 24);
        cmbMode.Size          = new Size(140, 24);
        cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbMode.Items.AddRange(new object[] { "Base Only", "Battle Only", "Both" });
        cmbMode.SelectedIndex = 0;
        cmbMode.FlatStyle     = FlatStyle.Flat;
        cmbMode.BackColor     = Color.FromArgb(50, 50, 50);
        cmbMode.ForeColor     = Color.WhiteSmoke;

        // Start button
        btnStart.Text      = "▶  Start";
        btnStart.Location  = new Point(220, 20);
        btnStart.Size      = new Size(110, 36);
        btnStart.BackColor = Color.FromArgb(34, 139, 34);
        btnStart.ForeColor = Color.White;
        btnStart.FlatStyle = FlatStyle.Flat;
        btnStart.FlatAppearance.BorderSize = 0;
        btnStart.Font      = new Font("Segoe UI", 10f, FontStyle.Bold);
        btnStart.Click    += BtnStart_Click;

        // Stop button
        btnStop.Text      = "■  Stop";
        btnStop.Location  = new Point(345, 20);
        btnStop.Size      = new Size(110, 36);
        btnStop.BackColor = Color.FromArgb(178, 34, 34);
        btnStop.ForeColor = Color.White;
        btnStop.FlatStyle = FlatStyle.Flat;
        btnStop.FlatAppearance.BorderSize = 0;
        btnStop.Font      = new Font("Segoe UI", 10f, FontStyle.Bold);
        btnStop.Enabled   = false;
        btnStop.Click    += BtnStop_Click;

        // Status label
        lblStatus.Text      = "Idle";
        lblStatus.Location  = new Point(480, 30);
        lblStatus.AutoSize  = true;
        lblStatus.ForeColor = Color.Gray;
        lblStatus.Font      = new Font("Segoe UI", 9f, FontStyle.Italic);

        grpControl.Controls.AddRange(new Control[]
            { lblMode, cmbMode, btnStart, btnStop, lblStatus });

        // ── Log RichTextBox ───────────────────────────────────────────────────
        rtbLog.Location    = new Point(12, 104);
        rtbLog.Size        = new Size(760, 380);
        rtbLog.Anchor      = AnchorStyles.Top | AnchorStyles.Bottom
                           | AnchorStyles.Left | AnchorStyles.Right;
        rtbLog.ReadOnly    = true;
        rtbLog.BackColor   = Color.FromArgb(20, 20, 20);
        rtbLog.ForeColor   = Color.LightGreen;
        rtbLog.Font        = new Font("Consolas", 9f);
        rtbLog.ScrollBars  = RichTextBoxScrollBars.Vertical;
        rtbLog.BorderStyle = BorderStyle.None;

        // ── StatusStrip ───────────────────────────────────────────────────────
        statusStrip.BackColor = Color.FromArgb(45, 45, 45);
        statusStrip.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripStatusLabel("ADB:") { ForeColor = Color.Gray },
            tsslAdb,
            new ToolStripSeparator(),
            new ToolStripStatusLabel("Window:") { ForeColor = Color.Gray },
            tsslWindow,
        });
        tsslAdb.Text    = "Not connected";
        tsslAdb.ForeColor = Color.OrangeRed;
        tsslWindow.Text   = "Not found";
        tsslWindow.ForeColor = Color.OrangeRed;

        // ── Add to form ───────────────────────────────────────────────────────
        Controls.AddRange(new Control[] { grpControl, rtbLog, statusStrip });
    }
}
