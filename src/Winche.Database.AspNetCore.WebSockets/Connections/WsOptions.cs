namespace Winche.Database.AspNetCore.WebSockets.Connections;

public sealed class WsOptions
{
    public int MaxFrameBytes { get; set; } = 1024 * 1024;
    public int SendQueueLimit { get; set; } = 64;

    /// <summary>
    /// The claim that identifies the user, for <see cref="WsConnectionRegistry"/> (default <c>sub</c>; the
    /// mapped <c>NameIdentifier</c> is used when it is absent).
    /// </summary>
    public string UserIdClaimType { get; set; } = "sub";

    /// <summary>
    /// Live queries and document listeners one connection may hold (default 100; 0 = unlimited). Past it, a
    /// <c>listen</c> / <c>doc.listen</c> gets <c>RESOURCE_EXHAUSTED</c>; the connection stays open.
    /// </summary>
    public int MaxSubscriptionsPerConnection { get; set; } = 100;

    /// <summary>
    /// Connections one user may hold on this node (default 10; 0 = unlimited). A new one past it closes the
    /// user's oldest connection (close code 4429) rather than being refused, so an app that reconnects
    /// while its stale sockets linger keeps working.
    /// </summary>
    public int MaxConnectionsPerUser { get; set; } = 10;

    /// <summary>
    /// Requests per second one connection may send, on average (default 50; 0 = unlimited), with bursts of up
    /// to <see cref="RequestBurst"/>. Past it the server reads the connection's next request later instead of
    /// rejecting it — clients slow down but never get an error (an offline write queue would drop a
    /// rejected write).
    /// </summary>
    public int RequestsPerSecond { get; set; } = 50;

    /// <summary>Requests a connection may send at once before <see cref="RequestsPerSecond"/> applies (default 100).</summary>
    public int RequestBurst { get; set; } = 100;

    /// <summary>
    /// Lets the console change the per-user and per-connection limits (settings area <c>database.live</c>); what's set
    /// here becomes the starting values. New connections get the new limits. Needs <c>AddWincheConfig(…)</c> to save.
    /// </summary>
    public WsOptions UseSettings()
    {
        SettingsEnabled = true;
        return this;
    }

    internal bool SettingsEnabled { get; private set; }
}
