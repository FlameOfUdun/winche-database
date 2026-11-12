using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Winche.Database.Authorization;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Runtime;
using Winche.Database.Values;

namespace Winche.Database.Wire;

public sealed record WireError(string Status, string Message, JsonObject? Details = null);

/// <summary>Exception → spec §1 status vocabulary.</summary>
public static class ErrorMapper
{
    /// <summary>
    /// <see cref="Map(Exception)"/>, and for an unexpected exception (<c>INTERNAL</c>) also logs it at Error level with
    /// <paramref name="operation"/> and <paramref name="user"/>, under a short error id that the caller gets in the
    /// message and in <c>details.errorId</c> — so a report like "Internal error (3f9c0a1b2c4d)" leads to the log
    /// entry. The client sees no more than before about the exception itself.
    /// </summary>
    public static WireError Map(Exception ex, ILogger logger, string operation, string? user)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var error = Map(ex);
        if (error.Status != "INTERNAL") return error;
        var errorId = Guid.NewGuid().ToString("N")[..12];
        logger.LogError(ex, "Internal error {ErrorId} in {Operation} for {User}", errorId, operation, user ?? "(signed out)");
        return error with { Message = $"Internal error ({errorId}).", Details = new JsonObject { ["errorId"] = errorId } };
    }

    /// <summary>The caller's user id from claims (<c>uid</c>, else <c>sub</c>), for logs.</summary>
    public static string? UserOf(IReadOnlyDictionary<string, object?>? claims) =>
        claims is null ? null
        : claims.TryGetValue("uid", out var uid) && uid is not null ? uid.ToString()
        : claims.TryGetValue("sub", out var sub) && sub is not null ? sub.ToString()
        : null;

    public static WireError Map(Exception ex) => ex switch
    {
        RuntimeException re => new WireError(re.Status switch
        {
            RuntimeStatus.NotFound => "NOT_FOUND",
            RuntimeStatus.AlreadyExists => "ALREADY_EXISTS",
            RuntimeStatus.FailedPrecondition => "FAILED_PRECONDITION",
            RuntimeStatus.Aborted => "ABORTED",
            RuntimeStatus.DeadlineExceeded => "DEADLINE_EXCEEDED",
            _ => "INVALID_ARGUMENT",
        }, re.Message),
        QueryParseException qp => new WireError("INVALID_QUERY", qp.Message,
            new JsonObject { ["jsonPath"] = qp.JsonPath }),
        PlanValidationException pv => new WireError("INVALID_QUERY", pv.Message,
            new JsonObject { ["code"] = pv.Code }),
        WireFormatException wf => new WireError("INVALID_ARGUMENT", wf.Message),
        // Native Winche.Database rules-engine denial
        Authorization.AccessDeniedException => new WireError("PERMISSION_DENIED", "Access denied."),
        UnauthorizedAccessException ua => new WireError("UNAUTHENTICATED", ua.Message),
        System.Text.Json.JsonException { InnerException: QueryParseException qp2 } =>
            new WireError("INVALID_QUERY", qp2.Message, new JsonObject { ["jsonPath"] = qp2.JsonPath }),
        System.Text.Json.JsonException { InnerException: PlanValidationException pv2 } =>
            new WireError("INVALID_QUERY", pv2.Message, new JsonObject { ["code"] = pv2.Code }),
        System.Text.Json.JsonException js => new WireError("INVALID_ARGUMENT", js.Message),
        // STJ polymorphic deserialization reports a missing/unknown "type" discriminator as
        // NotSupportedException — in wire-handling contexts that is always a caller error.
        NotSupportedException ns => new WireError("INVALID_ARGUMENT", ns.Message),
        ArgumentException ae => new WireError("INVALID_ARGUMENT", ae.Message),
        _ => new WireError("INTERNAL", "Internal error."),
    };
}
