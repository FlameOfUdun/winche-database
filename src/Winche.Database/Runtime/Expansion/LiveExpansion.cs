using System.Threading.Channels;
using Winche.Database.Documents;
using Winche.Database.Runtime.Listening;

namespace Winche.Database.Runtime.Expansion;

/// <summary>What changed in a subscription's included documents since the last time: new or changed ones, and gone ones.</summary>
public sealed record IncludedChange(IReadOnlyDictionary<string, Document> Upserted, IReadOnlyList<string> Removed, IReadOnlyDictionary<string, Document> All)
{
    /// <summary>Resolved values (<c>kind:value</c>) new or changed, gone, and all of them.</summary>
    public IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject> ResolvedUpserted { get; init; } = new Dictionary<string, System.Text.Json.Nodes.JsonObject>();
    public IReadOnlyList<string> ResolvedRemoved { get; init; } = [];
    public IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject> AllResolved { get; init; } = new Dictionary<string, System.Text.Json.Nodes.JsonObject>();

    public bool IsEmpty => Upserted.Count == 0 && Removed.Count == 0 && ResolvedUpserted.Count == 0 && ResolvedRemoved.Count == 0;
}

/// <summary>
/// Keeps a live subscription's expanded references current: after each result (<see cref="SetRootsAsync"/>) it expands
/// them again, and it listens to every referenced document, signalling <see cref="Changed"/> when one changes (or is
/// created or deleted), so the subscriber can <see cref="RefreshAsync"/> and send what changed. Reads and listens as
/// the caller: a referenced document they may not read is never included or listened to.
/// </summary>
public sealed class LiveExpansion : IAsyncDisposable
{
    private readonly IDocumentDatabase db;
    private readonly ExpandRequest request;
    private readonly ReferenceResolvers? resolvers;
    private readonly IDisposable? _resolverWatch;
    private Dictionary<string, System.Text.Json.Nodes.JsonObject> _sentResolved = new(StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="resolvers"/> resolve kinded paths (<c>file:</c>, <c>user:</c>) as the caller; their change
    /// notifications refresh this subscription too.
    /// </summary>
    public LiveExpansion(IDocumentDatabase db, ExpandRequest request, ReferenceResolvers? resolvers = null)
    {
        this.db = db;
        this.request = request;
        this.resolvers = resolvers;
        if (resolvers is not null && request.Kinds.Any()) _resolverWatch = resolvers.Watch(request.Kinds, () => _changed.Writer.TryWrite(true));
    }

    private readonly Channel<bool> _changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, Watch> _watching = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private IReadOnlyList<Document?> _roots = [];
    private Dictionary<string, Document> _sent = new(StringComparer.Ordinal);

    private sealed record Watch(IDocumentListener? Listener, Task Pump);

    /// <summary>Has an item when a referenced document changed since the last refresh.</summary>
    public ChannelReader<bool> Changed => _changed.Reader;

    /// <summary>The subscription's documents changed: expand theirs.</summary>
    public Task<IncludedChange> SetRootsAsync(IReadOnlyList<Document?> roots, CancellationToken ct)
    {
        _roots = roots;
        return RefreshAsync(ct);
    }

    /// <summary>Expands again, follows the referenced documents, and says what changed since the last call.</summary>
    public async Task<IncludedChange> RefreshAsync(CancellationToken ct)
    {
        while (_changed.Reader.TryRead(out _)) { }   // this refresh covers them
        var expansion = await ReferenceExpander.ExpandAsync(db, _roots, request, ct, resolvers?.For(db));
        await WatchAsync(expansion.Referenced, ct);
        var upserted = expansion.Included
            .Where(kv => !_sent.TryGetValue(kv.Key, out var before) || before.UpdateTime != kv.Value.UpdateTime || before.Version != kv.Value.Version)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var removed = _sent.Keys.Where(k => !expansion.Included.ContainsKey(k)).ToList();
        _sent = new Dictionary<string, Document>(expansion.Included, StringComparer.Ordinal);
        var resolvedUpserted = expansion.Resolved
            .Where(kv => !_sentResolved.TryGetValue(kv.Key, out var before) || !System.Text.Json.Nodes.JsonNode.DeepEquals(before, kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var resolvedRemoved = _sentResolved.Keys.Where(k => !expansion.Resolved.ContainsKey(k)).ToList();
        _sentResolved = new Dictionary<string, System.Text.Json.Nodes.JsonObject>(expansion.Resolved, StringComparer.Ordinal);
        return new IncludedChange(upserted, removed, _sent)
        {
            ResolvedUpserted = resolvedUpserted, ResolvedRemoved = resolvedRemoved, AllResolved = _sentResolved,
        };
    }

    private async Task WatchAsync(IReadOnlySet<string> paths, CancellationToken ct)
    {
        foreach (var gone in _watching.Keys.Where(p => !paths.Contains(p)).ToList())
        {
            if (_watching[gone].Listener is { } l) await l.DisposeAsync();
            _watching.Remove(gone);
        }
        foreach (var path in paths)
        {
            if (_watching.ContainsKey(path)) continue;
            if (_watching.Count >= ExpandRequest.MaxDocuments) break;
            IDocumentListener? listener = null;
            try { listener = await db.ListenToDocumentAsync(path, null, ct); }
            catch (Exception ex) when (ex is Authorization.AccessDeniedException or WincheDeniedException or RuntimeException) { }   // can't follow it: stays a pointer
            var known = _sent.GetValueOrDefault(path);
            _watching[path] = new Watch(listener, listener is null ? Task.CompletedTask : PumpAsync(listener, known, _stop.Token));
        }
    }

    /// <summary>Signals a change for every snapshot after the first (and for the first, if it's newer than what was included).</summary>
    private async Task PumpAsync(IDocumentListener listener, Document? known, CancellationToken ct)
    {
        try
        {
            var first = true;
            await foreach (var snapshot in listener.Snapshots(ct))
            {
                if (snapshot.Current) continue;
                if (first)
                {
                    first = false;
                    if (snapshot.Document?.UpdateTime == known?.UpdateTime) continue;
                }
                _changed.Writer.TryWrite(true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _changed.Writer.TryWrite(true); }   // the next refresh finds out what's readable now
    }

    public async ValueTask DisposeAsync()
    {
        _resolverWatch?.Dispose();
        await _stop.CancelAsync();
        foreach (var w in _watching.Values)
            if (w.Listener is { } l) await l.DisposeAsync();
        try { await Task.WhenAll(_watching.Values.Select(w => w.Pump)); } catch { }
        _watching.Clear();
        _stop.Dispose();
    }
}
