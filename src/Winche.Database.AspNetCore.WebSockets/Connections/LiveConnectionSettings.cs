namespace Winche.Database.AspNetCore.WebSockets.Connections;

/// <summary>The settings area <c>database.live</c>: limits on live (WebSocket) connections, changeable in the console.</summary>
public sealed record LiveConnectionSettings
{
    public const string Area = "database.live";

    public int MaxConnectionsPerUser { get; init; } = 10;
    public int MaxSubscriptionsPerConnection { get; init; } = 100;
    public int RequestsPerSecond { get; init; } = 50;
    public int RequestBurst { get; init; } = 100;
    /// <summary>The largest message a client may send, in KB.</summary>
    public int MaxMessageKb { get; init; } = 1024;

    public static LiveConnectionSettings From(WsOptions o) => new()
    {
        MaxConnectionsPerUser = o.MaxConnectionsPerUser, MaxSubscriptionsPerConnection = o.MaxSubscriptionsPerConnection,
        RequestsPerSecond = o.RequestsPerSecond, RequestBurst = o.RequestBurst, MaxMessageKb = o.MaxFrameBytes / 1024,
    };

    internal void ApplyTo(WsOptions o)
    {
        o.MaxConnectionsPerUser = MaxConnectionsPerUser;
        o.MaxSubscriptionsPerConnection = MaxSubscriptionsPerConnection;
        o.RequestsPerSecond = RequestsPerSecond;
        o.RequestBurst = RequestBurst;
        o.MaxFrameBytes = MaxMessageKb * 1024;
    }

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (MaxConnectionsPerUser is < 0 or > 1000) problems.Add("Connections per user: 0 (no limit) to 1000.");
        if (MaxSubscriptionsPerConnection is < 0 or > 10000) problems.Add("Listeners per connection: 0 (no limit) to 10000.");
        if (RequestsPerSecond is < 1 or > 10000) problems.Add("Requests per second: 1 to 10000.");
        if (RequestBurst < RequestsPerSecond) problems.Add("The burst must be at least the requests per second.");
        if (MaxMessageKb is < 1 or > 16 * 1024) problems.Add("Messages may be 1 KB to 16 MB.");
        return problems;
    }
}
