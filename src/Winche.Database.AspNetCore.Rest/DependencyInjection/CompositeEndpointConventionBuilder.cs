using Microsoft.AspNetCore.Builder;

namespace Winche.Database.AspNetCore.Rest.DependencyInjection;

/// <summary>
/// Fans endpoint conventions out to several underlying builders, so a single returned builder can
/// represent the whole REST surface — the route group (CRUD + ping) plus the standalone colon-verb
/// routes (<c>:commit</c>, <c>:runQuery</c>, <c>:aggregate</c>, …). A caller can apply
/// <c>.RequireAuthorization()</c>, rate limiting, CORS, metadata, etc. once and have it land on
/// every endpoint, including the verbs — which carry the most sensitive operations (writes via
/// <c>:commit</c>, expensive reads via <c>:runQuery</c>/<c>:aggregate</c>).
/// </summary>
internal sealed class CompositeEndpointConventionBuilder(IReadOnlyList<IEndpointConventionBuilder> builders)
    : IEndpointConventionBuilder
{
    public void Add(Action<EndpointBuilder> convention)
    {
        foreach (var builder in builders)
            builder.Add(convention);
    }

    public void Finally(Action<EndpointBuilder> finallyConvention)
    {
        foreach (var builder in builders)
            builder.Finally(finallyConvention);
    }
}
