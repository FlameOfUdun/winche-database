using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Database.AspNetCore.Abstraction;
using Winche.Database.AspNetCore.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets;
using Winche.Database.AspNetCore.WebSockets.Connections;
using Winche.Database.AspNetCore.WebSockets.Protocol;
using Winche.Database.AspNetCore.WebSockets.Routing;
using Winche.Database.IntegrationTests.Rest;
using Winche.Database.Runtime;
using Winche.Database.Wire;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Unexpected errors are logged with an error id the caller also gets, and document paths in REST URLs may be
/// base64url (11.0.1).
/// </summary>
[Collection("postgres")]
public class InternalErrorTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private sealed class CapturingLogger : ILogger, ILoggerProvider
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
    }

    private static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Map_logs_only_unexpected_errors_with_an_id()
    {
        var log = new CapturingLogger();
        var denied = ErrorMapper.Map(new Authorization.AccessDeniedException("a/b", "get"), log, "GET /x", "u1");
        Assert.Equal("permission-denied", denied.Code);
        Assert.Empty(log.Entries);

        var boom = new InvalidOperationException("secret detail");
        var error = ErrorMapper.Map(boom, log, "GET /x", "u1");
        Assert.Equal("internal", error.Code);
        var id = error.Details!["errorId"]!.GetValue<string>();
        Assert.Equal(12, id.Length);
        Assert.Equal($"Internal error ({id}).", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(boom, entry.Exception);
        Assert.Equal($"Internal error {id} in GET /x for u1", entry.Message);
        Assert.Equal("u1", ErrorMapper.UserOf(new Dictionary<string, object?> { ["sub"] = "u1" }));
    }

    [Fact]
    public async Task Rest_accepts_base64url_paths_and_reports_bad_ones_as_400()
    {
        await using var host = await RestTestHost.StartAsync(Fx.ConnectionString,
            c => c.UseRules(RuleSetBuilder.Build(r => r.Match("{document=**}", b => b.Allow(RuleOperations.All, Expr.Const(true))))));
        var client = host.Client;

        // Ids whose standard base64 has a '/' and a '+' — either used to break the URL.
        foreach (var unsafeChar in new[] { '/', '+' })
        {
            var id = Enumerable.Range(0xA0, 0x3000).Select(c => $"x{(char)c}y").First(i =>
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"rest-b64/{i}")).Contains(unsafeChar));
            var path = $"rest-b64/{id}";
            var put = await client.PutAsync($"/documents/v1/{B64Url(path)}",
                new StringContent("""{"fields":{"n":{"integerValue":"1"}}}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var got = JsonNode.Parse(await client.GetStringAsync($"/documents/v1/{B64Url(path)}"))!;
            Assert.Equal("1", got["fields"]!["n"]!["integerValue"]!.GetValue<string>());
        }

        // Standard base64 (padded) still works where it fits in a URL.
        var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes("rest-b64/plain"));
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync($"/documents/v1/{plain}",
            new StringContent("""{"fields":{}}""", Encoding.UTF8, "application/json"))).StatusCode);

        var bad = await client.GetAsync("/documents/v1/not*base64!");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = JsonNode.Parse(await bad.Content.ReadAsStringAsync())!;
        Assert.Equal("invalid-argument", body["error"]!["code"]!.GetValue<string>());
        Assert.Contains("isn't base64url", body["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rest_logs_an_unexpected_error_and_returns_its_id()
    {
        var log = new CapturingLogger();
        await using var host = await RestTestHost.StartAsync(Fx.ConnectionString, c =>
        {
            c.Services.AddSingleton<ILoggerProvider>(log);
            c.MapClaims(_ => throw new InvalidOperationException("claims mapper bug"));
        });

        var resp = await host.Client.GetAsync($"/documents/v1/{B64Url("a/b")}");
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        var id = body["error"]!["details"]!["errorId"]!.GetValue<string>();
        Assert.Equal($"Internal error ({id}).", body["error"]!["message"]!.GetValue<string>());

        (LogLevel Level, string Message, Exception? Exception) entry;
        lock (log.Entries) entry = Assert.Single(log.Entries, e => e.Message.StartsWith("Internal error", StringComparison.Ordinal));
        Assert.Equal($"Internal error {id} in GET /documents/v1/{B64Url("a/b")} for (signed out)", entry.Message);
        Assert.Equal("claims mapper bug", entry.Exception!.Message);
    }

    // ── WebSocket ────────────────────────────────────────────────────────────

    /// <summary>An IDocumentDatabase whose every call fails unexpectedly.</summary>
    public class BrokenDatabase : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("database bug");
    }

    private sealed class Claims : DocumentClaimsAccessor
    {
        public override IReadOnlyDictionary<string, object?>? MapClaims(HttpContext httpContext) => null;
    }

    [Fact]
    public async Task WebSocket_logs_an_unexpected_error_and_returns_its_id()
    {
        var log = new CapturingLogger();
        var router = new MessageRouter(new Logger<MessageRouter>(new LoggerFactory([log])));
        await using var scope = new ConnectionScope("c1", DispatchProxy.Create<IDocumentDatabase, BrokenDatabase>(), new Claims());
        scope.SetClaims(new Dictionary<string, object?> { ["uid"] = "u7" });
        using var socket = WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions { IsServer = true });
        await using var conn = new WsConnection(socket, new WsOptions());

        var reply = await router.HandleAsync(scope, conn, new DefaultHttpContext(),
            new DocGetMessage { Id = "m1", Path = "a/b" }, default);

        var error = Assert.IsType<ErrorMessage>(reply);
        Assert.Equal("internal", error.Error.Code);
        var id = error.Error.Details!["errorId"]!.GetValue<string>();
        var entry = Assert.Single(log.Entries);
        Assert.Equal($"Internal error {id} in WebSocket DocGet for u7", entry.Message);
        Assert.Equal("database bug", entry.Exception!.Message);
    }
}
