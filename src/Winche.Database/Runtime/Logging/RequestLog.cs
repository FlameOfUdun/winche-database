using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Winche.Database.Authorization;
using Winche.Database.Wire;

namespace Winche.Database.Runtime.Logging;

/// <summary>What <see cref="DependencyInjection.WincheDatabaseOptions.LogRequests"/> records about each request.</summary>
public sealed class RequestLogOptions
{
    /// <summary>The share of successful requests logged, 0–1 (default 0.1). Denied and slow ones are always logged.</summary>
    public double SuccessSampleRate { get; set; } = 0.1;

    /// <summary>A request at least this slow is always logged, as a Warning (default 1 s).</summary>
    public TimeSpan SlowThreshold { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Logs app requests that go through the access rules (category <see cref="Category"/>) as structured entries —
/// <c>Operation</c>, <c>Path</c>, <c>Principal</c>, <c>Outcome</c>, <c>LatencyMs</c> — with a plain message:
/// "u2 was denied reading users/u1: no rule allows it". Denied (Warning) and slow (Warning) requests are always
/// logged; successful ones (Information) are sampled. Unexpected errors are logged where they're mapped, with an id.
/// </summary>
internal sealed class RequestLog(ILogger logger, RequestLogOptions options, IRuleClaimsAccessor claims)
{
    public const string Category = "Winche.Database.Requests";

    public async Task<T> RunAsync<T>(string operation, string path, Func<Task<T>> body)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await body();
            Done(operation, path, "ok", watch);
            return result;
        }
        catch (AccessDeniedException ex)
        {
            Done(operation, ex.Path, "denied", watch);
            throw;
        }
        catch (RuntimeException ex)
        {
            Done(operation, path, Outcome(ex.Status), watch);
            throw;
        }
    }

    public async Task RunAsync(string operation, string path, Func<Task> body) =>
        await RunAsync<bool>(operation, path, async () => { await body(); return true; });

    public T Run<T>(string operation, string path, Func<T> body)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = body();
            Done(operation, path, "ok", watch);
            return result;
        }
        catch (AccessDeniedException ex)
        {
            Done(operation, ex.Path, "denied", watch);
            throw;
        }
    }

    private static string Outcome(RuntimeStatus status) => status switch
    {
        RuntimeStatus.NotFound => "notFound",
        RuntimeStatus.AlreadyExists => "alreadyExists",
        RuntimeStatus.FailedPrecondition => "failedPrecondition",
        RuntimeStatus.Aborted => "aborted",
        RuntimeStatus.DeadlineExceeded => "timeout",
        _ => "invalid",
    };

    private void Done(string operation, string path, string outcome, Stopwatch watch)
    {
        var ms = watch.ElapsedMilliseconds;
        var slow = ms >= options.SlowThreshold.TotalMilliseconds;
        var level = outcome is "ok" or "notFound" && !slow ? LogLevel.Information : LogLevel.Warning;
        if (!logger.IsEnabled(level)) return;
        if (level == LogLevel.Information && Random.Shared.NextDouble() >= options.SuccessSampleRate) return;
        var principal = ErrorMapper.UserOf(claims.GetClaims()) ?? "signed out";
        logger.Log(level, new EventId(outcome == "denied" ? 2 : slow ? 3 : 1, "Request"),
            new RequestEntry(operation, path, principal, outcome, ms, slow), null, static (e, _) => e.ToString());
    }
}

/// <summary>A request entry: structured values for log stores that keep them, a sentence for those that don't.</summary>
public sealed class RequestEntry(string operation, string path, string principal, string outcome, long latencyMs, bool slow)
    : IReadOnlyList<KeyValuePair<string, object?>>
{
    public string Operation => operation;
    public string Path => path;
    public string Principal => principal;
    public string Outcome => outcome;
    public long LatencyMs => latencyMs;

    private static readonly Dictionary<string, (string Verb, string Gerund)> Words = new(StringComparer.Ordinal)
    {
        ["database.get"] = ("read", "reading"),
        ["database.getAll"] = ("read", "reading"),
        ["database.query"] = ("queried", "querying"),
        ["database.count"] = ("counted", "counting"),
        ["database.aggregate"] = ("aggregated", "aggregating"),
        ["database.write"] = ("wrote", "writing"),
        ["database.add"] = ("added a document to", "adding a document to"),
        ["database.listen"] = ("started a live query on", "listening to"),
        ["database.listenDocument"] = ("started watching", "watching"),
        ["database.commit"] = ("committed a transaction writing", "committing a transaction to"),
        ["storage.create"] = ("registered", "registering"),
        ["storage.get"] = ("read", "reading"),
        ["storage.update"] = ("updated", "updating"),
        ["storage.delete"] = ("deleted", "deleting"),
        ["storage.list"] = ("listed", "listing"),
        ["storage.upload"] = ("started uploading", "uploading"),
        ["storage.download"] = ("downloaded", "downloading"),
        ["storage.confirm"] = ("finished uploading", "finishing the upload of"),
    };

    public override string ToString()
    {
        var (verb, gerund) = Words.GetValueOrDefault(operation, (operation, operation));
        return outcome switch
        {
            "ok" when slow => $"{principal} {verb} {path}, which took {latencyMs:N0} ms (slow).",
            "ok" => $"{principal} {verb} {path} ({latencyMs:N0} ms).",
            "denied" => $"{principal} was denied {gerund} {path}: no rule allows it.",
            "notFound" => $"{principal} {verb} {path}: it doesn't exist.",
            "alreadyExists" => $"{principal} couldn't create {path}: it already exists.",
            "failedPrecondition" => $"{principal} {verb} {path}: a precondition failed.",
            "aborted" => $"{principal} {verb} {path}: aborted by a concurrent change.",
            _ => $"{principal} {verb} {path}: {outcome}.",
        };
    }

    private KeyValuePair<string, object?>[] Values =>
    [
        new("Operation", operation), new("Path", path), new("Principal", principal),
        new("Outcome", outcome), new("LatencyMs", latencyMs), new("{OriginalFormat}", "{Principal} {Operation} {Path}: {Outcome}"),
    ];

    public KeyValuePair<string, object?> this[int index] => Values[index];
    public int Count => 6;
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)Values).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
