namespace Winche.Database.AspNetCore.WebSockets.Connections;

public sealed class WsOptions
{
    public int MaxFrameBytes { get; set; } = 1024 * 1024;
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int SendQueueLimit { get; set; } = 64;
}
