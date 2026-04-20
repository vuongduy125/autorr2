namespace RR2Bot.Models
{
    public class SubscriptionInfo
    {
        public bool IsActive { get; set; }
        public int MaxInstance { get; set; }
        public string MainboardId { get; set; } = string.Empty;
    }
}
