using System.Text.Json.Nodes;
using Winche.Database.AspNetCore.WebSockets.Protocol;
using Winche.Database.Documents;
using Winche.Database.Runtime.Listening;
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

    public static async Task RunQueryAsync(
        string subscriptionId, IQueryListener listener, ConnectionScope scope, WsConnection conn,
        bool supportsDeletedKind, CancellationToken ct)
    {
        try
        {
            await using var snapshots = listener.Snapshots(ct).GetAsyncEnumerator(ct);
            IReadOnlyList<Document>? sent = null;

            while (true)
            {
                scope.ApplyClaims();                              // inert for listener auth (subscribe-time only); kept for context coherence
                if (!await snapshots.MoveNextAsync()) break;
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

                if (sent is null)
                {
                    conn.TrySend(new ListenSnapshotMessage
                    {
                        SubscriptionId = subscriptionId,
                        Documents = snapshot.Documents,
                        ReadTime = snapshot.ReadTime,
                        ResumeToken = snapshot.ResumeToken,
                    });
                    sent = snapshot.Documents;
                    continue;
                }

                var changes = SnapshotDiff.Compute(sent, snapshot.Documents);
                sent = snapshot.Documents;
                if (changes.Count == 0) continue;

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
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // listener failed (e.g. invalid stored query): surface once, stream ends; client may re-listen
            SendSubscriptionError(subscriptionId, ex, conn);
        }
    }

    public static async Task RunDocumentAsync(
        string subscriptionId, IDocumentListener listener, ConnectionScope scope, WsConnection conn,
        bool supportsDeletedKind, CancellationToken ct)
    {
        try
        {
            await using var snapshots = listener.Snapshots(ct).GetAsyncEnumerator(ct);
            Document? sent = null;
            var sentAny = false;

            while (true)
            {
                scope.ApplyClaims();                              // inert for listener auth (subscribe-time only)
                if (!await snapshots.MoveNextAsync()) break;
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

                if (!sentAny)
                {
                    conn.TrySend(new ListenSnapshotMessage
                    {
                        SubscriptionId = subscriptionId,
                        Documents = docs,
                        ReadTime = snapshot.ReadTime,
                        ResumeToken = snapshot.ResumeToken,
                    });
                    sent = snapshot.Document;
                    sentAny = true;
                    continue;
                }

                var change = DocumentChange(sent, snapshot.Document, supportsDeletedKind);
                sent = snapshot.Document;
                if (change is null) continue;

                conn.TrySend(new ListenDeltaMessage
                {
                    SubscriptionId = subscriptionId,
                    Changes = [change],
                    Count = docs.Count,
                    ReadTime = snapshot.ReadTime,
                    ResumeToken = snapshot.ResumeToken,
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SendSubscriptionError(subscriptionId, ex, conn);
        }
    }

    /// <summary>Maps an exception to a wire error and sends it as the subscription's terminal frame.</summary>
    private static void SendSubscriptionError(string subscriptionId, Exception ex, WsConnection conn)
    {
        var error = ErrorMapper.Map(ex);
        var details = new JsonObject { ["subscriptionId"] = subscriptionId };
        if (error.Details is { } mapperDetails)
            foreach (var kvp in mapperDetails) details[kvp.Key] = kvp.Value?.DeepClone();
        conn.TrySend(new ErrorMessage
        {
            Status = error.Status,
            Message = $"Subscription '{subscriptionId}' failed: {error.Message}",
            Details = details,
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
