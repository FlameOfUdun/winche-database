using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Winche.Database.Authorization;

namespace Winche.Database.Runtime.Expansion;

/// <summary>
/// The app's <see cref="IWincheReferenceResolver"/>s for <c>expand</c> (<c>file:</c> from Winche.Storage, <c>user:</c>
/// from Winche Auth, the app's own), who they resolve for, and their change notifications for live subscriptions
/// (one LISTEN connection per channel, shared by every subscription).
/// </summary>
public sealed class ReferenceResolvers : IAsyncDisposable
{
    private readonly IRuleClaimsAccessor _claims;
    private readonly NpgsqlDataSource _db;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Channel> _channels = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();

    public ReferenceResolvers(IEnumerable<IWincheReferenceResolver> resolvers, IRuleClaimsAccessor claims, NpgsqlDataSource db, ILogger<ReferenceResolvers>? logger = null)
    {
        All = [.. resolvers];
        _claims = claims;
        _db = db;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        if (All.GroupBy(r => r.Kind).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidOperationException($"Two reference resolvers for '{twice.Key}:'.");
    }

    public IReadOnlyList<IWincheReferenceResolver> All { get; }

    /// <summary>Resolution for reads through <paramref name="db"/>: as the caller through the rules guard, as the app otherwise.</summary>
    public ReferenceResolution For(IDocumentDatabase db) =>
        new(All, db is RuleGuardedDocumentDatabase ? new ReferenceCaller(_claims.GetClaims(), false) : ReferenceCaller.Server);

    /// <summary>Calls <paramref name="changed"/> whenever something resolvers of <paramref name="kinds"/> resolve may have changed.</summary>
    public IDisposable Watch(IEnumerable<string> kinds, Action changed)
    {
        var channels = All.Where(r => kinds.Contains(r.Kind)).Select(r => r.ChangeChannel).OfType<string>().Distinct().ToList();
        foreach (var name in channels) _channels.GetOrAdd(name, n => new Channel(n, this)).Changed += changed;
        return new Unwatch(() => { foreach (var name in channels) if (_channels.TryGetValue(name, out var c)) c.Changed -= changed; });
    }

    private sealed class Unwatch(Action undo) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) undo(); }
    }

    /// <summary>One LISTEN on a channel, kept up (and re-established) while the app runs.</summary>
    private sealed class Channel
    {
        public event Action? Changed;

        public Channel(string name, ReferenceResolvers owner) => _ = RunAsync(name, owner);

        private async Task RunAsync(string name, ReferenceResolvers owner)
        {
            var ct = owner._stop.Token;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var conn = await owner._db.OpenConnectionAsync(ct);
                    conn.Notification += (_, _) => Changed?.Invoke();
                    await using (var listen = new NpgsqlCommand($"LISTEN \"{name.Replace("\"", "\"\"")}\"", conn)) await listen.ExecuteNonQueryAsync(ct);
                    Changed?.Invoke();   // anything missed while (re)connecting
                    while (!ct.IsCancellationRequested) await conn.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    owner._logger.LogWarning(ex, "Listening for {Channel} (expanded references) failed; trying again.", name);
                    try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { break; }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _stop.Dispose();
    }
}
