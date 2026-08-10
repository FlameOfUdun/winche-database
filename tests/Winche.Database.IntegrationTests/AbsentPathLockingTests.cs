using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Covers the gap FOR UPDATE cannot close on its own: it locks rows, and a path with no row
/// locks nothing, so two batches racing on the same absent path can both read "absent" with no
/// serialization between them. WriteApplier closes this with a transaction-scoped advisory lock
/// over every path in play, taken before any row lock (see WriteApplier's class doc).
/// </summary>
[Collection("postgres")]
public class AbsentPathLockingTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Db() => new(Fx.DataSource,
        Options.Create(new WincheDatabaseOptions { TransactionConfig = new TransactionConfig() }));

    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    /// <summary>
    /// T1 reads a shared absent path and then claims it (writes it, plus a doc of its own). T2
    /// reads the SAME absent path — as a pure read-validation, never writing it itself — and
    /// writes only its own, unrelated doc. If T2's absence-read is allowed to stand even though
    /// T1 concurrently claimed the path, T2 has committed on a stale assumption: that is the bug.
    ///
    /// A write path shared by both transactions is NOT used here: the guarded upsert (versionBefore
    /// check in the ON CONFLICT clause) already serializes two writers racing to create the SAME
    /// row regardless of the advisory lock — confirmed empirically (both with and without the fix,
    /// that scenario always aborts exactly one side). The advisory lock closes a different gap:
    /// a path that is read-validated by one side but only ever WRITTEN by the other. Row locks
    /// (FOR UPDATE) never block on an absent row, so without the advisory lock this read/write
    /// asymmetry lets both sides succeed — reproduced 8/8 in manual verification with the fix
    /// removed, using this exact test body.
    ///
    /// Ordering is forced deterministically via a side-channel SESSION-level advisory lock on the
    /// same key WriteApplier hashes internally: held before either commit starts, it queues T1
    /// then T2 behind it (both genuinely in-flight, uncommitted, when released) and releasing it
    /// lets whichever queued first (T1) through first — without this, who wins the natural race is
    /// itself nondeterministic (either outcome can be legitimate), which is exactly why a forced,
    /// repeatable ordering is needed to assert a single fixed outcome.
    /// </summary>
    [Fact]
    public async Task ConcurrentTransactions_ReadValidatedAbsentPath_LoserAborts()
    {
        var db = Db();

        var tx1 = await db.BeginTransactionAsync();
        var tx2 = await db.BeginTransactionAsync();
        Assert.Null(await db.GetAsync(tx1.Id, "shared/claim"));
        Assert.Null(await db.GetAsync(tx2.Id, "shared/claim"));

        await using var side = await Fx.DataSource.OpenConnectionAsync();
        await using (var lockCmd = side.CreateCommand())
        {
            lockCmd.CommandText = "SELECT pg_advisory_lock(hashtextextended($1, 0))";
            lockCmd.Parameters.AddWithValue("shared/claim");
            await lockCmd.ExecuteNonQueryAsync();
        }

        // T1 claims the path; T2 only validates its absence and writes elsewhere.
        var t1 = db.CommitTransactionAsync(tx1.Id,
        [
            new SetWrite { Path = "docs/one", Fields = Map(("owner", new StringValue("t1"))) },
            new SetWrite { Path = "shared/claim", Fields = Map(("owner", new StringValue("t1"))) },
        ]);
        await Task.Delay(20);      // small: preserves genuine overlap — T1's commit is still in flight
        var t2 = db.CommitTransactionAsync(tx2.Id,
        [
            new SetWrite { Path = "docs/two", Fields = Map(("owner", new StringValue("t2"))) },
        ]);
        // Generous, but harmless either way: with the advisory lock present, both T1 and T2 are
        // now blocked on the side lock (queued in start order) no matter how long we wait here.
        await Task.Delay(400);

        await using (var unlockCmd = side.CreateCommand())
        {
            unlockCmd.CommandText = "SELECT pg_advisory_unlock(hashtextextended($1, 0))";
            unlockCmd.Parameters.AddWithValue("shared/claim");
            await unlockCmd.ExecuteNonQueryAsync();
        }

        await t1;                                                          // T1 always wins the forced ordering
        var ex = await Assert.ThrowsAsync<TransactionAbortedException>(() => t2);
        Assert.Equal(RuntimeStatus.Aborted, ex.Status);

        Assert.NotNull(await db.GetAsync("docs/one"));
        Assert.NotNull(await db.GetAsync("shared/claim"));
        Assert.Null(await db.GetAsync("docs/two"));                        // T2's whole batch rolled back
    }

    /// <summary>
    /// Many concurrent write batches over overlapping and disjoint path sets must all complete —
    /// no deadlock, no hang — since every writer takes both the advisory locks and the row locks
    /// in the same global sorted order.
    /// </summary>
    [Fact]
    public async Task ManyConcurrentBatches_OverlappingAndDisjointPaths_NoDeadlock()
    {
        var db = Db();
        var paths = Enumerable.Range(0, 8).Select(i => $"c/p{i}").ToArray();
        var rng = new Random(42);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var batches = Enumerable.Range(0, 60).Select(async i =>
        {
            var count = rng.Next(1, 4);
            var chosen = paths.OrderBy(_ => rng.Next()).Take(count).ToList();
            var writes = chosen.Select(p => (Write)new SetWrite
            {
                Path = p,
                Fields = Map(("writer", new IntegerValue(i))),
            }).ToList();
            try { await db.WriteAsync(writes); }
            catch (RuntimeException ex) when (ex.Status == RuntimeStatus.Aborted) { }   // contention is fine
        });
        await Task.WhenAll(batches);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Took {sw.Elapsed} — possible contention/hang.");
    }
}
