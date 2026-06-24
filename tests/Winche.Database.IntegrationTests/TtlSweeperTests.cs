using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Hosting;
using Winche.Database.Runtime.Ttl;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class TtlSweeperTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Core() =>
        new(Fx.DataSource, Options.Create(new WincheDatabaseOptions { ConnectionString = Fx.ConnectionString }));

    private TtlSweeper Sweeper(DocumentDatabase core, int batchSize, params TtlPolicy[] policies) =>
        Sweeper(core, batchSize, cascade: true, policies);

    private TtlSweeper Sweeper(DocumentDatabase core, int batchSize, bool cascade, params TtlPolicy[] policies) =>
        new(core, Fx.DataSource, policies,
            Options.Create(new WincheDatabaseOptions
            {
                ConnectionString = Fx.ConnectionString,
                Ttl = new TtlConfig { BatchSize = batchSize, CascadeDelete = cascade },
            }),
            NullLogger<TtlSweeper>.Instance);

    private static Dictionary<string, Value> TtlAt(DateTimeOffset at) => new() { ["ttl"] = new TimestampValue(at) };

    private async Task Seed(string path, Dictionary<string, Value> fields)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await new DocumentOperations(conn, null).SetAsync(path, fields);
    }

    private async Task<Document?> Get(string path)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        return await new DocumentOperations(conn, null).GetAsync(path);
    }

    [Fact]
    public async Task ExpiredDeleted_OthersKept()
    {
        await Seed("c/expired", TtlAt(DateTimeOffset.UtcNow.AddMinutes(-5)));
        await Seed("c/future", TtlAt(DateTimeOffset.UtcNow.AddMinutes(5)));
        await Seed("c/nofield", new() { ["x"] = new IntegerValue(1) });
        await Seed("c/wrongtype", new() { ["ttl"] = new IntegerValue(1) });   // non-timestamp ttl

        var n = await Sweeper(Core(), 500, TtlPolicy.For("c", "ttl")).SweepOnceAsync(default);

        Assert.Equal(1, n);
        Assert.Null(await Get("c/expired"));
        Assert.NotNull(await Get("c/future"));
        Assert.NotNull(await Get("c/nofield"));
        Assert.NotNull(await Get("c/wrongtype"));
    }

    [Fact]
    public async Task TtlDelete_EmitsRemovedChangeRow()
    {
        await Seed("c/x", TtlAt(DateTimeOffset.UtcNow.AddMinutes(-1)));
        await Fx.ResetChangesAsync();   // clear any seed change rows so we observe only the sweep's

        await Sweeper(Core(), 500, TtlPolicy.For("c", "ttl")).SweepOnceAsync(default);

        var changes = await Fx.ReadChangesAsync();
        Assert.Contains(changes, ch => ch.Path == "c/x" && ch.Type == "removed");
    }

    [Fact]
    public async Task CascadeDefault_DeletesSubcollection()
    {
        await Seed("c/doc", TtlAt(DateTimeOffset.UtcNow.AddMinutes(-1)));
        await Seed("c/doc/sub/child", new() { ["x"] = new IntegerValue(1) });

        await Sweeper(Core(), 500, TtlPolicy.For("c", "ttl")).SweepOnceAsync(default);   // CascadeDelete defaults true

        Assert.Null(await Get("c/doc"));
        Assert.Null(await Get("c/doc/sub/child"));   // subtree removed by default cascade
    }

    [Fact]
    public async Task CascadeDisabled_LeavesSubcollection()
    {
        await Seed("c/doc", TtlAt(DateTimeOffset.UtcNow.AddMinutes(-1)));
        await Seed("c/doc/sub/child", new() { ["x"] = new IntegerValue(1) });

        await Sweeper(Core(), 500, cascade: false, TtlPolicy.For("c", "ttl")).SweepOnceAsync(default);

        Assert.Null(await Get("c/doc"));
        Assert.NotNull(await Get("c/doc/sub/child"));   // subcollection survives when cascade is off
    }

    [Fact]
    public async Task CollectionGroup_SweepsAllParents()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Seed("a/x/things/t1", TtlAt(past));
        await Seed("b/y/things/t2", TtlAt(past));

        var n = await Sweeper(Core(), 500, TtlPolicy.For("things", "ttl")).SweepOnceAsync(default);

        Assert.Equal(2, n);
        Assert.Null(await Get("a/x/things/t1"));
        Assert.Null(await Get("b/y/things/t2"));
    }

    [Fact]
    public async Task BatchLoop_DrainsMoreThanBatchSize()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (var i = 0; i < 5; i++)
            await Seed($"c/d{i}", TtlAt(past));

        var n = await Sweeper(Core(), 2, TtlPolicy.For("c", "ttl")).SweepOnceAsync(default);   // BatchSize=2 < 5

        Assert.Equal(5, n);
        for (var i = 0; i < 5; i++)
            Assert.Null(await Get($"c/d{i}"));
    }

    [Fact]
    public async Task NoExpired_ReturnsZero()
    {
        await Seed("c/future", TtlAt(DateTimeOffset.UtcNow.AddHours(1)));

        var n = await Sweeper(Core(), 500, TtlPolicy.For("c", "ttl")).SweepOnceAsync(default);

        Assert.Equal(0, n);
        Assert.NotNull(await Get("c/future"));
    }

    [Fact]
    public async Task ZeroPolicies_ReturnsZeroWithoutError()
    {
        await Seed("c/expired", TtlAt(DateTimeOffset.UtcNow.AddMinutes(-1)));

        var n = await Sweeper(Core(), 500).SweepOnceAsync(default);   // no policies registered

        Assert.Equal(0, n);
        Assert.NotNull(await Get("c/expired"));   // nothing swept without a policy
    }

    [Fact]
    public async Task BatchSizeAboveWriteLimit_StillDeletesAll()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var conn = await Fx.DataSource.OpenConnectionAsync())
        {
            var ops = new DocumentOperations(conn, null);
            for (var i = 0; i < 510; i++)   // > the 500 write-batch cap
                await ops.SetAsync($"c/d{i}", TtlAt(past));
        }

        // BatchSize 1000 > WriteValidator.MaxBatchSize (500); the sweeper must clamp the SELECT LIMIT so
        // each delete batch stays within the write cap — otherwise WriteAsync throws and nothing deletes.
        var n = await Sweeper(Core(), 1000, TtlPolicy.For("c", "ttl")).SweepOnceAsync(default);

        Assert.Equal(510, n);
        Assert.Null(await Get("c/d0"));
        Assert.Null(await Get("c/d509"));
    }
}
