using Winche.Database.Authorization;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Rules;

namespace Winche.Database.Tests.Authorization;

public class PostgresDocumentSourceTests
{
    // ── Fake core database ────────────────────────────────────────────────────

    private sealed class FakeCore : IDocumentDatabase
    {
        private readonly Dictionary<string, Document?> _docs;
        public int GetCallCount { get; private set; }

        public FakeCore(Dictionary<string, Document?> docs) => _docs = docs;

        public Task<Document?> GetAsync(string path, CancellationToken ct = default)
        {
            GetCallCount++;
            _docs.TryGetValue(path, out var doc);
            return Task.FromResult(doc);
        }

        // Remaining members are not exercised by PostgresDocumentSource.
        public Task<IReadOnlyList<Document?>> GetAllAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<long> CountAsync(Query query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<Write> writes, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TransactionHandle> BeginTransactionAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Document?> GetAsync(string transactionId, string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<QueryResult> QueryAsync(string transactionId, Query query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WriteResult>> CommitTransactionAsync(string transactionId, IReadOnlyList<Write> writes, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<T> RunTransactionAsync<T>(Func<TransactionContext, Task<T>> body, TransactionOptions? options = null, CancellationToken ct = default) => throw new NotImplementedException();
        public IQueryListener Listen(Query query, ListenOptions? options = null) => throw new NotImplementedException();
    }

    private static Document MakeDoc(string path, string id, IReadOnlyDictionary<string, Value>? fields = null) => new()
    {
        Path = path,
        Id = id,
        Collection = path.Split('/')[0],
        Fields = fields ?? new Dictionary<string, Value>(),
        CreateTime = DateTimeOffset.UnixEpoch,
        UpdateTime = DateTimeOffset.UnixEpoch,
        Version = 1,
    };

    // ── ExistsAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExistsAsync_ReturnsTrue_WhenDocumentExists()
    {
        var core = new FakeCore(new Dictionary<string, Document?>
        {
            ["users/u1"] = MakeDoc("users/u1", "u1"),
        });
        var source = new PostgresDocumentSource(core);

        Assert.True(await source.ExistsAsync("users/u1"));
    }

    [Fact]
    public async Task ExistsAsync_ReturnsFalse_WhenDocumentMissing()
    {
        var core = new FakeCore(new Dictionary<string, Document?>());
        var source = new PostgresDocumentSource(core);

        Assert.False(await source.ExistsAsync("users/missing"));
    }

    // ── GetAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ReturnsResourceMap_WhenDocumentExists()
    {
        var fields = new Dictionary<string, Value> { ["name"] = new StringValue("Bob") };
        var core = new FakeCore(new Dictionary<string, Document?>
        {
            ["users/u2"] = MakeDoc("users/u2", "u2", fields),
        });
        var source = new PostgresDocumentSource(core);

        var result = await source.GetAsync("users/u2");
        Assert.Equal(RuleValueKind.Map, result.Kind);
        Assert.Equal("Bob", result.AsMap["data"].AsMap["name"].AsString);
        Assert.Equal("u2", result.AsMap["id"].AsString);
        Assert.Equal(RuleValueKind.Path, result.AsMap["__name__"].Kind);
    }

    [Fact]
    public async Task GetAsync_ReturnsRuleNull_WhenDocumentMissing()
    {
        var core = new FakeCore(new Dictionary<string, Document?>());
        var source = new PostgresDocumentSource(core);

        var result = await source.GetAsync("users/ghost");
        Assert.Equal(RuleValueKind.Null, result.Kind);
    }

    // ── Per-instance cache ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_CalledTwice_HitsDbOnlyOnce()
    {
        var core = new FakeCore(new Dictionary<string, Document?>
        {
            ["users/u3"] = MakeDoc("users/u3", "u3"),
        });
        var source = new PostgresDocumentSource(core);

        await source.GetAsync("users/u3");
        await source.GetAsync("users/u3");

        Assert.Equal(1, core.GetCallCount);
    }

    [Fact]
    public async Task ExistsAsync_CalledTwice_HitsDbOnlyOnce()
    {
        var core = new FakeCore(new Dictionary<string, Document?>
        {
            ["users/u4"] = MakeDoc("users/u4", "u4"),
        });
        var source = new PostgresDocumentSource(core);

        await source.ExistsAsync("users/u4");
        await source.ExistsAsync("users/u4");

        Assert.Equal(1, core.GetCallCount);
    }

    [Fact]
    public async Task GetAsyncAndExistsAsync_SamePath_HitsDbOnlyOnce()
    {
        var core = new FakeCore(new Dictionary<string, Document?>
        {
            ["users/u5"] = MakeDoc("users/u5", "u5"),
        });
        var source = new PostgresDocumentSource(core);

        await source.GetAsync("users/u5");
        await source.ExistsAsync("users/u5");

        Assert.Equal(1, core.GetCallCount);
    }

    [Fact]
    public async Task MissingDocument_IsCached_DbHitOnlyOnce()
    {
        var core = new FakeCore(new Dictionary<string, Document?>());
        var source = new PostgresDocumentSource(core);

        await source.GetAsync("users/none");
        await source.ExistsAsync("users/none");

        Assert.Equal(1, core.GetCallCount);
    }

    [Fact]
    public async Task DifferentPaths_EachHitDbOnce()
    {
        var core = new FakeCore(new Dictionary<string, Document?>
        {
            ["col/a"] = MakeDoc("col/a", "a"),
            ["col/b"] = MakeDoc("col/b", "b"),
        });
        var source = new PostgresDocumentSource(core);

        await source.GetAsync("col/a");
        await source.GetAsync("col/b");
        await source.GetAsync("col/a"); // cached
        await source.GetAsync("col/b"); // cached

        Assert.Equal(2, core.GetCallCount);
    }
}
