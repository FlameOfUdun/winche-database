using Npgsql;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class ChangesFeedTests(PostgresFixture fx) : QueryTestBase(fx)
{
    [Fact]
    public async Task ChangesTable_ExistsAndNotifies()
    {
        // direct insert → row readable + pg_notify('winche_changes', seq) fires
        await using var listenConn = await Fx.DataSource.OpenConnectionAsync();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        listenConn.Notification += (_, e) => received.TrySetResult(e.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN winche_changes", listenConn))
            await listen.ExecuteNonQueryAsync();

        await using (var conn = await Fx.DataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO {Fx.Table}_changes (type, path, collection, version, commit_time) VALUES ('added', 'c/x', 'c', 1, now())";
            await cmd.ExecuteNonQueryAsync();
        }

        var waitTask = listenConn.WaitAsync(new CancellationTokenSource(5000).Token);
        await Task.WhenAny(received.Task, waitTask);
        if (!received.Task.IsCompleted) await waitTask;          // pump the connection once
        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(long.TryParse(payload, out var seq) && seq > 0);

        var rows = await Fx.ReadChangesAsync();
        var row = Assert.Single(rows);
        Assert.Equal(("added", "c/x", "c", 1L), (row.Type, row.Path, row.Collection, row.Version));
    }

    [Fact]
    public async Task ChangeRows_SeqOrdersAcrossBatches()
    {
        var applier = new Winche.Database.Runtime.Writes.WriteApplier(Fx.DataSource, Fx.Table);
        await applier.ApplyAsync([new Winche.Database.Runtime.Writes.SetWrite
            { Path = "c/first", Fields = new Dictionary<string, Winche.Database.Values.Value>() }]);
        await applier.ApplyAsync([new Winche.Database.Runtime.Writes.SetWrite
            { Path = "c/second", Fields = new Dictionary<string, Winche.Database.Values.Value>() }]);

        var rows = await Fx.ReadChangesAsync();
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Seq < rows[1].Seq);
        Assert.Equal(["c/first", "c/second"], rows.Select(r => r.Path));
    }
}
