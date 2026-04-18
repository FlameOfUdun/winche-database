namespace WincheDatabase.WS.Abstraction;

public interface ISubscriptionConnectionMap
{
    void Track(string connectionId, string subscriptionId);
    bool TryGetOwner(string subscriptionId, out string? connectionId);
    void Untrack(string connectionId, string subscriptionId);
    IReadOnlyList<string> UntrackAll(string connectionId);
}
