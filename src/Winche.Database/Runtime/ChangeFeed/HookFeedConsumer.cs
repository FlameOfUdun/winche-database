using Winche.Database.Abstraction;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>
/// Hooks as feed consumers (spec §5): post-commit, at-least-once, any-node — hooks must be
/// idempotent. Executes hooks DIRECTLY and SEQUENTIALLY so that failures propagate to the
/// DurableConsumerRunner, which retries the same batch with capped backoff.
/// Cursor advances ONLY after all hooks in a batch succeed — true at-least-once end-to-end.
/// A single failing hook blocks this consumer (logged by the runner); it does not affect
/// other consumers or listeners.
/// </summary>
public sealed class HookFeedConsumer(
    IEnumerable<HookRegistration> hooks
) : IChangeFeedConsumer
{
    private readonly IReadOnlyList<HookRegistration> _hooks = hooks.ToList();

    public string? DurableName => "hooks";

    public async Task OnBatchAsync(ChangeBatch batch, CancellationToken ct)
    {
        foreach (var record in batch.Records)
        {
            var path = record.Path;
            foreach (var registration in _hooks)
            {
                if (!Winche.Rules.Matching.PathMatcher.IsMatch(registration.Path, path)) continue;

                var hook = registration.Hook;
                switch (record.Type)
                {
                    // When TryGetValue guards fail (doc absent from batch.Documents), the document was
                    // deleted again before the batch fetch — skip the set/update hook silently.
                    // The later Removed record still fires the delete hook, so no hook is permanently lost.
                    case ChangeType.Added when batch.Documents.TryGetValue(path, out var added):
                        await hook.OnDocumentSetAsync(path, added, ct);
                        break;
                    case ChangeType.Modified when batch.Documents.TryGetValue(path, out var modified):
                        await hook.OnDocumentUpdatedAsync(path, modified, ct);
                        break;
                    case ChangeType.Removed:
                        await hook.OnDocumentDeletedAsync(path, ct);
                        break;
                }
            }
        }
    }
}
