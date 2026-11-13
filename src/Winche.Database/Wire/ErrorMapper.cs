using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Winche.Database.Authorization;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Runtime;
using Winche.Database.Values;

namespace Winche.Database.Wire;

/// <summary>Exception → the shared <see cref="WincheError"/> every Winche API answers with (PROTOCOL §5.1).</summary>
public static class ErrorMapper
{
    /// <summary>
    /// <see cref="Map(Exception)"/>, and for an unexpected exception (<c>INTERNAL</c>) also logs it at Error level with
    /// <paramref name="operation"/> and <paramref name="user"/>, under a short error id that the caller gets in the
    /// message and in <c>details.errorId</c> — so a report like "Internal error (3f9c0a1b2c4d)" leads to the log
    /// entry. The client sees no more than before about the exception itself.
    /// </summary>
    public static WincheError Map(Exception ex, ILogger logger, string operation, string? user)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var error = Map(ex);
        return error.Kind != WincheErrorCode.Internal ? error : WincheError.Unexpected(ex, logger, operation, user);
    }

    /// <summary>The caller's user id from claims (<c>uid</c>, else <c>sub</c>), for logs.</summary>
    public static string? UserOf(IReadOnlyDictionary<string, object?>? claims) => WincheCaller.UidOf(claims);

    /// <summary>The reason a query was refused: <c>invalid-argument</c> with this reason.</summary>
    public const string InvalidQuery = "invalid-query";

    public static WincheError Map(Exception ex) => ex switch
    {
        RuntimeException re => new WincheError(re.Status switch
        {
            RuntimeStatus.NotFound => WincheErrorCode.NotFound,
            RuntimeStatus.AlreadyExists => WincheErrorCode.AlreadyExists,
            RuntimeStatus.FailedPrecondition => WincheErrorCode.FailedPrecondition,
            RuntimeStatus.Aborted => WincheErrorCode.Aborted,
            RuntimeStatus.DeadlineExceeded => WincheErrorCode.DeadlineExceeded,
            _ => WincheErrorCode.InvalidArgument,
        }, re.Message),
        QueryParseException qp => new WincheError(WincheErrorCode.InvalidArgument, qp.Message, InvalidQuery,
            new JsonObject { ["jsonPath"] = qp.JsonPath }),
        PlanValidationException pv => new WincheError(WincheErrorCode.InvalidArgument, pv.Message, InvalidQuery,
            new JsonObject { ["code"] = pv.Code }),
        WireFormatException wf => new WincheError(WincheErrorCode.InvalidArgument, wf.Message),
        // Native Winche.Database rules-engine denial
        Authorization.AccessDeniedException => new WincheError(WincheErrorCode.PermissionDenied, "Access denied."),
        UnauthorizedAccessException ua => new WincheError(WincheErrorCode.Unauthenticated, ua.Message),
        System.Text.Json.JsonException { InnerException: QueryParseException qp2 } =>
            new WincheError(WincheErrorCode.InvalidArgument, qp2.Message, InvalidQuery, new JsonObject { ["jsonPath"] = qp2.JsonPath }),
        System.Text.Json.JsonException { InnerException: PlanValidationException pv2 } =>
            new WincheError(WincheErrorCode.InvalidArgument, pv2.Message, InvalidQuery, new JsonObject { ["code"] = pv2.Code }),
        System.Text.Json.JsonException js => new WincheError(WincheErrorCode.InvalidArgument, js.Message),
        // STJ polymorphic deserialization reports a missing/unknown "type" discriminator as
        // NotSupportedException — in wire-handling contexts that is always a caller error.
        NotSupportedException ns => new WincheError(WincheErrorCode.InvalidArgument, ns.Message),
        ArgumentException ae => new WincheError(WincheErrorCode.InvalidArgument, ae.Message),
        _ => new WincheError(WincheErrorCode.Internal, "Internal error."),
    };
}
