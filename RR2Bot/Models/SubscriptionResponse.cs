namespace RR2Bot.Models
{
    /// <summary>
    /// Represents the raw API response from api/v1/subcription.
    /// The payload is a base64-encoded JSON string containing the SubscriptionInfo.
    /// </summary>
    public class SubscriptionResponse
    {
        public string Payload { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
    }
}
