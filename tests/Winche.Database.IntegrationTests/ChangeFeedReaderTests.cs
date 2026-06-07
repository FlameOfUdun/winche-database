using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class ChangeFeedReaderTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private ChangeFeedReader Reader() => new(Fx.DataSource);
    private WriteApplier Applier() => new(Fx.DataSource);
    private static Dictionary<string, Value> Map() => new();

    [Fact]
    public async Task ReadAfter_PagesInSeqOrder_TypedRecords()
    {
        var applier = Applier();
        await applier.ApplyAsync([new SetWrite { Path = "c/a", Fields = Map() }]);
        await applier.ApplyAsync([new SetWrite { Path = "c/a", Fields = Map() }]);   // modified
        await applier.ApplyAsync([new DeleteWrite { Path = "c/a" }]);                // removed

        var all = await Reader().ReadAfterAsync(0, limit: 10);
        Assert.Equal(3, all.Count);
        Assert.Equal([ChangeType.Added, ChangeType.Modified, ChangeType.Removed], all.Select(r => r.Type));
        Assert.True(all[0].Seq < all[1].Seq && all[1].Seq < all[2].Seq);
        Assert.All(all, r => Assert.Equal(("c/a", "c"), (r.Path, r.Collection)));

        var page2 = await Reader().ReadAfterAsync(all[0].Seq, limit: 1);
        Assert.Equal(ChangeType.Modified, Assert.Single(page2).Type);

        Assert.Equal(all[2].Seq, await Reader().GetMaxSeqAsync());
    }

    [Fact]
    public async Task GetMaxSeq_EmptyFeed_IsZero()
    {
        Assert.Equal(0, await Reader().GetMaxSeqAsync());
    }

    [Fact]
    public async Task PruneBefore_DeletesOldRows()
    {
        var applier = Applier();
        await applier.ApplyAsync([new SetWrite { Path = "c/a", Fields = Map() }]);
        Assert.Equal(1, await Reader().PruneBeforeAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Empty(await Reader().ReadAfterAsync(0, 10));
    }
}
