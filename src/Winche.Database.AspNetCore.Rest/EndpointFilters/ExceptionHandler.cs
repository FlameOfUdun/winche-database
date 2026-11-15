using Microsoft.Extensions.Logging;
using Winche.Database.Wire;

namespace Winche.Database.AspNetCore.Rest.EndpointFilters;

/// <summary>Answers failures with the shared Winche error (<see cref="ErrorMapper"/>); unexpected ones are logged under an id.</summary>
internal class ExceptionHandler(ILogger<ExceptionHandler> logger) : WincheErrorFilter(logger)
{
    protected override WincheError? Map(Exception exception) =>
        ErrorMapper.Map(exception) is { Kind: not WincheErrorCode.Internal } error ? error : null;
}
