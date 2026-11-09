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
}
