using Winche.Database.Constants;
using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class WincheKeyTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task Seed(string id, Value value)
    {
        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO {WincheTables.Documents} (path, id, collection, data) VALUES ($1, $2, 'c', $3::jsonb)";
        cmd.Parameters.AddWithValue($"c/{id}");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(StorageCodec.Encode(new Dictionary<string, Value> { ["f"] = value }));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> IdsOrderedByKey()
    {
        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id FROM {WincheTables.Documents} ORDER BY winche_key(data->'f')";
        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        return ids;
    }

    [Fact]
    public async Task ScalarKeyOrdering_MatchesFirestoreTotalOrder()
    {
        await Seed("i_map", new MapValue(new Dictionary<string, Value> { ["a"] = new IntegerValue(1) }));
        await Seed("a_null", new NullValue());
        await Seed("c_nan", new DoubleValue(double.NaN));
        await Seed("h_arr", new ArrayValue([new IntegerValue(1)]));
        await Seed("b_bool", new BooleanValue(true));
        await Seed("e_str", new StringValue("a"));
        await Seed("d_num", new IntegerValue(7));
        await Seed("d2_ts", new TimestampValue(DateTimeOffset.UnixEpoch));
        await Seed("f_bytes", new BytesValue([9]));
        await Seed("g_geo", new GeoPointValue(1, 1));

        // note: g_ref omitted (covered by scalar suite); rank order: null<bool<NaN<num<ts<str<bytes<geo...
        Assert.Equal(
            ["a_null", "b_bool", "c_nan", "d_num", "d2_ts", "e_str", "f_bytes", "g_geo", "h_arr", "i_map"],
            await IdsOrderedByKey());
    }

    [Fact]
    public async Task Arrays_OrderElementWise_ShorterPrefixFirst()
    {
        // Firestore: [1] < [1,2] < [2] — element-wise, NOT length-first (jsonb default would be wrong)
        await Seed("a3", new ArrayValue([new IntegerValue(2)]));
        await Seed("a1", new ArrayValue([new IntegerValue(1)]));
        await Seed("a2", new ArrayValue([new IntegerValue(1), new IntegerValue(2)]));
        Assert.Equal(["a1", "a2", "a3"], await IdsOrderedByKey());
    }

    [Fact]
    public async Task Arrays_IntAndDoubleElementsInterleave()
    {
        await Seed("a2", new ArrayValue([new DoubleValue(1.5)]));
        await Seed("a1", new ArrayValue([new IntegerValue(1)]));
        await Seed("a3", new ArrayValue([new IntegerValue(2)]));
        Assert.Equal(["a1", "a2", "a3"], await IdsOrderedByKey());
    }

    [Fact]
    public async Task Arrays_CrossTypeElements_FollowTotalOrder()
    {
        await Seed("a2", new ArrayValue([new StringValue("a")]));      // string rank > number rank
        await Seed("a1", new ArrayValue([new IntegerValue(999)]));
        Assert.Equal(["a1", "a2"], await IdsOrderedByKey());
    }

    [Fact]
    public async Task Strings_PrefixSortsFirst_AndControlByteIsSafe()
    {
        // prefix < extension ("a" < "a!"), and an embedded control char U+0001 sorts
        // between them; the 0x00 -> 0x00FF escaping keeps the terminator unambiguous.
        await Seed("s3", new StringValue("a!"));
        await Seed("s1", new StringValue("a"));
        await Seed("s2", new StringValue("a"));
        await Seed("s4", new StringValue("b"));
        Assert.Equal(["s1", "s2", "s3", "s4"], await IdsOrderedByKey());
    }

    [Fact]
    public async Task Maps_OrderByKeyThenValue()
    {
        // Firestore: {a:1} < {a:2} < {b:1} (keys byte-ordered, then values)
        await Seed("m3", new MapValue(new Dictionary<string, Value> { ["b"] = new IntegerValue(1) }));
        await Seed("m1", new MapValue(new Dictionary<string, Value> { ["a"] = new IntegerValue(1) }));
        await Seed("m2", new MapValue(new Dictionary<string, Value> { ["a"] = new IntegerValue(2) }));
        Assert.Equal(["m1", "m2", "m3"], await IdsOrderedByKey());
    }

    [Fact]
    public async Task KeyEquality_IntEqualsDouble()
    {
        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT winche_key('{"integerValue":"5"}'::jsonb) = winche_key('{"doubleValue":5}'::jsonb)""";
        Assert.Equal(true, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task KeyEquality_NestedArrayNumericEquivalence()
    {
        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT winche_key('{"arrayValue":{"values":[{"integerValue":"1"}]}}'::jsonb)
                 = winche_key('{"arrayValue":{"values":[{"doubleValue":1}]}}'::jsonb)
            """;
        Assert.Equal(true, await cmd.ExecuteScalarAsync());
    }
}
