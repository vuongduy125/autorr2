using System.Windows.Forms;

namespace RR2Bot.Forms
{
    public class LicenseInputForm : Form
    {
        public string EnteredKey => _txtKey.Text.Trim();

        private TextBox _txtKey;
        private Button _btnActivate;
        private Button _btnCancel;
        private Label _lblPrompt;

        public LicenseInputForm()
        {
            Text = "RR2Bot - License Activation";
            Size = new Size(420, 180);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;

            _lblPrompt = new Label
            {
                Text = "Enter your license key:",
                Location = new Point(20, 20),
                AutoSize = true
            };

            _txtKey = new TextBox
            {
                Location = new Point(20, 50),
                Size = new Size(360, 25)
            };

            _btnActivate = new Button
            {
                Text = "Activate",
                Location = new Point(200, 90),
                Size = new Size(85, 30),
                DialogResult = DialogResult.OK
            };

            _btnCancel = new Button
            {
                Text = "Cancel",
                Location = new Point(295, 90),
                Size = new Size(85, 30),
                DialogResult = DialogResult.Cancel
            };

            AcceptButton = _btnActivate;
            CancelButton = _btnCancel;

            Controls.AddRange(new Control[] { _lblPrompt, _txtKey, _btnActivate, _btnCancel });
        }
    }
}
