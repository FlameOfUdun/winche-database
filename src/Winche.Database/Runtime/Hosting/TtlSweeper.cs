using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;
using Winche.Database.Querying.Sql;
using Winche.Database.Runtime.Ttl;
using Winche.Database.Runtime.Writes;

namespace Winche.Database.Runtime.Hosting;

/// <summary>
/// Deletes documents whose registered TTL field is in the past. Best-effort — correctness never
/// depends on it. Deletes route through the write path (so change-feed "removed" rows emit and
/// listeners/hooks observe them) and use the rule-free core (system-initiated, bypasses access rules).
/// Whether a delete cascades to subcollections is controlled by <see cref="TtlConfig.CascadeDelete"/>.
/// </summary>
public sealed class TtlSweeper(
    DocumentDatabase core,
    [FromKeyedServices(ServiceKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source,
    IEnumerable<TtlPolicy> policies,
    IOptions<WincheDatabaseOptions> options,
    ILogger<TtlSweeper> logger) : BackgroundService
{
    private readonly IReadOnlyList<TtlPolicy> _policies = policies.ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A zero/negative interval would throw in PeriodicTimer (stopping the sweeper permanently);
        // fail loudly and stay down rather than crash silently.
        if (options.Value.Ttl.SweepInterval <= TimeSpan.Zero)
        {
            logger.LogError("TtlConfig.SweepInterval must be positive; TTL sweeper will not run");
            return;
        }

        using var timer = new PeriodicTimer(options.Value.Ttl.SweepInterval);
        while (await Wait(timer, stoppingToken))
        {
            try
            {
                var deleted = await SweepOnceAsync(stoppingToken);
                if (deleted > 0)
                    logger.LogInformation("TTL-deleted {Count} documents", deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "TTL sweep failed");
            }
        }
    }

    /// <summary>Runs one full pass over all policies, draining each collection in batches. Returns the total deleted.</summary>
    internal async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        // Clamp to [1, write-batch cap]: a BatchSize above WriteValidator.MaxBatchSize would make every
        // delete batch exceed the write limit and throw INVALID_ARGUMENT — silently disabling TTL. The
        // low bound guards a misconfigured LIMIT 0/negative.
        var batchSize = Math.Clamp(options.Value.Ttl.BatchSize, 1, WriteValidator.MaxBatchSize);
        var cascade = options.Value.Ttl.CascadeDelete;
        var total = 0;
        foreach (var policy in _policies)
        {
            while (true)
            {
                var paths = await SelectExpiredPathsAsync(policy, batchSize, ct);
                if (paths.Count == 0)
                    break;
                // No transaction spans the SELECT and the DELETE: a doc sampled as expired could be
                // updated (TTL reset) before the delete lands, and is deleted anyway. Intentional —
                // TTL is best-effort; a spanning txn would hold open across a large batch.
                await core.WriteAsync([.. paths.Select(p => new DeleteWrite { Path = p, Cascade = cascade })], ct);
                total += paths.Count;
                if (paths.Count < batchSize)
                    break;   // last (short) batch — collection drained
            }
        }
        return total;
    }

    private async Task<List<string>> SelectExpiredPathsAsync(TtlPolicy policy, int batchSize, CancellationToken ct)
    {
        var compiled = TtlSql.SelectExpired(policy, batchSize);
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        compiled.Apply(cmd);
        var paths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            paths.Add(reader.GetString(0));
        return paths;
    }

    private static async Task<bool> Wait(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
