namespace Winche.Database.AspNetCore.WebSockets.Connections;

public sealed class WsOptions
{
    public int MaxFrameBytes { get; set; } = 1024 * 1024;
    public int SendQueueLimit { get; set; } = 64;

    /// <summary>
    /// Absolute cap on how long a single connection may stay open, measured from the HTTP
    /// upgrade. <see langword="null"/> (default) means no cap. Once it elapses the server closes
    /// the connection with code <c>4401</c>; the client is expected to reconnect.
    /// </summary>
    public TimeSpan? MaxConnectionLifetime { get; set; }

    /// <summary>
    /// When <see langword="true"/> (default) and the authenticated principal carries a standard
    /// JWT <c>exp</c> claim (NumericDate — seconds since the Unix epoch), the server closes the
    /// connection once that instant passes, with code <c>4401</c>; the client is expected to
    /// reconnect with a fresh token. Has no effect when the principal carries no <c>exp</c> claim,
    /// so it is inert for non-JWT authentication setups.
    /// </summary>
    public bool CloseOnTokenExpiry { get; set; } = true;

    /// <summary>
    /// Upper bound on how long a draining close (see <see cref="WsConnection.DrainAndCloseAsync"/>)
    /// waits for the send loop to flush queued frames before the socket is closed regardless.
    /// Bounds teardown against a client that has stopped reading. Default: 5 seconds.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
