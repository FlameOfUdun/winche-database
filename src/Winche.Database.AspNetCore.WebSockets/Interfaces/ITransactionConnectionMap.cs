namespace Winche.Database.AspNetCore.WebSockets.Interfaces
{
    public interface ITransactionConnectionMap
    {
        void Track(string connectionId, string transactionId);
        bool TryGetOwner(string transactionId, out string? connectionId);
        void Untrack(string connectionId, string transactionId);
        IReadOnlyList<string> UntrackAll(string connectionId);
    }
}
