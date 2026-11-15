using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Logging;
using Winche.Database.Runtime.Writes;
using Winche.Values;
using Winche.Rules;
using Winche.Rules.Text;

namespace Winche.Database.IntegrationTests;

/// <summary>LogRequests (11.1.0): structured entries for requests through the rules.</summary>
[Collection("postgres")]
public class RequestLogTests(PostgresFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class Capture : ILoggerProvider, ILogger
    {
        public List<(string Category, LogLevel Level, string Message, Dictionary<string, object?> Values)> Entries { get; } = [];
        private string _category = "";
        public ILogger CreateLogger(string categoryName) => new Named(this, categoryName);
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> kv ? kv.ToDictionary(x => x.Key, x => x.Value) : [];
            lock (Entries) Entries.Add((_category, logLevel, formatter(state, exception), values));
        }
        private sealed class Named(Capture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries) { owner._category = category; owner.Log(logLevel, eventId, state, exception, formatter); }
            }
        }
    }

    private sealed class Claims : IRuleClaimsAccessor
    {
        public IReadOnlyDictionary<string, object?>? Value { get; set; }
        public IReadOnlyDictionary<string, object?>? GetClaims() => Value;
    }

    private ServiceProvider Provider(double rate, Capture capture, Claims claims)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        services.AddWincheDatabase(o =>
        {
            o.ConnectionString = fx.ConnectionString;
            o.UseRulesText("match users/{uid} { allow read, write: if request.auth.uid == uid; }");
            o.LogRequests(r => r.SuccessSampleRate = rate);
        });
        services.AddSingleton<IRuleClaimsAccessor>(claims);
        return services.BuildServiceProvider();
    }

    private static List<(string Category, LogLevel Level, string Message, Dictionary<string, object?> Values)> Requests(Capture c)
    {
        lock (c.Entries) return [.. c.Entries.Where(e => e.Category == RequestLog.Category)];
    }

    [Fact]
    public async Task Requests_are_logged_with_who_what_and_outcome()
    {
        var capture = new Capture();
        var claims = new Claims { Value = new Dictionary<string, object?> { ["uid"] = "u1" } };
        await using var sp = Provider(1.0, capture, claims);
        var db = sp.GetRequiredService<IDocumentDatabase>();

        await db.WriteAsync([new SetWrite { Path = "users/u1", Fields = new Dictionary<string, Value> { ["name"] = new StringValue("Ann") } }]);
        await db.GetAsync("users/u1");
        claims.Value = new Dictionary<string, object?> { ["uid"] = "u2" };
        await Assert.ThrowsAsync<AccessDeniedException>(() => db.GetAsync("users/u1"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => db.WriteAsync([new DeleteWrite { Path = "users/u1" }]));
        claims.Value = null;
        await Assert.ThrowsAsync<AccessDeniedException>(() => db.QueryAsync(new Query("users")));

        var log = Requests(capture);
        Assert.Equal(5, log.Count);
        Assert.Equal(LogLevel.Information, log[0].Level);
        Assert.StartsWith("u1 wrote users/u1 (", log[0].Message);
        Assert.Equal("database.write", log[0].Values["Operation"]);
        Assert.Equal("ok", log[0].Values["Outcome"]);
        Assert.StartsWith("u1 read users/u1 (", log[1].Message);

        Assert.Equal(LogLevel.Warning, log[2].Level);
        Assert.Equal("u2 was denied reading users/u1: no rule allows it.", log[2].Message);
        Assert.Equal(("u2", "users/u1", "denied"), ((string)log[2].Values["Principal"]!, (string)log[2].Values["Path"]!, (string)log[2].Values["Outcome"]!));
        Assert.Equal("u2 was denied deleting users/u1: no rule allows it.", log[3].Message);
        Assert.Equal("signed out was denied querying users: no rule allows it.", log[4].Message);
        Assert.IsType<long>(log[4].Values["LatencyMs"]);
    }

    [Fact]
    public async Task Successes_are_sampled_denials_never()
    {
        var capture = new Capture();
        var claims = new Claims { Value = new Dictionary<string, object?> { ["uid"] = "u1" } };
        await using var sp = Provider(0.0, capture, claims);
        var db = sp.GetRequiredService<IDocumentDatabase>();
        for (var i = 0; i < 5; i++) await db.GetAsync("users/u1");
        await Assert.ThrowsAsync<AccessDeniedException>(() => db.GetAsync("users/u2"));

        var log = Requests(capture);
        var denied = Assert.Single(log);
        Assert.Equal("denied", denied.Values["Outcome"]);
    }

    [Fact]
    public async Task Successful_writes_are_always_logged_reads_sampled()
    {
        var capture = new Capture();
        var claims = new Claims { Value = new Dictionary<string, object?> { ["uid"] = "u1" } };
        await using var sp = Provider(0.0, capture, claims);
        var db = sp.GetRequiredService<IDocumentDatabase>();
        await db.WriteAsync([new SetWrite { Path = "users/u1", Fields = new Dictionary<string, Value> { ["name"] = new StringValue("Ann") } }]);
        await db.GetAsync("users/u1");
        var write = Assert.Single(Requests(capture));
        Assert.Equal("database.write", write.Values["Operation"]);
        await db.WriteAsync([new DeleteWrite { Path = "users/u1" }]);   // a delete is a change too
        Assert.Equal("database.delete", Requests(capture)[^1].Values["Operation"]);
    }

    [Fact]
    public async Task A_rest_request_is_one_entry_its_follow_up_reads_are_not_logged()
    {
        var capture = new Capture();
        await using var host = await Rest.RestTestHost.StartAsync(fx.ConnectionString, o =>
        {
            o.UseRulesText("match notes/{id} { allow read, write: if true; }");
            o.LogRequests(r => r.SuccessSampleRate = 1.0);
        }, capture);
        var path = System.Buffers.Text.Base64Url.EncodeToString("notes/n1"u8);
        using var put = await host.Client.PutAsync($"/documents/v1/{path}",
            new StringContent("""{ "fields": { "a": { "integerValue": "1" } } }""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.OK, put.StatusCode);
        using var patch = await host.Client.PatchAsync($"/documents/v1/{path}",
            new StringContent("""{ "fields": { "b": { "integerValue": "2" } } }""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await host.Client.GetAsync($"/documents/v1/{path}")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"/documents/v1/{path}")).StatusCode);

        // Write, write, read, delete: one entry each (a write's answer and a delete's check are part of it).
        var log = Requests(capture);
        Assert.Equal(["database.write", "database.write", "database.get", "database.delete"], log.Select(e => (string)e.Values["Operation"]!).ToList());
        Assert.StartsWith("signed out deleted notes/n1 (", log[3].Message);
    }

    [Fact]
    public async Task Reads_inside_a_transaction_are_logged_and_their_denials_always()
    {
        var capture = new Capture();
        var claims = new Claims { Value = new Dictionary<string, object?> { ["uid"] = "u1" } };
        await using var sp = Provider(0.0, capture, claims);
        var db = sp.GetRequiredService<IDocumentDatabase>();
        var tx = await db.BeginTransactionAsync();
        await db.GetAsync(tx.Id, "users/u1");   // allowed: sampled out at 0
        await Assert.ThrowsAsync<AccessDeniedException>(() => db.GetAsync(tx.Id, "users/u2"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => db.QueryAsync(tx.Id, new Query("users")));
        await db.RollbackTransactionAsync(tx.Id);
        var log = Requests(capture);
        Assert.Equal(["u1 was denied reading users/u2: no rule allows it.", "u1 was denied querying users: no rule allows it."], log.Select(e => e.Message).ToList());
    }

    [Fact]
    public async Task Without_LogRequests_nothing_is_logged()
    {
        var capture = new Capture();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        services.AddWincheDatabase(o => { o.ConnectionString = fx.ConnectionString; o.UseRulesText("match users/{uid} { allow read; }"); });
        await using var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<IDocumentDatabase>().GetAsync("users/u1");
        Assert.Empty(Requests(capture));
    }
}
