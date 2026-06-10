using System.Text.Json.Nodes;
using Winche.Database.AspNetCore.WebSockets.Protocol;
using Winche.Database.Documents;
using Winche.Database.Runtime.Listening;
using Winche.Database.Wire;

namespace Winche.Database.AspNetCore.WebSockets.Connections;

/// <summary>
/// Per-subscription delivery: the FIRST frame is always a full listen.snapshot
/// (REPLACES client state — including the reset after resume-with-changes); afterwards each
/// runtime snapshot is RE-DIFFED against the last list actually SENT, so deltas are exact
/// relative to client state across runtime coalescing. Empty re-diff ⇒ no frame.
///
/// Claims note: claims are constant for the connection lifetime (fixed at the HTTP upgrade).
/// Under the Winche.Rules guard a listener is authorized once at subscribe time (rules-are-not-filters).
/// <c>ApplyClaims()</c> is called each iteration only to keep this pump's async-context slot
/// coherent — it does not change authorization.
/// </summary>
public static class SubscriptionPump
{
    public static async Task RunAsync(
        string subscriptionId, IQueryListener listener, ConnectionScope scope, WsConnection conn, CancellationToken ct)
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

                conn.TrySend(new ListenDeltaMessage
                {
                    SubscriptionId = subscriptionId,
                    Changes = [.. changes.Select(c => new WireChange(
                        c.Type switch
                        {
                            ListenChangeType.Added => "added",
                            ListenChangeType.Modified => "modified",
                            _ => "removed",
                        },
                        c.Document, c.OldIndex, c.NewIndex))],
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
            var error = ErrorMapper.Map(ex);
            var details = new JsonObject { ["subscriptionId"] = subscriptionId };
            if (error.Details is { } mapperDetails)
            {
                foreach (var kvp in mapperDetails)
                    details[kvp.Key] = kvp.Value?.DeepClone();
            }
            conn.TrySend(new ErrorMessage
            {
                Status = error.Status,
                Message = $"Subscription '{subscriptionId}' failed: {error.Message}",
                Details = details,
            });
        }
    }
}
