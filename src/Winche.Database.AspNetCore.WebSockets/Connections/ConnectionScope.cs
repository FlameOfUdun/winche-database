using Winche.Database.AspNetCore.Abstraction;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Listening;

namespace Winche.Database.AspNetCore.WebSockets.Connections;

/// <summary>
/// Per-socket state: claims fixed at connect time, open transaction ids, active subscriptions.
/// Claims are set once at upgrade from <c>HttpContext.User</c> and never change; the connection
/// must be re-established (client reconnects with a fresh token) to change identity.
///
/// ApplyClaims() pins the connection's claims onto the DocumentClaimsAccessor's AsyncLocal —
/// call it before every IDocumentDatabase touch (request handlers and pump iterations).
///
/// Claims-accessor design: DocumentClaimsAccessor.SetClaims stores claims in
/// AsyncLocal&lt;IReadOnlyDictionary&lt;string, object?&gt;?&gt;. Each async context (message-loop
/// and each pump Task) sets its own AsyncLocal slot right before evaluation — this is correct
/// and thread-safe. No fallback ClaimsScopedEvaluator is required.
///
/// Disposal: cancel pumps, dispose listeners, best-effort rollback of open transactions.
/// </summary>
public sealed class ConnectionScope(
    string connectionId,
    IDocumentDatabase db,
    DocumentClaimsAccessor claimsAccessor) : IAsyncDisposable
{
    public sealed record Subscription(IQueryListener Listener, Task Pump, CancellationTokenSource Cts);

    private volatile IReadOnlyDictionary<string, object?> _claims = new Dictionary<string, object?>();

    public string ConnectionId { get; } = connectionId;
    public IDocumentDatabase Db { get; } = db;
    public HashSet<string> Transactions { get; } = [];
    public Dictionary<string, Subscription> Subscriptions { get; } = new(StringComparer.Ordinal);
    public object Gate { get; } = new();

    public void SetClaims(IReadOnlyDictionary<string, object?> claims) => _claims = claims;

    /// <summary>
    /// Pushes the connection's current claims into the DocumentClaimsAccessor's AsyncLocal for
    /// this async context. Must be called before any IDocumentDatabase operation — in the message
    /// loop and at the top of each SubscriptionPump iteration.
    /// </summary>
    public void ApplyClaims() => claimsAccessor.SetClaims(_claims);

    public async ValueTask DisposeAsync()
    {
        List<Subscription> subs;
        List<string> txs;
        lock (Gate)
        {
            subs = [.. Subscriptions.Values];
            Subscriptions.Clear();
            txs = [.. Transactions];
            Transactions.Clear();
        }

        foreach (var sub in subs)
        {
            sub.Cts.Cancel();
            try { await sub.Listener.DisposeAsync(); } catch { /* best effort */ }
        }
        foreach (var sub in subs)
        {
            try { await sub.Pump; } catch { /* pump exceptions already surfaced */ }
            sub.Cts.Dispose();
        }

        foreach (var tx in txs)
        {
            try { await Db.RollbackTransactionAsync(tx); } catch { /* best effort */ }
        }
    }
}
