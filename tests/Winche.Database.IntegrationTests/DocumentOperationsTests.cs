using Npgsql;
using Winche.Database.Documents;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class DocumentOperationsTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<T> WithOps<T>(Func<DocumentOperations, Task<T>> action)
    {
        await using var conn = await fx.DataSource.OpenConnectionAsync();
        return await action(new DocumentOperations(conn, null));
    }

    private static Dictionary<string, Value> AllTypesFields() => new()
    {
        ["n"] = new NullValue(),
        ["b"] = new BooleanValue(true),
        ["i"] = new IntegerValue(long.MaxValue),
        ["d"] = new DoubleValue(1.5),
        ["nan"] = new DoubleValue(double.NaN),
        ["ts"] = new TimestampValue(new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero)),
        ["s"] = new StringValue("héllo 🌍"),
        ["by"] = new BytesValue([1, 2, 3]),
        ["ref"] = new ReferenceValue("users/u1"),
        ["geo"] = new GeoPointValue(59.9, 10.7),
        ["arr"] = new ArrayValue([new IntegerValue(1), new MapValue(new Dictionary<string, Value> { ["x"] = new NullValue() })]),
        ["map"] = new MapValue(new Dictionary<string, Value> { ["nested"] = new TimestampValue(DateTimeOffset.UnixEpoch) }),
    };

    private static Dictionary<string, Value> Empty => new();

    [Fact]
    public async Task Set_Then_Get_RoundTripsEveryValueType()
    {
        var fields = AllTypesFields();
        await WithOps(ops => ops.SetAsync("users/u1", fields, CancellationToken.None));
        var doc = await WithOps(ops => ops.GetAsync("users/u1", CancellationToken.None));

        Assert.NotNull(doc);
        Assert.Equal("users/u1", doc.Path);
        Assert.Equal("u1", doc.Id);
        Assert.Equal("users", doc.Collection);
        Assert.Equal(1, doc.Version);
        Assert.Equal(fields.Count, doc.Fields.Count);
        foreach (var (key, value) in fields)
            Assert.Equal(value, doc.Fields[key]);
    }

    [Fact]
    public async Task Get_Missing_ReturnsNull()
    {
        Assert.Null(await WithOps(ops => ops.GetAsync("users/none", CancellationToken.None)));
    }

    [Fact]
    public async Task Set_Twice_IncrementsVersion_AndReplacesFields()
    {
        await WithOps(ops => ops.SetAsync("users/u1",
            new Dictionary<string, Value> { ["a"] = new IntegerValue(1), ["b"] = new IntegerValue(2) }, CancellationToken.None));
        var doc = await WithOps(ops => ops.SetAsync("users/u1",
            new Dictionary<string, Value> { ["a"] = new IntegerValue(9) }, CancellationToken.None));

        Assert.Equal(2, doc.Version);
        Assert.Single(doc.Fields);                       // Set REPLACES (no merge)
        Assert.Equal(new IntegerValue(9), doc.Fields["a"]);
    }

    [Fact]
    public async Task Update_MergesRecursively()
    {
        await WithOps(ops => ops.SetAsync("users/u1", new Dictionary<string, Value>
        {
            ["address"] = new MapValue(new Dictionary<string, Value>
            {
                ["city"] = new StringValue("Oslo"),
                ["zip"] = new StringValue("0001"),
            }),
            ["age"] = new IntegerValue(30),
        }, CancellationToken.None));

        var doc = await WithOps(ops => ops.UpdateAsync("users/u1", new Dictionary<string, Value>
        {
            ["address"] = new MapValue(new Dictionary<string, Value> { ["city"] = new StringValue("Bergen") }),
        }, CancellationToken.None));

        Assert.NotNull(doc);
        Assert.Equal(2, doc.Version);
        Assert.Equal(new IntegerValue(30), doc.Fields["age"]);
        var address = Assert.IsType<MapValue>(doc.Fields["address"]);
        Assert.Equal(new StringValue("Bergen"), address.Fields["city"]);
        Assert.Equal(new StringValue("0001"), address.Fields["zip"]);
    }

    [Fact]
    public async Task Update_Missing_ReturnsNull()
    {
        Assert.Null(await WithOps(ops => ops.UpdateAsync("users/none",
            new Dictionary<string, Value> { ["a"] = new NullValue() }, CancellationToken.None)));
    }

    [Fact]
    public async Task Delete_CascadesToSubtree()
    {
        await WithOps(ops => ops.SetAsync("users/u1", Empty, CancellationToken.None));
        await WithOps(ops => ops.SetAsync("users/u1/orders/o1", Empty, CancellationToken.None));
        await WithOps(ops => ops.SetAsync("users/u2", Empty, CancellationToken.None));

        var deleted = await WithOps(ops => ops.DeleteAsync("users/u1", CancellationToken.None));

        Assert.Equal(["users/u1", "users/u1/orders/o1"], deleted.OrderBy(p => p));
        Assert.Null(await WithOps(ops => ops.GetAsync("users/u1", CancellationToken.None)));
        Assert.NotNull(await WithOps(ops => ops.GetAsync("users/u2", CancellationToken.None)));
    }

    [Fact]
    public async Task Delete_DoesNotTreatPathAsLikePattern()
    {
        await WithOps(ops => ops.SetAsync("users/u_1", Empty, CancellationToken.None));
        await WithOps(ops => ops.SetAsync("users/ux1", Empty, CancellationToken.None));   // would match 'u_1' as LIKE

        var deleted = await WithOps(ops => ops.DeleteAsync("users/u_1", CancellationToken.None));

        Assert.Equal(["users/u_1"], deleted);
        Assert.NotNull(await WithOps(ops => ops.GetAsync("users/ux1", CancellationToken.None)));
    }

    [Fact]
    public async Task InvalidPaths_Throw()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => WithOps(ops => ops.GetAsync("users", CancellationToken.None)));
        await Assert.ThrowsAsync<ArgumentException>(() => WithOps(ops => ops.SetAsync("users", Empty, CancellationToken.None)));
    }

    [Fact]
    public async Task Set_TrailingSlashPath_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => WithOps(ops => ops.SetAsync("users/", Empty, CancellationToken.None)));
    }

    [Fact]
    public async Task Write_PopulatesCollectionIdAndDocumentId()
    {
        var applier = new WriteApplier(fx.DataSource);
        await applier.ApplyAsync([new SetWrite {
            Path = "userData/alice/sessionHistory/s1",
            Fields = new Dictionary<string, Value> { ["v"] = new IntegerValue(1) } }]);

        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT collection_id, document_id FROM winche_documents WHERE document_path = $1";
        cmd.Parameters.AddWithValue("userData/alice/sessionHistory/s1");
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal("sessionHistory", r.GetString(0));
        Assert.Equal("s1", r.GetString(1));
    }
}
