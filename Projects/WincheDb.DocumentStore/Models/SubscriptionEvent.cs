namespace WincheDb.DocumentStore.Models;

public record SubscriptionEvent
{
    public required string SubscriptionId { get; set; } 
    public required QueryChange Change { get; set; }
}
