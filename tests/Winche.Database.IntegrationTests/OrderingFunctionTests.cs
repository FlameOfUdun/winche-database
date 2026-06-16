using Winche.Database.Constants;
using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class OrderingFunctionTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task Seed(string id, Value value)
    {
        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO {WincheTables.Documents} (document_path, document_id, collection_path, collection_id, data) VALUES ($1, $2, 'c', 'c', $3::jsonb)";
        cmd.Parameters.AddWithValue($"c/{id}");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(StorageCodec.Encode(new Dictionary<string, Value> { ["f"] = value }));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> IdsOrderedByField()
    {
        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT document_id FROM {WincheTables.Documents}
            ORDER BY winche_rank(data->'f'),
                     winche_num(data->'f')  NULLS FIRST,
                     winche_num2(data->'f') NULLS FIRST,
                     winche_text(data->'f') COLLATE "C" NULLS FIRST,
                     winche_bytes(data->'f') NULLS FIRST
            """;
        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        return ids;
    }

    [Fact]
    public async Task CrossTypeOrdering_MatchesFirestoreTotalOrder()
    {
        // seeded deliberately out of order; expected order is the Firestore total order
        await Seed("h_geo", new GeoPointValue(0, 0));
        await Seed("a_null", new NullValue());
        await Seed("e_string", new StringValue("hello"));
        await Seed("c_nan", new DoubleValue(double.NaN));
        await Seed("b_bool", new BooleanValue(false));
        await Seed("d_number", new IntegerValue(42));
        await Seed("d2_timestamp", new TimestampValue(DateTimeOffset.UnixEpoch));
        await Seed("f_bytes", new BytesValue([1]));
        await Seed("g_ref", new ReferenceValue("users/u1"));

        var ids = await IdsOrderedByField();

        Assert.Equal(
            ["a_null", "b_bool", "c_nan", "d_number", "d2_timestamp", "e_string", "f_bytes", "g_ref", "h_geo"],
            ids);
    }

    [Fact]
    public async Task Numbers_IntAndDoubleInterleaveNumerically()
    {
        await Seed("n3", new DoubleValue(2.5));
        await Seed("n1", new IntegerValue(1));
        await Seed("n4", new IntegerValue(3));
        await Seed("n2", new DoubleValue(1.5));
        await Seed("n0", new DoubleValue(double.NegativeInfinity));
        await Seed("n5", new DoubleValue(double.PositiveInfinity));

        Assert.Equal(["n0", "n1", "n2", "n3", "n4", "n5"], await IdsOrderedByField());
    }

    [Fact]
    public async Task Booleans_FalseBeforeTrue()
    {
        await Seed("t", new BooleanValue(true));
        await Seed("f", new BooleanValue(false));
        Assert.Equal(["f", "t"], await IdsOrderedByField());
    }

    [Fact]
    public async Task Timestamps_OrderChronologically()
    {
        await Seed("t2", new TimestampValue(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        await Seed("t1", new TimestampValue(new DateTimeOffset(1969, 1, 1, 0, 0, 0, TimeSpan.Zero))); // pre-epoch
        await Seed("t3", new TimestampValue(new DateTimeOffset(2026, 1, 1, 0, 0, 0, 1, TimeSpan.Zero)));
        Assert.Equal(["t1", "t2", "t3"], await IdsOrderedByField());
    }

    [Fact]
    public async Task GeoPoints_OrderByLatitudeThenLongitude()
    {
        await Seed("g3", new GeoPointValue(10, 5));
        await Seed("g1", new GeoPointValue(-10, 99));
        await Seed("g2", new GeoPointValue(10, 1));
        Assert.Equal(["g1", "g2", "g3"], await IdsOrderedByField());
    }

    [Fact]
    public async Task Bytes_OrderLexicographically()
    {
        await Seed("b2", new BytesValue([1, 2]));
        await Seed("b1", new BytesValue([1]));
        await Seed("b3", new BytesValue([2]));
        Assert.Equal(["b1", "b2", "b3"], await IdsOrderedByField());
    }

    [Fact]
    public async Task Strings_OrderByUtf8ByteOrder_NotLocale()
    {
        // UTF-8 byte order: "B" (0x42) < "a" (0x61) < "á" (0xC3 0xA1)
        await Seed("s2", new StringValue("a"));
        await Seed("s1", new StringValue("B"));
        await Seed("s3", new StringValue("á"));
        Assert.Equal(["s1", "s2", "s3"], await IdsOrderedByField());
    }
}
