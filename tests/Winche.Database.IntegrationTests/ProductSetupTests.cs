using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Winche.Core;
using Winche.Database.DependencyInjection;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class ProductSetupTests(PostgresFixture fx)
{
    private async Task<string> FreshDatabaseAsync()
    {
        var name = "ondemand_" + Guid.NewGuid().ToString("n")[..10];
        await using (var cmd = fx.DataSource.CreateCommand($"CREATE DATABASE {name}")) await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fx.ConnectionString) { Database = name }.ConnectionString;
    }

    private static async Task<bool> HasTableAsync(string conn, string table)
    {
        await using var db = NpgsqlDataSource.Create(conn);
        await using var cmd = db.CreateCommand($"SELECT to_regclass('public.{table}') IS NOT NULL");
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Set_up_on_demand_the_database_makes_nothing_until_its_setup_runs_step_by_step()
    {
        var conn = await FreshDatabaseAsync();
        var services = new ServiceCollection().AddLogging().AddWincheProductSetupOnDemand();
        services.AddWincheDatabase(o => o.ConnectionString = conn);
        await using var sp = services.BuildServiceProvider();
        var hosted = sp.GetServices<IHostedService>().ToList();
        foreach (var h in hosted) await h.StartAsync(default);
        try
        {
            await Task.Delay(300);
            Assert.False(await HasTableAsync(conn, "winche_documents"));   // its background work is waiting, not making tables

            var setup = sp.GetServices<IWincheProductSetup>().Single(s => s.Product == WincheProductNames.Database);
            Assert.Equal(3, setup.Steps.Count);
            await setup.RunAllAsync();
            await setup.RunAllAsync();   // again: nothing to do, no harm
            Assert.True(await HasTableAsync(conn, "winche_documents"));
            Assert.True(await HasTableAsync(conn, "winche_feed_cursors"));

            sp.GetRequiredService<WincheProductReadiness>().MarkReady(WincheProductNames.Database);
            var db = sp.GetRequiredService<Winche.Database.Runtime.DocumentDatabase>();
            await db.WriteAsync([new Winche.Database.Runtime.Writes.SetWrite { Path = "notes/a", Fields = new Dictionary<string, Winche.Values.Value> { ["x"] = new Winche.Values.IntegerValue(1) } }]);
            Assert.NotNull(await db.GetAsync("notes/a"));
        }
        finally
        {
            foreach (var h in hosted) await h.StopAsync(default);
        }
    }
}
