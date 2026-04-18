using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using WincheDatabase.WS.Abstraction;
using WincheDatabase.WS.Services;

namespace WincheDatabase.WS.DependencyInjection
{
    public static class WebApplicationExtensions
    {
        public static WebApplication UseWincheDatabaseWsApi(this WebApplication app, string prefix = "documents")
        {
            app.UseWebSockets();

            var group = app.MapGroup(prefix);

            group.MapGet("/ws", async (HttpContext context, WsClaimsMapper claimsMapper, IConnectionManager connectionManager) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("WebSocket connection required");
                    return;
                }

                var socket = await context.WebSockets.AcceptWebSocketAsync();
                var claims = claimsMapper.MapClaims(context);
                await connectionManager.AcceptAsync(socket, claims);
            });

            return app;
        }
    }
}