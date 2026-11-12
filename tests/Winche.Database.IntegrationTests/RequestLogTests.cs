using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Logging;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
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
        Assert.Equal("u2 was denied writing users/u1: no rule allows it.", log[3].Message);
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
