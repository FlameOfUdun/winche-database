using Microsoft.AspNetCore.Http;
using Winche.Database.AspNetCore.Abstraction;

namespace Winche.Database.AspNetCore.WebSockets.DependencyInjection;

/// <summary>
/// Token → claims seam for the hello handshake and auth.refresh (spec §1). The default
/// ignores the token and uses the HttpContext-based mapping (browser clients pass the token
/// in hello because they cannot set WS upgrade headers — products override this to validate it).
/// Throw UnauthorizedAccessException to reject (→ error + close 4401 at hello; UNAUTHENTICATED on refresh).
/// </summary>
public interface IWsAuthenticator
{
    ValueTask<IReadOnlyDictionary<string, object?>> AuthenticateAsync(
        HttpContext context, string? token, CancellationToken ct);
}

public sealed class DefaultWsAuthenticator(DocumentClaimsAccessor claimsAccessor) : IWsAuthenticator
{
    public ValueTask<IReadOnlyDictionary<string, object?>> AuthenticateAsync(
        HttpContext context, string? token, CancellationToken ct) =>
        ValueTask.FromResult(claimsAccessor.MapClaims(context));
}
