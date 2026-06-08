using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Database.AspNetCore.DependencyInjection;
using Winche.Database.AspNetCore.Rest.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.DependencyInjection;
using Winche.Database.DependencyInjection;
using Winche.Database.IntegrationTests.Ws;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// The Map* methods return an IEndpointConventionBuilder covering their whole surface. These tests
/// pin that a convention applied to the returned builder reaches the REST group routes AND every
/// colon-verb (the part that used to be silently missable), plus the WS upgrade route. No Postgres:
/// endpoints are only materialized, never served, so no connection is opened.
/// </summary>
public class EndpointConventionTests
{
    private sealed class Marker;

    private static List<RouteEndpoint> MapAndMaterialize(Marker restMarker, Marker wsMarker)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddWincheDatabase(o =>
        {
            o.ConnectionString = "Host=localhost;Database=t;Username=t;Password=t";   // never connected
            o.SetCallerClaimsAccessor<TestClaimsAccessor>();
        });
        builder.Services.AddWincheDatabaseWsApi();

        var app = builder.Build();
        app.UseWebSockets();
        app.MapWincheDatabaseRestApi().WithMetadata(restMarker);
        app.MapWincheDatabaseWsApi().WithMetadata(wsMarker);

        return [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()];
    }

    [Fact]
    public void RestBuilder_AppliesConventionToGroupRoutesAndEveryVerb()
    {
        var restMarker = new Marker();
        var endpoints = MapAndMaterialize(restMarker, new Marker());

        var groupRoute = endpoints.First(e => e.RoutePattern.RawText!.Contains("{path}"));
        Assert.Contains(restMarker, groupRoute.Metadata);

        // Every colon-verb must carry the convention — this is what the composite builder guarantees
        // and what a bare RouteGroupBuilder return would have silently missed.
        foreach (var verb in new[] { ":commit", ":beginTransaction", ":rollback", ":batchGet", ":runQuery", ":aggregate" })
        {
            var verbRoute = endpoints.First(e => e.RoutePattern.RawText!.Contains(verb));
            Assert.Contains(restMarker, verbRoute.Metadata);
        }
    }

    [Fact]
    public void WsBuilder_AppliesConventionToUpgradeRoute_AndStaysIsolatedFromRest()
    {
        var restMarker = new Marker();
        var wsMarker = new Marker();
        var endpoints = MapAndMaterialize(restMarker, wsMarker);

        var wsRoute = endpoints.First(e => e.RoutePattern.RawText!.Contains("/ws"));
        Assert.Contains(wsMarker, wsRoute.Metadata);

        // Each returned builder is independent: the WS convention does not leak onto a REST verb,
        // and the REST convention does not leak onto the WS route.
        var runQuery = endpoints.First(e => e.RoutePattern.RawText!.Contains(":runQuery"));
        Assert.DoesNotContain(wsMarker, runQuery.Metadata);
        Assert.DoesNotContain(restMarker, wsRoute.Metadata);
    }
}
