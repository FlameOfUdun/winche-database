using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class HousekeepingTests(PostgresFixture fx) : QueryTestBase(fx)
{
    [Fact]
    public async Task Pruner_Logic_DeletesOnlyOldRows()
    {
        await new WriteApplier(Fx.DataSource).ApplyAsync(
            [new SetWrite { Path = "c/fresh", Fields = new Dictionary<string, Value>() }]);

        var reader = new ChangeFeedReader(Fx.DataSource);
        Assert.Equal(0, await reader.PruneBeforeAsync(DateTimeOffset.UtcNow.AddDays(-7))); // nothing that old
        Assert.Single(await reader.ReadAfterAsync(0, 10));
        Assert.Equal(1, await reader.PruneBeforeAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public async Task Sweeper_Logic_RemovesExpiredLedgerEntries()
    {
        var db = new DocumentDatabase(Fx.DataSource, Options.Create(new WincheDatabaseOptions
        {
            TransactionConfig = new TransactionConfig { IdleTimeoutSpan = TimeSpan.FromMilliseconds(50) },
        }));
        await db.BeginTransactionAsync();
        await db.BeginTransactionAsync();
        Assert.Equal(2, db.Ledger.Count);
        await Task.Delay(150);
        Assert.Equal(2, db.Ledger.RemoveExpired());
        Assert.Equal(0, db.Ledger.Count);
    }
}
