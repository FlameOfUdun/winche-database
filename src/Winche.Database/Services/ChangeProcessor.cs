using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using Winche.Database.Interfaces;
using Winche.Database.Models;
using Winche.Database.Core.Models;
using Winche.Database.Infrastructure;

namespace Winche.Database.Services;

public sealed class ChangeProcessor(
    ISubscriptionRegistry store,
    IDocumentManager manager,
    IEventChannel channel,
    ILogger<ChangeProcessor> logger
) : IChangeProcessor
{
    private const int MaxParallelism = 4;

    public async Task ProcessAsync(DocumentChange change, CancellationToken ct = default)
    {
        var groups = store.GetGroupsByCollection(change.Collection).ToList();
        if (groups.Count == 0)
            return;

        if (change.Type != DocumentChangeType.Removed)
        {
            var document = await manager.GetUnprotectedAsync(change.Path, ct);
            if (document != null)
                change = change with { Data = document.Data };
        }

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxParallelism,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(groups, options, async (group, token) =>
        {
            try
            {
                await ProcessGroupAsync(group, change, token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing change for query group {GroupKey}", group.Key);
            }
        });
    }

    private async Task ProcessGroupAsync(QueryGroup group, DocumentChange change, CancellationToken ct)
    {
        if (!QueryMatcher.CouldAffect(group.Query, group.Snapshot, change))
            return;

        while (true)
        {
            var result = await manager.QueryAsync(group.Query, ct);
            var newIds = result.Documents.Select(d => d.Id).ToImmutableHashSet();

            var current = store.TryGetGroup(group.Key);
            if (current == null)
                return;

            var oldIds = current.Snapshot.DocumentIds;
            var newSnapshot = new QuerySnapshot { DocumentIds = newIds };

            if (!store.TryUpdateGroupSnapshot(group.Key, current.Snapshot, newSnapshot))
            {
                var refreshed = store.TryGetGroup(group.Key);
                if (refreshed == null)
                    return;
                group = refreshed;
                continue;
            }

            var subscriptionIds = store.GetSubscriptionIds(group.Key).ToList();
            foreach (var subId in subscriptionIds)
            {
                var events = BuildEvents(subId, change, oldIds, newIds, result.Documents);
                if (events.Count > 0)
                {
                    await channel.WriteAsync(events, ct);
                }
            }

            return;
        }
    }

    private static List<SubscriptionEvent> BuildEvents(
        string subscriptionId,
        DocumentChange change,
        ImmutableHashSet<string> oldIds,
        ImmutableHashSet<string> newIds,
        IReadOnlyList<Document> newDocuments)
    {
        var events = new List<SubscriptionEvent>();
        var docById = newDocuments.ToDictionary(d => d.Id);

        foreach (var id in newIds.Except(oldIds))
        {
            events.Add(new SubscriptionEvent
            {
                SubscriptionId = subscriptionId,
                Change = new QueryChange { Type = QueryChangeType.Added, DocumentId = id, Document = docById[id] },
            });
        }

        foreach (var id in oldIds.Except(newIds))
        {
            events.Add(new SubscriptionEvent
            {
                SubscriptionId = subscriptionId,
                Change = new QueryChange { Type = QueryChangeType.Removed, DocumentId = id },
            });
        }

        if (change.Type == DocumentChangeType.Modified
            && newIds.Contains(change.Id)
            && oldIds.Contains(change.Id))
        {
            events.Add(new SubscriptionEvent
            {
                SubscriptionId = subscriptionId,
                Change = new QueryChange { Type = QueryChangeType.Modified, DocumentId = change.Id, Document = docById[change.Id] },
            });
        }

        return events;
    }
}
