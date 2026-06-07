using System.Runtime.CompilerServices;
using Winche.Database.Documents;
using Winche.Sentinel.Interfaces;
using Winche.Sentinel.Models;

namespace Winche.Database.Runtime.Listening;

/// <summary>
/// Filters each core snapshot through read rules and RE-DIFFS the filtered state so
/// indices stay correct (spec §5). Claims are whatever the Sentinel accessor holds at
/// delivery time — per-connection claim scoping is the WS refactor's concern.
/// </summary>
public sealed class GuardedQueryListener(IQueryListener inner, IAccessRuleEvaluator<Document> evaluator)
    : IQueryListener
{
    public async IAsyncEnumerable<QuerySnapshot> Snapshots([EnumeratorCancellation] CancellationToken ct = default)
    {
        IReadOnlyList<Document> previous = [];
        var first = true;

        await foreach (var snapshot in inner.Snapshots(ct))
        {
            var filtered = new List<Document>(snapshot.Documents.Count);
            foreach (var doc in snapshot.Documents)
            {
                try
                {
                    await evaluator.EvaluateAsync(AccessOperation.Read, doc.Path, null,
                        _ => Task.FromResult<Document?>(doc), ct);
                    filtered.Add(doc);
                }
                catch (AccessDeniedException) { }
                catch (NoRulesMatchedException) { }
            }

            var changes = SnapshotDiff.Compute(previous, filtered);
            previous = filtered;

            if (changes.Count == 0 && !first) continue;
            first = false;
            yield return new QuerySnapshot(filtered, changes, snapshot.ReadTime, snapshot.ResumeToken);
        }
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
