namespace WincheDatabase.Store.Models;

public record SubscriptionEvent
{
    public required string SubscriptionId { get; set; } 
    public required QueryChange Change { get; set; }
}
