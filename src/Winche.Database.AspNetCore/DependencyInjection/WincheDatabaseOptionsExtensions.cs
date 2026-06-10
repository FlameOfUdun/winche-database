using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Winche.Database.AspNetCore.Abstraction;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;

namespace Winche.Database.AspNetCore.DependencyInjection;

public static class WincheDatabaseOptionsExtensions
{
    /// <summary>
    /// Registers a delegate-based claims accessor that maps an <see cref="HttpContext"/> to the
    /// caller-claims dictionary consumed by the Winche.Rules guard.
    /// <para>
    /// The accessor is a <b>singleton</b>; it carries claims per async execution context via
    /// <see cref="System.Threading.AsyncLocal{T}"/> so concurrent requests are isolated without
    /// introducing a captive dependency.
    /// </para>
    /// <para>
    /// DI services are available inside the delegate via <c>httpContext.RequestServices</c>.
    /// </para>
    /// </summary>
    /// <param name="map">
    /// A function that receives the current <see cref="HttpContext"/> and returns the claims
    /// dictionary, or <see langword="null"/> for an unauthenticated caller.
    /// </param>
    public static WincheDatabaseOptions MapClaims(
        this WincheDatabaseOptions options,
        Func<HttpContext, IReadOnlyDictionary<string, object?>?> map)
    {
        var accessor = new DelegateClaimsAccessor(map);
        // Register as DocumentClaimsAccessor so transport packages (ClaimsAccessor filter /
        // ConnectionScope) can resolve it for SetClaims().
        options.Services.AddSingleton<DocumentClaimsAccessor>(accessor);
        // Register as IRuleClaimsAccessor — added AFTER the null-fallback that AddWincheDatabase
        // installs, so GetRequiredService<IRuleClaimsAccessor>() returns this one.
        options.Services.AddSingleton<IRuleClaimsAccessor>(accessor);
        return options;
    }
}

/// <summary>
/// Internal claims accessor backed by a user-supplied <see cref="Func{HttpContext, TResult}"/>
/// delegate. Registered by <see cref="WincheDatabaseOptionsExtensions.MapClaims"/>.
/// </summary>
internal sealed class DelegateClaimsAccessor(
    Func<HttpContext, IReadOnlyDictionary<string, object?>?> map) : DocumentClaimsAccessor
{
    public override IReadOnlyDictionary<string, object?>? MapClaims(HttpContext httpContext) =>
        map(httpContext);
}
