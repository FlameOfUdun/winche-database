using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class CollectionListingTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private DocumentDatabase Db() => new(fx.DataSource, Options.Create(new WincheDatabaseOptions()));

    private static Dictionary<string, Value> Empty => new();
    private Task Seed(string path) => Db().WriteAsync([new SetWrite { Path = path, Fields = Empty }]);

    [Fact]
    public async Task Root_ReturnsDistinctTopLevelCollections()
    {
        await Seed("users/u1");
        await Seed("users/u2");
        await Seed("orders/o1");

        var result = await Db().ListCollectionIdsAsync(parentDocumentPath: null);

        Assert.Equal(["orders", "users"], result.CollectionIds);   // distinct, byte-ordered
        Assert.Null(result.NextPageToken);
    }

    [Fact]
    public async Task UnderDocument_ReturnsDirectSubcollections()
    {
        await Seed("users/u1/orders/o1");
        await Seed("users/u1/posts/p1");
        await Seed("users/u2/orders/o9");   // different parent — must not leak in

        var result = await Db().ListCollectionIdsAsync("users/u1");

        Assert.Equal(["orders", "posts"], result.CollectionIds);
        Assert.Null(result.NextPageToken);
    }

    [Fact]
    public async Task MissingIntermediateParent_StillListsSubcollection()
    {
        // Only the deep leaf is written; a/b/c/d is "missing".
        await Seed("a/b/c/d/e/f");

        var result = await Db().ListCollectionIdsAsync("a/b");

        Assert.Equal(["c"], result.CollectionIds);
    }

    [Fact]
    public async Task NoSubcollections_ReturnsEmpty()
    {
        await Seed("users/u1");

        var result = await Db().ListCollectionIdsAsync("users/u1");

        Assert.Empty(result.CollectionIds);
        Assert.Null(result.NextPageToken);
    }

    [Fact]
    public async Task Pagination_WalksAllPagesWithoutGapsOrDuplicates()
    {
        // 5 distinct top-level collections: c1..c5
        foreach (var c in new[] { "c1", "c2", "c3", "c4", "c5" })
            await Seed($"{c}/d1");

        var seen = new List<string>();
        string? token = null;
        do
        {
            var page = await Db().ListCollectionIdsAsync(parentDocumentPath: null, pageSize: 2, pageToken: token);
            Assert.True(page.CollectionIds.Count <= 2);
            seen.AddRange(page.CollectionIds);
            token = page.NextPageToken;
        } while (token is not null);

        Assert.Equal(["c1", "c2", "c3", "c4", "c5"], seen);   // ordered, complete, no dupes
    }

    [Fact]
    public async Task InvalidParentPath_Throws()
    {
        // "users" is a collection path (even segment count), not a document path.
        await Assert.ThrowsAsync<ArgumentException>(() => Db().ListCollectionIdsAsync("users"));
    }

    [Fact]
    public async Task InvalidPageToken_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Db().ListCollectionIdsAsync(parentDocumentPath: null, pageToken: "not base64!!!"));
    }
}
