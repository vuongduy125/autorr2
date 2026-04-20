using System.Management;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RR2Bot.Models;

namespace RR2Bot.Services
{
    public class SubscriptionService
    {
        private const string ApiBaseUrl = "https://h2n8n.store"; // TODO: configure
        private static readonly string KeyFilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "key.txt");

        private static readonly HttpClient _httpClient = new();
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private CancellationTokenSource? _heartbeatCts;
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(30);

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
        /// Decodes a base64-encoded payload from the API response and deserializes it into SubscriptionInfo.
        /// The API returns: { "payload": "base64...", "signature": "..." }
        /// The payload decodes to JSON like: {"IsActive":true,"MaxInstance":3,"MainboardId":"111","ExpiresAt":1777309151}
        /// </summary>
        private SubscriptionInfo DecodeSubscriptionResponse(string responseJson)
        {
            var envelope = JsonSerializer.Deserialize<SubscriptionResponse>(responseJson, _jsonOptions)
                           ?? throw new Exception("Invalid subscription response envelope.");

            if (string.IsNullOrEmpty(envelope.Payload))
                throw new Exception("Subscription response payload is empty.");

            var payloadBytes = Convert.FromBase64String(envelope.Payload);
            var payloadJson = Encoding.UTF8.GetString(payloadBytes);

            return JsonSerializer.Deserialize<SubscriptionInfo>(payloadJson, _jsonOptions)
                   ?? throw new Exception("Failed to deserialize subscription payload.");
        }

        /// <summary>
        /// GET api/v1/subcription/{key}
        /// </summary>
        public async Task<SubscriptionInfo> GetSubscriptionAsync(string key, string mainboardId)
        {
            var response = await _httpClient.GetAsync($"{ApiBaseUrl}/api/v1/subcription/{key}?mainboard_id={mainboardId}");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return DecodeSubscriptionResponse(json);
        }

        /// <summary>
        /// POST api/v1/subcription/{key} with MainboardId body
        /// </summary>
        public async Task<SubscriptionInfo> RegisterSubscriptionAsync(string key, string mainboardId)
        {
            var body = new { mainboard_id = mainboardId };
            var content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync($"{ApiBaseUrl}/api/v1/subcription/{key}", content);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return DecodeSubscriptionResponse(json);
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
                    info = await GetSubscriptionAsync(key, localMainboardId);
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

        /// <summary>
        /// Starts a background heartbeat that checks the subscription every 30 minutes.
        /// If the subscription becomes inactive, shows a message and exits the application.
        /// </summary>
        public void StartHeartbeat(string key)
        {
            StopHeartbeat();
            _heartbeatCts = new CancellationTokenSource();
            var ct = _heartbeatCts.Token;

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(HeartbeatInterval, ct);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }

                    try
                    {
                        var localMainboardId = GetLocalMainboardId();
                        var info = await GetSubscriptionAsync(key, localMainboardId);

                        if (!info.IsActive)
                        {
                            // Must show message on UI thread, then exit
                            if (Application.OpenForms.Count > 0)
                            {
                                Application.OpenForms[0]!.Invoke(() =>
                                {
                                    MessageBox.Show(
                                        "Your subscription is no longer active. The application will now close.",
                                        "Subscription Expired",
                                        MessageBoxButtons.OK,
                                        MessageBoxIcon.Warning);
                                    Application.Exit();
                                });
                            }
                            else
                            {
                                Application.Exit();
                            }
                            break;
                        }

                        // Update MaxInstance in case it changed
                        GlobalState.MaxInstance = info.MaxInstance;
                    }
                    catch
                    {
                        // Silently ignore heartbeat errors (network issues, etc.)
                        // The app continues running; next heartbeat will retry.
                    }
                }
            }, ct);
        }

        /// <summary>
        /// Stops the background heartbeat.
        /// </summary>
        public void StopHeartbeat()
        {
            _heartbeatCts?.Cancel();
            _heartbeatCts?.Dispose();
            _heartbeatCts = null;
        }
    }
}
