namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>
/// Thrown by a durable consumer to say that the first <see cref="Delivered"/> changes of a batch went through
/// and the next one failed (the cause is <see cref="Exception.InnerException"/>). The runner then saves its
/// cursor after the delivered ones and retries only the failing change. A consumer that throws anything
/// else has the batch redelivered one change at a time to find the failing one.
/// </summary>
public sealed class ChangeDeliveryException(int delivered, Exception inner)
    : Exception($"Change {delivered} of the batch failed: {inner.Message}", inner)
{
    /// <summary>How many changes at the start of the batch were delivered.</summary>
    public int Delivered { get; } = delivered;
}
