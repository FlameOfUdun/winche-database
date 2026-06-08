using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class CursorTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private async Task SeedNumbers()
    {
        // f values: d1=1, d2=2, d3=2, d4=3  (d2/d3 duplicate to exercise the tiebreaker)
        await Seed("d1", new IntegerValue(1));
        await Seed("d2", new IntegerValue(2));
        await Seed("d3", new IntegerValue(2));
        await Seed("d4", new IntegerValue(3));
    }

    private static Query Q(Cursor? start = null, Cursor? end = null, SortDirection dir = SortDirection.Asc) =>
        new("c", OrderBy: [new Ordering(FieldPath.Parse("f"), dir)], Start: start, End: end);

    [Fact]
    public async Task StartAt_Inclusive_StartAfter_Exclusive()
    {
        await SeedNumbers();
        Assert.Equal(["d2", "d3", "d4"], await Ids(Q(start: new Cursor([new IntegerValue(2)], Before: true))));
        Assert.Equal(["d4"], await Ids(Q(start: new Cursor([new IntegerValue(2)], Before: false))));
    }

    [Fact]
    public async Task EndBefore_Exclusive_EndAt_Inclusive()
    {
        await SeedNumbers();
        Assert.Equal(["d1"], await Ids(Q(end: new Cursor([new IntegerValue(2)], Before: true))));
        Assert.Equal(["d1", "d2", "d3"], await Ids(Q(end: new Cursor([new IntegerValue(2)], Before: false))));
    }

    [Fact]
    public async Task TwoValueCursor_DisambiguatesDuplicates_ViaName()
    {
        await SeedNumbers();
        // after (2, "c/d2") → d3 (same value, later name), then d4
        var ids = await Ids(Q(start: new Cursor([new IntegerValue(2), new StringValue("c/d2")], Before: false)));
        Assert.Equal(["d3", "d4"], ids);
    }

    [Fact]
    public async Task DescendingCursor_MirrorsDirection()
    {
        await SeedNumbers();
        var ids = await Ids(Q(start: new Cursor([new IntegerValue(2)], Before: false), dir: SortDirection.Desc));
        Assert.Equal(["d1"], ids);            // DESC order: 3,2,2,1 — after value 2 comes 1
    }

    [Fact]
    public async Task CrossTypeCursor_NumberBoundary_ExcludesLowerRanks()
    {
        await Seed("b", new BooleanValue(true));      // rank 20 — before all numbers
        await Seed("n", new IntegerValue(5));
        await Seed("s", new StringValue("a"));        // rank 50 — after all numbers

        var ids = await Ids(Q(start: new Cursor([new IntegerValue(0)], Before: true)));
        Assert.Equal(["n", "s"], ids);                 // bool is before the numeric boundary (rule 9)
    }

    [Fact]
    public async Task Pagination_WalksTheWholeSetWithoutGapsOrDuplicates()
    {
        for (var i = 0; i < 10; i++)
            await Seed($"p{i:D2}", new IntegerValue(i % 3));   // duplicates galore

        var seen = new List<string>();
        Cursor? cursor = null;
        while (true)
        {
            var q = new Query("c", OrderBy: [new Ordering(F("f"))], Limit: 3, Start: cursor);
            var page = await Run(q);
            seen.AddRange(page.Documents.Select(d => d.Id));
            if (!page.HasMore) break;
            var last = page.Documents[^1];
            cursor = new Cursor([last.Fields["f"], new StringValue(last.Path)], Before: false);
        }

        Assert.Equal(10, seen.Count);
        Assert.Equal(10, seen.Distinct().Count());
    }
}
