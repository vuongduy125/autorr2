# AI Agent Plan

Branch: trello-IUZydmkI/add-subcription

Generated at: 2026-04-20T15:01:35.1858846+00:00

## Output

# Implementation Plan: Add Subscription Check

## Summary

Add a startup license/subscription validation flow that checks a `key.txt` file for a license key, validates it against a remote API (with mainboard ID matching), and gates access to the application. The `MaxInstance` value from the subscription response is stored globally for use throughout the app.

## Assumptions

1. **API Base URL**: Will be stored in a configuration constant (e.g., `https://api.example.com`). I'll add it as a configurable field in `BotConfig` or a dedicated config constant, since the Trello card doesn't specify the base URL.
2. **key.txt location**: Stored alongside the executable (`AppDomain.CurrentDomain.BaseDirectory`), not in `Templates/`.
3. **"If no" (key.txt doesn't exist)**: The app should show an input dialog prompting the user to enter a license key, then call POST to register it. The card says "If no" → call POST with a key, but doesn't specify where the key comes from — I assume a user-input dialog.
4. **Mainboard ID**: Retrieved via WMI (`Win32_BaseBoard` → `SerialNumber`). This is the standard unique mainboard identifier on Windows.
5. **"Dialog check box"**: Interpreted as a dialog/form (not literally a checkbox) that appears at startup to handle license validation. If the key is valid and matches, it auto-proceeds. If not, it shows error messages.
6. **POST request body typo**: `"MaiboardId"` in the card is a typo for `"MainboardId"` — I'll use `"MainboardId"` consistently unless the API literally expects the typo (will note this).
7. **HTTP error handling**: If the API is unreachable or returns errors, show a dialog and prevent app access.
8. **No existing HTTP client dependency**: Will use `System.Net.Http.HttpClient` (built-in).
9. **JSON serialization**: Will use `System.Text.Json` (built-in with .NET).

## Impacted Areas

| Area | Change |
|------|--------|
| `Program.cs` | Add subscription check before `MainForm` launch |
| **NEW** `Models/SubscriptionInfo.cs` | Response model for API |
| **NEW** `Models/GlobalState.cs` | Static class holding `MaxInstance` (globally accessible) |
| **NEW** `Services/SubscriptionService.cs` | API calls + key.txt I/O + mainboard ID retrieval |
| **NEW** `Forms/LicenseInputForm.cs` | Dialog for entering license key when key.txt is missing |
| `RR2Bot.csproj` | Possibly add `System.Management` NuGet reference for WMI |

## Implementation Steps

### Step 1: Add `System.Management` reference

In `RR2Bot/RR2Bot.csproj`, add:
```xml
<PackageReference Include="System.Management" Version="8.*" />
```
Required for WMI query to get mainboard serial number.

### Step 2: Create `Models/SubscriptionInfo.cs`

```csharp
namespace RR2Bot.Models
{
    public class SubscriptionInfo
    {
        public bool IsActive { get; set; }
        public int MaxInstance { get; set; }
        public string MainboardId { get; set; } = string.Empty;
    }
}
```

### Step 3: Create `Models/GlobalState.cs`

```csharp
namespace RR2Bot.Models
{
    public static class GlobalState
    {
        public static int MaxInstance { get; set; } = 1;
    }
}
```

Simple static class — accessible anywhere. Follows the repo's minimal style (similar to how `BotConfig` is a plain object passed around).

### Step 4: Create `Services/SubscriptionService.cs`

This is the core logic class:

```csharp
using System.Management;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RR2Bot.Models;

namespace RR2Bot.Services
{
    public class SubscriptionService
    {
        private const string ApiBaseUrl = "https://api.example.com"; // TODO: configure
        private static readonly string KeyFilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "key.txt");

        private static readonly HttpClient _httpClient = new();
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Returns the license key from key.txt, or null if file doesn't exist / is empty.
        /// </summary>
        public string? GetStoredKey()
        {
            if (!File.Exists(KeyFilePath)) return null;
            var key = File.ReadAllText(KeyFilePath).Trim();
            return string.IsNullOrEmpty(key) ? null : key;
        }

        public void StoreKey(string key)
        {
            File.WriteAllText(KeyFilePath, key.Trim());
        }

        /// <summary>
        /// GET api/v1/subcription/{key}
        /// </summary>
        public async Task<SubscriptionInfo> GetSubscriptionAsync(string key)
        {
            var response = await _httpClient.GetAsync($"{ApiBaseUrl}/api/v1/subcription/{key}");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<SubscriptionInfo>(json, _jsonOptions)
                   ?? throw new Exception("Invalid subscription response.");
        }

        /// <summary>
        /// POST api/v1/subcription/{key} with MainboardId body
        /// </summary>
        public async Task<SubscriptionInfo> RegisterSubscriptionAsync(string key, string mainboardId)
        {
            var body = new { MainboardId = mainboardId };
            var content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync($"{ApiBaseUrl}/api/v1/subcription/{key}", content);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<SubscriptionInfo>(json, _jsonOptions)
                   ?? throw new Exception("Invalid subscription response.");
        }

        /// <summary>
        /// Get mainboard serial number via WMI.
        /// </summary>
        public string GetLocalMainboardId()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard");
                foreach (var obj in searcher.Get())
                {
                    var serial = obj["SerialNumber"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(serial) && serial != "Default string")
                        return serial;
                }
            }
            catch { /* fallback below */ }

            // Fallback: try Product + Manufacturer as a composite ID
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Manufacturer, Product FROM Win32_BaseBoard");
                foreach (var obj in searcher.Get())
                {
                    var mfr = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                    var prod = obj["Product"]?.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(prod))
                        return $"{mfr}_{prod}";
                }
            }
            catch { }

            return "UNKNOWN";
        }

        /// <summary>
        /// Full validation flow. Returns (success, errorMessage).
        /// On success, GlobalState.MaxInstance is set.
        /// </summary>
        public async Task<(bool Success, string? ErrorMessage)> ValidateAsync(string key, bool isNewKey)
        {
            try
            {
                var localMainboardId = GetLocalMainboardId();
                SubscriptionInfo info;

                if (isNewKey)
                {
                    info = await RegisterSubscriptionAsync(key, localMainboardId);
                }
                else
                {
                    info = await GetSubscriptionAsync(key);
                }

                if (!info.IsActive)
                    return (false, "Your subscription is not active. Please contact support.");

                if (!string.Equals(info.MainboardId, localMainboardId, StringComparison.OrdinalIgnoreCase))
                    return (false, "Your key is already attached to another device.");

                // Success — store globally
                GlobalState.MaxInstance = info.MaxInstance;

                if (isNewKey)
                    StoreKey(key);

                return (true, null);
            }
            catch (HttpRequestException ex)
            {
                return (false, $"Failed to connect to license server:\n{ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, $"License validation error:\n{ex.Message}");
            }
        }
    }
}
```

### Step 5: Create `Forms/LicenseInputForm.cs`

A simple WinForms dialog for entering a license key when `key.txt` is missing:

```csharp
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
```

### Step 6: Modify `Program.cs`

Add the subscription check **before** launching `MainForm`:

```csharp
using RR2Bot.Services;
using RR2Bot.Forms;

// ... existing usings ...

[STAThread]
static async Task Main() // change to async Task
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);

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

    Application.Run(new MainForm());
}
```

**Note**: If `Program.cs` currently uses `static void Main()`, change signature to `static async Task Main()`. .NET supports async Main.

### Step 7: Add `key.txt` to `.gitignore`

Append to `.gitignore`:
```
key.txt
```

This file contains user-specific license keys and should not be committed.

## Tests to Add

No test framework exists in the project currently. If tests are added later, these should be covered:

| Test | Description |
|------|-------------|
| `SubscriptionService.GetStoredKey_NoFile_ReturnsNull` | Verify null when key.txt missing |
| `SubscriptionService.GetStoredKey_EmptyFile_ReturnsNull` | Verify null when key.txt is empty |
| `SubscriptionService.StoreKey_WritesFile` | Verify key.txt is created with correct content |
| `SubscriptionService.GetLocalMainboardId_ReturnsNonEmpty` | Verify WMI returns something (integration test, Windows-only) |
| `ValidateAsync_ActiveAndMatchingBoard_Succeeds` | Mock HTTP, verify success path |
| `ValidateAsync_InactiveSubscription_Fails` | Mock HTTP with `IsActive: false` |
| `ValidateAsync_MismatchedBoard_Fails` | Mock HTTP with different MainboardId |
| `ValidateAsync_HttpError_ReturnsError` | Mock HTTP failure |

## Risk Notes

1. **API Base URL is hardcoded as placeholder** (`https://api.example.com`). Must be updated before deployment. Consider making it configurable via `appsettings.json` or an environment variable.

2. **POST body field name**: The Trello card shows `"MaiboardId"` (typo). I'm using `"MainboardId"`. If the API literally expects `"MaiboardId"`, the serialization in `RegisterSubscriptionAsync` must use that exact spelling (via `[JsonPropertyName("MaiboardId")]` or anonymous object with that name). **Clarify with API team.**

3. **WMI mainboard serial**: Some machines (especially VMs, cheap boards) return `"Default string"`, `"None"`, or empty. The fallback uses `Manufacturer_Product` but this is not unique across identical hardware. Consider using a different hardware ID if this is a problem.

4. **No offline grace period**: If the license server is down, the app won't start. Consider adding a cached validation result with a TTL if this is a concern.

5. **key.txt is plain text**: The license key is stored unencrypted. Acceptable for now but could be obfuscated with DPAPI (`ProtectedData`) if needed.

6. **Async Main**: Changing `void Main` to `async Task Main` is safe in .NET 7+/8+ but verify the project's target framework supports it (it almost certainly does given the repo uses modern .NET).

7. **Thread safety of `GlobalState.MaxInstance`**: Currently a simple static property. Fine for read-after-write pattern (set once at startup, read later). If it ever needs to be updated at runtime, add thread-safety.