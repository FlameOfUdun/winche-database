using System.Text.Json.Nodes;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Runtime;
using Winche.Database.Values;
using Winche.Sentinel.Models;

namespace Winche.Database.Wire;

public sealed record WireError(string Status, string Message, JsonObject? Details = null);

/// <summary>Exception → spec §1 status vocabulary.</summary>
public static class ErrorMapper
{
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
        AccessDeniedException => new WireError("PERMISSION_DENIED", "Access denied."),
        NoRulesMatchedException => new WireError("PERMISSION_DENIED", "No rule matched."),
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
