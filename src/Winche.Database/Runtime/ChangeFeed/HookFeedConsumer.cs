using Winche.Database.Interfaces;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>
/// Hooks as feed consumers (spec §5): post-commit, at-least-once, any-node — hooks should be
/// idempotent. Reuses the existing dispatcher/processor queueing.
/// </summary>
public sealed class HookFeedConsumer(IHookInvocationDispatcher dispatcher) : IChangeFeedConsumer
{
    public Task OnBatchAsync(ChangeBatch batch, CancellationToken ct)
    {
        foreach (var record in batch.Records)
        {
            var path = record.Path;
            switch (record.Type)
            {
                // When TryGetValue guards fail (doc absent from batch.Documents), the document was
                // deleted again before the batch fetch — skip the set/update hook silently.
                // The later Removed record still fires the delete hook, so no hook is permanently lost.
                case ChangeType.Added when batch.Documents.TryGetValue(path, out var added):
                    dispatcher.Enqueue(path, (h, t) => h.OnDocumentSetAsync(path, added, t));
                    break;
                case ChangeType.Modified when batch.Documents.TryGetValue(path, out var modified):
                    dispatcher.Enqueue(path, (h, t) => h.OnDocumentUpdatedAsync(path, modified, t));
                    break;
                case ChangeType.Removed:
                    dispatcher.Enqueue(path, (h, t) => h.OnDocumentDeletedAsync(path, t));
                    break;
            }
        }
        return Task.CompletedTask;
    }
}
