using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using WincheDatabase.WS.Services;

namespace WincheDatabase.WS.DependencyInjection
{
    public static class WebApplicationExtensions
    {
        public static WebApplication UseWincheDatabaseWsApi(
            this WebApplication app, 
            string prefix = "documents", 
            Func<HttpContext, Task<Dictionary<string, object?>>>? mapClaims = null
        )
        {
            app.UseWebSockets();

            var group = app.MapGroup(prefix);

            group.MapGet("/ws", async (HttpContext context, ConnectionManager connectionManager) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("WebSocket connection required");
                    return;
                }

                var socket = await context.WebSockets.AcceptWebSocketAsync();
                var claims = mapClaims != null ? await mapClaims(context) : [];
                await connectionManager.AcceptAsync(socket, claims);
            });

            return app;
        }
    }
}