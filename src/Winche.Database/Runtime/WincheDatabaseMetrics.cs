using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Winche.Database.Runtime;

/// <summary>
/// The library's metrics, as <see cref="System.Diagnostics.Metrics"/> instruments on the
/// <see cref="MeterName"/> meter — collect them with OpenTelemetry (<c>AddMeter("Winche.Database")</c>),
/// Prometheus or <c>dotnet-counters</c>. With nothing listening, recording costs next to nothing.
/// <list type="table">
///   <item><term><c>winche.database.query.duration</c> (s)</term><description>query / count / aggregate; tags <c>operation</c>, <c>collection_id</c>, <c>collection_group</c>, <c>indexed</c> (a declared index covers the collection)</description></item>
///   <item><term><c>winche.database.write.duration</c> (s)</term><description>a write batch or transaction commit; tag <c>outcome</c> (ok, aborted, denied, invalid, error)</description></item>
///   <item><term><c>winche.database.write.documents</c></term><description>writes in committed batches</description></item>
///   <item><term><c>winche.database.listeners</c></term><description>live listeners held (up/down); tag <c>kind</c> (query, document)</description></item>
///   <item><term><c>winche.database.listener.update.duration</c> (s)</term><description>bringing one listener group up to date; tags <c>kind</c>, <c>method</c> (in_memory, requery)</description></item>
///   <item><term><c>winche.database.hook.changes</c></term><description>changes delivered to a durable consumer; tag <c>consumer</c></description></item>
///   <item><term><c>winche.database.hook.lag</c> (s)</term><description>commit → delivery of each batch's newest change; tag <c>consumer</c></description></item>
///   <item><term><c>winche.database.hook.failures</c> / <c>winche.database.hook.dead_letters</c></term><description>failed deliveries and changes given up on; tag <c>consumer</c></description></item>
///   <item><term><c>winche.database.ws.connections</c> / <c>winche.database.ws.subscriptions</c></term><description>open WebSocket connections and subscriptions (up/down)</description></item>
///   <item><term><c>winche.database.ws.requests</c></term><description>WebSocket requests; tag <c>type</c></description></item>
///   <item><term><c>winche.database.ws.throttled</c> / <c>winche.database.ws.limited</c></term><description>requests delayed by the rate limit; refusals and evictions by the other limits (tag <c>limit</c>)</description></item>
/// </list>
/// </summary>
public static class WincheDatabaseMetrics
{
    /// <summary>The meter's name: <c>Winche.Database</c>.</summary>
    public const string MeterName = "Winche.Database";

    internal static readonly Meter Meter = new(MeterName, typeof(WincheDatabaseMetrics).Assembly.GetName().Version?.ToString());

    internal static readonly Histogram<double> QueryDuration =
        Meter.CreateHistogram<double>("winche.database.query.duration", "s", "Query, count and aggregate duration");
    internal static readonly Histogram<double> WriteDuration =
        Meter.CreateHistogram<double>("winche.database.write.duration", "s", "Write batch / commit duration");
    internal static readonly Counter<long> WriteDocuments =
        Meter.CreateCounter<long>("winche.database.write.documents", null, "Writes in committed batches");
    internal static readonly UpDownCounter<long> Listeners =
        Meter.CreateUpDownCounter<long>("winche.database.listeners", null, "Live listeners held");
    internal static readonly Histogram<double> ListenerUpdateDuration =
        Meter.CreateHistogram<double>("winche.database.listener.update.duration", "s", "Bringing a listener group up to date");
    internal static readonly Counter<long> HookChanges =
        Meter.CreateCounter<long>("winche.database.hook.changes", null, "Changes delivered to durable consumers");
    internal static readonly Histogram<double> HookLag =
        Meter.CreateHistogram<double>("winche.database.hook.lag", "s", "Commit to delivery of a batch's newest change");
    internal static readonly Counter<long> HookFailures =
        Meter.CreateCounter<long>("winche.database.hook.failures", null, "Failed deliveries to durable consumers");
    internal static readonly Counter<long> HookDeadLetters =
        Meter.CreateCounter<long>("winche.database.hook.dead_letters", null, "Changes durable consumers gave up on");
    internal static readonly UpDownCounter<long> WsConnections =
        Meter.CreateUpDownCounter<long>("winche.database.ws.connections", null, "Open WebSocket connections");
    internal static readonly UpDownCounter<long> WsSubscriptions =
        Meter.CreateUpDownCounter<long>("winche.database.ws.subscriptions", null, "Open WebSocket subscriptions");
    internal static readonly Counter<long> WsRequests =
        Meter.CreateCounter<long>("winche.database.ws.requests", null, "WebSocket requests");
    internal static readonly Counter<long> WsThrottled =
        Meter.CreateCounter<long>("winche.database.ws.throttled", null, "WebSocket requests delayed by the rate limit");
    internal static readonly Counter<long> WsLimited =
        Meter.CreateCounter<long>("winche.database.ws.limited", null, "WebSocket subscriptions refused / connections closed by a limit");

    internal static TagList Tags(string k1, object? v1) => new() { { k1, v1 } };
    internal static TagList Tags(string k1, object? v1, string k2, object? v2) => new() { { k1, v1 }, { k2, v2 } };
}
