using RR2Bot;
using RR2Bot.Services;
using RR2Bot.Forms;

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

var subscriptionService = new SubscriptionService();
var storedKey = subscriptionService.GetStoredKey();

bool validated = false;

if (storedKey != null)
{
    // Key exists — validate via GET
    var (success, error) = await subscriptionService.ValidateAsync(storedKey, isNewKey: false);
    if (success)
    {
        validated = true;
    }
    else
    {
        MessageBox.Show(error, "License Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

if (!validated)
{
    // No key or validation failed — prompt user
    using var inputForm = new LicenseInputForm();
    if (inputForm.ShowDialog() == DialogResult.OK && !string.IsNullOrEmpty(inputForm.EnteredKey))
    {
        var (success, error) = await subscriptionService.ValidateAsync(
            inputForm.EnteredKey, isNewKey: true);
        if (success)
        {
            validated = true;
        }
        else
        {
            MessageBox.Show(error, "License Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

if (!validated)
{
    // User cancelled or all attempts failed
    return;
}

// Start heartbeat to check subscription every 30 minutes
var activeKey = subscriptionService.GetStoredKey()!;
subscriptionService.StartHeartbeat(activeKey);

Application.Run(new MainForm());

// Stop heartbeat when application exits
subscriptionService.StopHeartbeat();
