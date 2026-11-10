using Winche.Database.Abstraction;
using Winche.Database.Documents;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>
/// Hooks as feed consumers (spec §5): post-commit, at-least-once, any-node — hooks must be
/// idempotent. Executes hooks DIRECTLY and SEQUENTIALLY so that failures propagate to the
/// DurableConsumerRunner, which retries the failing change with capped backoff.
/// Cursor advances ONLY after the hooks for a change succeed — true at-least-once end-to-end.
///
/// The host runs one consumer per hook type (<see cref="PerHookType"/>), each with its own cursor, so a
/// failing hook holds back only itself, never the other hooks.
/// </summary>
public sealed class HookFeedConsumer(
    IEnumerable<HookRegistration> hooks,
    string durableName = HookFeedConsumer.LegacyDurableName
) : IChangeFeedConsumer
{
    /// <summary>The single cursor every hook shared before 10.4.0; per-type cursors start from it on upgrade.</summary>
    public const string LegacyDurableName = "hooks";

    /// <summary>The prefix of every per-type hook cursor.</summary>
    public const string DurableNamePrefix = "hooks:";

    private readonly IReadOnlyList<HookRegistration> _hooks = hooks.ToList();

    public string? DurableName => durableName;

    public bool WantsSnapshots => _hooks.Any(r => r.WithSnapshots);

    /// <summary>
    /// The cursor name for hooks of <paramref name="hookType"/>: <c>hooks:</c> + its full name. Renaming the
    /// class or its namespace therefore gives it a new cursor, which starts from now.
    /// </summary>
    public static string DurableNameFor(Type hookType) => DurableNamePrefix + (hookType.FullName ?? hookType.Name);

    /// <summary>One consumer per hook type (all of a type's registrations share it), in registration order.</summary>
    public static IReadOnlyList<HookFeedConsumer> PerHookType(IEnumerable<HookRegistration> registrations) =>
        [.. registrations
            .GroupBy(r => r.Hook.GetType())
            .Select(g => new HookFeedConsumer(g, DurableNameFor(g.Key)))];

    public async Task OnBatchAsync(ChangeBatch batch, CancellationToken ct)
    {
        for (var i = 0; i < batch.Records.Count; i++)
        {
            try
            {
                await DeliverAsync(batch.Records[i], batch, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw new ChangeDeliveryException(i, ex);
            }
        }
    }

    private async Task DeliverAsync(ChangeRecord record, ChangeBatch batch, CancellationToken ct)
    {
        var path = record.Path;
        foreach (var registration in _hooks)
        {
            if (!Winche.Rules.Matching.PathMatcher.IsMatch(registration.Path, path)) continue;

            // With snapshots: the versions this change wrote. Without: the current document (null if it's
            // gone again before delivery — a set/update hook then waits for the later Removed record).
            Document? before = null, after;
            if (registration.WithSnapshots && batch.Snapshots.TryGetValue(record.Seq, out var snapshot))
                (before, after) = snapshot;
            else
                after = record.Type == ChangeType.Removed ? null : batch.Documents.GetValueOrDefault(path);
            await registration.Hook.OnChangeAsync(new DocumentChange(record.Type, path, before, after, record.Version), ct);
        }
    }
}
