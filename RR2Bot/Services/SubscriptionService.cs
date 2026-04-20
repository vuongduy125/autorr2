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
