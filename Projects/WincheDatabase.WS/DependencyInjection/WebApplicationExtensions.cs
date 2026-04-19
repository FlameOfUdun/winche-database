using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WincheDatabase.Store.Services;
using WincheDatabase.WS.Abstraction;
using WincheDatabase.WS.EndpointFilters;
using WincheDatabase.WS.Services;

namespace WincheDatabase.WS.DependencyInjection
{
    public static class WebApplicationExtensions
    {
        public static WebApplication UseWincheDatabaseWsApi(this WebApplication app, string prefix = "documents", Action<RouteGroupBuilder>? configure = null)
        {
            app.UseWebSockets();

            var group = app.MapGroup(prefix);

            configure?.Invoke(group);

            group.AddEndpointFilter<CallerAccessor>();

            group.MapGet("/ws", async (HttpContext context, CallerContextAccessor accessor, IConnectionManager manager) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("WebSocket connection required");
                    return;
                }

                var socket = await context.WebSockets.AcceptWebSocketAsync();
                var claims = accessor.GetClaims();
                await manager.AcceptAsync(socket, claims);
            });

            return app;
        }
    }
}