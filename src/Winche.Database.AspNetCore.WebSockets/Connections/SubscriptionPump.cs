using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using Winche.Database.AspNetCore.WebSockets.Protocol;
using Winche.Database.Documents;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Expansion;
using Winche.Database.Wire;

namespace Winche.Database.AspNetCore.WebSockets.Connections;

/// <summary>
/// Per-subscription delivery for both query (<see cref="RunQueryAsync"/>) and single-document
/// (<see cref="RunDocumentAsync"/>) listeners: the FIRST frame is always a full listen.snapshot
/// (REPLACES client state — including the reset after resume-with-changes); afterwards each
/// runtime snapshot is RE-DIFFED against the last state actually SENT, so deltas are exact
/// relative to client state across runtime coalescing. Empty re-diff ⇒ no frame.
///
/// Claims note: claims are constant for the connection lifetime (fixed at the HTTP upgrade).
/// Under the Winche.Rules guard a listener is authorized once at subscribe time (rules-are-not-filters).
/// <c>ApplyClaims()</c> is called each iteration only to keep this pump's async-context slot
/// coherent — it does not change authorization.
/// </summary>
public static class SubscriptionPump
{
    /// <summary>Minimum client protocol version that receives the `deleted` change kind. v1 clients get `removed`.</summary>
    internal const long DeletedKindMinProtocol = 2;

    /// <summary>Runs a query subscription (unexpected errors aren't logged; see the overload with a logger).</summary>
    public static Task RunQueryAsync(
        string subscriptionId, IQueryListener listener, ConnectionScope scope, WsConnection conn,
        bool supportsDeletedKind, CancellationToken ct) =>
        RunQueryAsync(subscriptionId, listener, scope, conn, supportsDeletedKind, NullLogger.Instance, ct);

    /// <summary>Runs a query subscription; an unexpected error is logged with an error id the client also gets.</summary>
    public static async Task RunQueryAsync(
        string subscriptionId, IQueryListener listener, ConnectionScope scope, WsConnection conn,
        bool supportsDeletedKind, ILogger logger, CancellationToken ct, LiveExpansion? expansion = null)
    {
        try
        {
            await using var exp = expansion;
            await using var snapshots = listener.Snapshots(ct).GetAsyncEnumerator(ct);
            IReadOnlyList<Document>? sent = null;
            var lastToken = 0L;
            var wait = new Waiter();

            while (true)
            {
                scope.ApplyClaims();                              // inert for listener auth (subscribe-time only); kept for context coherence
                if (exp is not null && sent is not null && await wait.ReferencesChangedFirstAsync(snapshots, exp, ct))
                {
                    var refs = await exp.RefreshAsync(ct);
                    if (!refs.IsEmpty) conn.TrySend(IncludedOnly(subscriptionId, refs, sent.Count, lastToken));
                    continue;
                }
                if (!await wait.NextAsync(snapshots)) break;
                var snapshot = snapshots.Current;

                if (snapshot.Current)
                {
                    conn.TrySend(new ListenCurrentMessage
                    {
                        SubscriptionId = subscriptionId,
                        ResumeToken = snapshot.ResumeToken,
                    });
                    continue;
                }

                var included = exp is null ? null : await exp.SetRootsAsync(snapshot.Documents, ct);
                lastToken = snapshot.ResumeToken;
                if (sent is null)
                {
                    conn.TrySend(new ListenSnapshotMessage
                    {
                        SubscriptionId = subscriptionId,
                        Documents = snapshot.Documents,
                        ReadTime = snapshot.ReadTime,
                        ResumeToken = snapshot.ResumeToken,
                        Included = included?.All,
                    });
                    sent = snapshot.Documents;
                    continue;
                }

                var changes = SnapshotDiff.Compute(sent, snapshot.Documents);
                sent = snapshot.Documents;
                if (changes.Count == 0)
                {
                    if (included is { IsEmpty: false }) conn.TrySend(IncludedOnly(subscriptionId, included, sent.Count, lastToken));
                    continue;
                }

                // Batch the existence check for ALL removed changes into one probe so a
                // bulk delete / limit-window churn costs a single DB round-trip, not one
                // per removed doc. If the probe fails (transient DB error), degrade to
                // "removed": never falsely tombstone the client, and keep the subscription
                // alive rather than tearing it down mid-delta.
                IReadOnlySet<string> existing = new HashSet<string>();
                if (supportsDeletedKind)
                {
                    var removedPaths = changes
                        .Where(c => c.Type == ListenChangeType.Removed)
                        .Select(c => c.Document.Path)
                        .ToList();
                    if (removedPaths.Count > 0)
                    {
                        try { existing = await listener.ExistingAsync(removedPaths, ct); }
                        catch (Exception) when (!ct.IsCancellationRequested)
                        {
                            existing = removedPaths.ToHashSet(); // degrade → all "removed"
                        }
                    }
                }

                var wireChanges = new List<WireChange>(changes.Count);
                foreach (var c in changes)
                {
                    var kind = c.Type switch
                    {
                        ListenChangeType.Added => "added",
                        ListenChangeType.Modified => "modified",
                        _ => supportsDeletedKind && !existing.Contains(c.Document.Path)
                            ? "deleted"
                            : "removed",
                    };
                    wireChanges.Add(new WireChange(kind, c.Document, c.OldIndex, c.NewIndex));
                }

                conn.TrySend(new ListenDeltaMessage
                {
                    SubscriptionId = subscriptionId,
                    Changes = wireChanges,
                    Count = snapshot.Documents.Count,
                    ReadTime = snapshot.ReadTime,
                    ResumeToken = snapshot.ResumeToken,
                    Included = Upserted(included),
                    Excluded = Removed(included),
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // listener failed (e.g. invalid stored query): surface once, stream ends; client may re-listen
            SendSubscriptionError(subscriptionId, ex, conn, scope, logger);
        }
    }

    /// <summary>Runs a document subscription (unexpected errors aren't logged; see the overload with a logger).</summary>
    public static Task RunDocumentAsync(
        string subscriptionId, IDocumentListener listener, ConnectionScope scope, WsConnection conn,
        bool supportsDeletedKind, CancellationToken ct) =>
        RunDocumentAsync(subscriptionId, listener, scope, conn, supportsDeletedKind, NullLogger.Instance, ct);

    /// <summary>Runs a document subscription; an unexpected error is logged with an error id the client also gets.</summary>
    public static async Task RunDocumentAsync(
        string subscriptionId, IDocumentListener listener, ConnectionScope scope, WsConnection conn,
        bool supportsDeletedKind, ILogger logger, CancellationToken ct, LiveExpansion? expansion = null)
    {
        try
        {
            await using var exp = expansion;
            await using var snapshots = listener.Snapshots(ct).GetAsyncEnumerator(ct);
            Document? sent = null;
            var sentAny = false;
            var lastToken = 0L;
            var wait = new Waiter();

            while (true)
            {
                scope.ApplyClaims();                              // inert for listener auth (subscribe-time only)
                if (exp is not null && sentAny && await wait.ReferencesChangedFirstAsync(snapshots, exp, ct))
                {
                    var refs = await exp.RefreshAsync(ct);
                    if (!refs.IsEmpty) conn.TrySend(IncludedOnly(subscriptionId, refs, sent is null ? 0 : 1, lastToken));
                    continue;
                }
                if (!await wait.NextAsync(snapshots)) break;
                var snapshot = snapshots.Current;

                if (snapshot.Current)
                {
                    conn.TrySend(new ListenCurrentMessage
                    {
                        SubscriptionId = subscriptionId,
                        ResumeToken = snapshot.ResumeToken,
                    });
                    continue;
                }

                IReadOnlyList<Document> docs = snapshot.Document is { } d ? new[] { d } : Array.Empty<Document>();
                var included = exp is null ? null : await exp.SetRootsAsync(docs, ct);
                lastToken = snapshot.ResumeToken;

                if (!sentAny)
                {
                    conn.TrySend(new ListenSnapshotMessage
                    {
                        SubscriptionId = subscriptionId,
                        Documents = docs,
                        ReadTime = snapshot.ReadTime,
                        ResumeToken = snapshot.ResumeToken,
                        Included = included?.All,
                    });
                    sent = snapshot.Document;
                    sentAny = true;
                    continue;
                }

                var change = DocumentChange(sent, snapshot.Document, supportsDeletedKind);
                sent = snapshot.Document;
                if (change is null)
                {
                    if (included is { IsEmpty: false }) conn.TrySend(IncludedOnly(subscriptionId, included, docs.Count, lastToken));
                    continue;
                }

                conn.TrySend(new ListenDeltaMessage
                {
                    SubscriptionId = subscriptionId,
                    Changes = [change],
                    Count = docs.Count,
                    ReadTime = snapshot.ReadTime,
                    ResumeToken = snapshot.ResumeToken,
                    Included = Upserted(included),
                    Excluded = Removed(included),
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SendSubscriptionError(subscriptionId, ex, conn, scope, logger);
        }
    }

    private static IReadOnlyDictionary<string, Document>? Upserted(IncludedChange? c) => c is { Upserted.Count: > 0 } ? c.Upserted : null;
    private static IReadOnlyList<string>? Removed(IncludedChange? c) => c is { Removed.Count: > 0 } ? c.Removed : null;

    /// <summary>A delta that only changes the included (referenced) documents: the results themselves are as they were.</summary>
    private static ListenDeltaMessage IncludedOnly(string subscriptionId, IncludedChange change, int count, long resumeToken) => new()
    {
        SubscriptionId = subscriptionId,
        Changes = [],
        Count = count,
        ReadTime = DateTimeOffset.UtcNow,
        ResumeToken = resumeToken,
        Included = Upserted(change),
        Excluded = Removed(change),
    };

    /// <summary>
    /// Waits for the subscription's next snapshot or, with expansion, a referenced document changing, whichever is first;
    /// a pending snapshot read is kept for the next turn rather than started twice.
    /// </summary>
    private sealed class Waiter
    {
        private Task<bool>? _next, _refs;

        public async Task<bool> ReferencesChangedFirstAsync<T>(IAsyncEnumerator<T> snapshots, LiveExpansion exp, CancellationToken ct)
        {
            _next ??= snapshots.MoveNextAsync().AsTask();
            _refs ??= exp.Changed.WaitToReadAsync(ct).AsTask();
            var first = await Task.WhenAny(_next, _refs);
            if (first != _refs || _next.IsCompleted) return false;
            _refs = null;
            return true;
        }

        public async Task<bool> NextAsync<T>(IAsyncEnumerator<T> snapshots)
        {
            var next = _next ?? snapshots.MoveNextAsync().AsTask();
            _next = null;
            return await next;
        }
    }

    /// <summary>Maps an exception to a wire error and sends it as the subscription's terminal frame.</summary>
    private static void SendSubscriptionError(string subscriptionId, Exception ex, WsConnection conn, ConnectionScope scope, ILogger logger)
    {
        var error = ErrorMapper.Map(ex, logger, $"WebSocket subscription {subscriptionId}", scope.UserId);
        var details = new JsonObject { ["subscriptionId"] = subscriptionId };
        if (error.Details is { } mapperDetails)
            foreach (var kvp in mapperDetails) details[kvp.Key] = kvp.Value?.DeepClone();
        conn.TrySend(new ErrorMessage
        {
            Error = error with { Message = $"Subscription '{subscriptionId}' failed: {error.Message}", Details = details },
        });
    }

    private static WireChange? DocumentChange(Document? prev, Document? next, bool supportsDeletedKind)
    {
        if (prev is null && next is null) return null;
        if (prev is null) return new WireChange("added", next!, -1, 0);
        if (next is null) return new WireChange(supportsDeletedKind ? "deleted" : "removed", prev, 0, -1);
        if (prev.UpdateTime == next.UpdateTime) return null;
        return new WireChange("modified", next, 0, 0);
    }
}
