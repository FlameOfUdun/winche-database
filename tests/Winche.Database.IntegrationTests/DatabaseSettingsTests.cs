using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Winche.Config;
using Winche.Database.Abstraction;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.History;
using Winche.Database.Runtime.Writes;
using Winche.Database.Schema;
using Winche.Database.Settings;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>o.UseSettings(): indexes, expiry, history and limits changed from the console, without a restart.</summary>
[Collection("postgres")]
public class DatabaseSettingsTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fx.ResetAsync();
        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS winche_settings; DROP TABLE IF EXISTS winche_secrets;";
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<ServiceProvider> StartAsync(Action<WincheDatabaseOptions>? configure = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddWincheConfig(o => { o.ConnectionString = fx.ConnectionString; o.PollInterval = TimeSpan.FromMilliseconds(200); });
        services.AddWincheDatabase(o =>
        {
            o.ConnectionString = fx.ConnectionString;
            o.UseRulesText("match {document=**} { allow read, write: if true; }");
            o.UseSettings();
            configure?.Invoke(o);
        });
        var sp = services.BuildServiceProvider();
        foreach (var h in sp.GetServices<IHostedService>()) await h.StartAsync(default);
        return sp;
    }

    private static async Task SaveAsync(IServiceProvider sp, DatabaseSettings settings, string reason) =>
        await sp.GetRequiredService<SettingsStore>().SaveAsync(DatabaseSettings.Area, JsonSerializer.SerializeToNode(settings, SettingsArea.Json)!, "admin@x", reason);

    private async Task<bool> IndexExistsAsync(IndexDefinition index)
    {
        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM pg_indexes WHERE indexname = @n AND tablename = @t";
        cmd.Parameters.AddWithValue("n", Querying.Sql.IndexSql.NameOf(index));
        cmd.Parameters.AddWithValue("t", WincheDatabaseTables.Documents);
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    private async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail("Timed out waiting for the indexes to follow the settings.");
    }

    private static Dictionary<string, Value> Fields(params (string K, Value V)[] f) => f.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public async Task Indexes_and_expiry_added_in_settings_are_built_and_dropped_without_a_restart()
    {
        await using var sp = await StartAsync(o => o.UseIndexes(ib => ib.Add("patients", [new("ward")])));
        var area = sp.GetRequiredService<SettingsArea<DatabaseSettings>>();
        Assert.Equal("patients", Assert.Single(area.Current.Indexes).Collection);   // the code's index to start with

        var added = new IndexSettings
        {
            Collection = "sessions",
            Fields = [new IndexFieldSettings { Path = "userId" }, new IndexFieldSettings { Path = "startedAt", Direction = "desc" }],
        };
        await SaveAsync(sp, area.Current with
        {
            Indexes = [.. area.Current.Indexes, added],
            Ttl = [new TtlSettings { Collection = "sessions", Field = "expiresAt" }],
        }, "Session lists are slow");

        var catalog = sp.GetRequiredService<IndexCatalog>();
        Assert.Equal(2, catalog.Indexes.Count);
        Assert.Equal("sessions", Assert.Single(catalog.TtlPolicies).CollectionId);
        await WaitForAsync(() => IndexExistsAsync(added.ToDefinition()));
        Assert.Equal("sessions", sp.GetRequiredService<Querying.CollectionIndexResolver>().ScopeFor("sessions"));

        await SaveAsync(sp, area.Current with { Indexes = [.. area.Current.Indexes.Where(i => i.Collection != "sessions")] }, "Not needed");
        await WaitForAsync(async () => !await IndexExistsAsync(added.ToDefinition()));
        Assert.Null(sp.GetRequiredService<Querying.CollectionIndexResolver>().ScopeFor("sessions"));
    }

    [Fact]
    public async Task Startup_loads_saved_indexes_before_syncing_so_they_arent_dropped()
    {
        var added = new IndexSettings { Collection = "visits", Fields = [new IndexFieldSettings { Path = "at" }] };
        await using (var first = await StartAsync())
        {
            await SaveAsync(first, first.GetRequiredService<SettingsArea<DatabaseSettings>>().Current with { Indexes = [added] }, "Visits");
            await WaitForAsync(() => IndexExistsAsync(added.ToDefinition()));
        }

        // A fresh server: the startup sync must see the saved index, not just the code's (none).
        var services = new ServiceCollection().AddLogging();
        services.AddWincheConfig(o => o.ConnectionString = fx.ConnectionString);
        services.AddWincheDatabase(o => { o.ConnectionString = fx.ConnectionString; o.UseRulesText("match {document=**} { allow read, write: if true; }"); o.UseSettings(); });
        await using var sp = services.BuildServiceProvider();
        var host = new FakeHost(sp);
        await host.InitializeWincheDatabaseAsync();
        Assert.True(await IndexExistsAsync(added.ToDefinition()));
        Assert.Equal("visits", Assert.Single(sp.GetRequiredService<IndexCatalog>().Indexes).CollectionId);
    }

    private sealed class FakeHost(IServiceProvider services) : IHost
    {
        public IServiceProvider Services => services;
        public void Dispose() { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task History_and_write_limits_change_while_running()
    {
        await using var sp = await StartAsync();
        var db = sp.GetRequiredService<IDocumentDatabase>();
        var history = sp.GetRequiredService<DocumentHistory>();
        var area = sp.GetRequiredService<SettingsArea<DatabaseSettings>>();

        await db.WriteAsync([new SetWrite { Path = "notes/a", Fields = Fields(("text", new StringValue("before"))) }]);
        Assert.All(await history.QueryAsync(new HistoryQuery { Path = "notes/a" }), e => Assert.Null(e.After));   // no copies kept yet

        await SaveAsync(sp, area.Current with
        {
            History = new HistorySettings { Enabled = true, RetentionDays = 14 },
            WriteLimits = new WriteLimitSettings { MaxDocumentKb = 1, MaxDepth = 20 },
        }, "Keep history; small documents");

        await db.WriteAsync([new SetWrite { Path = "notes/a", Fields = Fields(("text", new StringValue("after"))) }]);
        var latest = (await history.QueryAsync(new HistoryQuery { Path = "notes/a" }))[0];   // newest first
        Assert.Equal("before", latest.Before?.Fields["text"] is StringValue b ? b.Value : null);
        Assert.Equal("after", latest.After?.Fields["text"] is StringValue a ? a.Value : null);

        var big = await Assert.ThrowsAsync<RuntimeException>(() =>
            db.WriteAsync([new SetWrite { Path = "notes/b", Fields = Fields(("text", new StringValue(new string('x', 2000)))) }]));
        Assert.Equal(RuntimeStatus.InvalidArgument, big.Status);
        Assert.Equal(TimeSpan.FromDays(14), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WincheDatabaseOptions>>().Value.History!.Retention);
    }

    [Fact]
    public async Task Settings_that_cant_work_are_refused_with_reasons()
    {
        await using var sp = await StartAsync();
        var area = sp.GetRequiredService<SettingsArea<DatabaseSettings>>();
        var bad = area.Current with
        {
            Indexes = [new IndexSettings { Collection = "a/b", Fields = [new IndexFieldSettings { Path = "x" }] },
                       new IndexSettings { Collection = "tags", Kind = "array", Fields = [new IndexFieldSettings { Path = "a" }, new IndexFieldSettings { Path = "b" }] }],
            WriteLimits = new WriteLimitSettings { MaxDocumentKb = 0, MaxDepth = 20 },
        };
        var ex = await Assert.ThrowsAsync<SettingsValidationException>(() => SaveAsync(sp, bad, "x"));
        Assert.Contains(ex.Errors, e => e.StartsWith("Index on a/b"));
        Assert.Contains(ex.Errors, e => e.Contains("exactly one field"));
        Assert.Contains(ex.Errors, e => e.Contains("1 KB to 16 MB"));
    }
}
