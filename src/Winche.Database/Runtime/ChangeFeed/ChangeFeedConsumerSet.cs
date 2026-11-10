using Winche.Database.Abstraction;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>
/// Every change-feed consumer the host runs: the registered <see cref="IChangeFeedConsumer"/>s plus one
/// <see cref="HookFeedConsumer"/> per hook type. The pump, the pruner (which must keep what durable
/// consumers still need) and <see cref="ChangeFeedConsumers"/> all read it.
/// </summary>
public sealed class ChangeFeedConsumerSet
{
    public ChangeFeedConsumerSet(IEnumerable<IChangeFeedConsumer> consumers, IEnumerable<HookRegistration> hooks)
    {
        All = [.. consumers, .. HookFeedConsumer.PerHookType(hooks)];
        Durable = [.. All.Where(c => c.DurableName is not null)];
    }

    public IReadOnlyList<IChangeFeedConsumer> All { get; }

    /// <summary>The consumers with a persisted cursor.</summary>
    public IReadOnlyList<IChangeFeedConsumer> Durable { get; }

    public IReadOnlyList<string> DurableNames => [.. Durable.Select(c => c.DurableName!)];

    /// <summary>The per-type hook cursors.</summary>
    public IReadOnlyList<string> HookNames =>
        [.. DurableNames.Where(n => n.StartsWith(HookFeedConsumer.DurableNamePrefix, StringComparison.Ordinal))];
}
