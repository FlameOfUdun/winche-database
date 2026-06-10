using Winche.Database.Authorization;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Values;
using Winche.Database.Wire;

namespace Winche.Database.Tests.Ws;

public class ErrorMapperTests
{
    [Theory]
    [InlineData(RuntimeStatus.NotFound, "NOT_FOUND")]
    [InlineData(RuntimeStatus.AlreadyExists, "ALREADY_EXISTS")]
    [InlineData(RuntimeStatus.FailedPrecondition, "FAILED_PRECONDITION")]
    [InlineData(RuntimeStatus.Aborted, "ABORTED")]
    [InlineData(RuntimeStatus.InvalidArgument, "INVALID_ARGUMENT")]
    [InlineData(RuntimeStatus.DeadlineExceeded, "DEADLINE_EXCEEDED")]
    public void RuntimeStatuses_Map(RuntimeStatus status, string wire) =>
        Assert.Equal(wire, ErrorMapper.Map(new RuntimeException(status, "m")).Status);

    [Fact]
    public void TransactionAborted_MapsAborted() =>
        Assert.Equal("ABORTED", ErrorMapper.Map(new TransactionAbortedException("m")).Status);

    [Fact]
    public void QueryParse_InvalidQuery_WithJsonPath()
    {
        var e = ErrorMapper.Map(new QueryParseException("bad", "$.where.op"));
        Assert.Equal("INVALID_QUERY", e.Status);
        Assert.Equal("$.where.op", (string)e.Details!["jsonPath"]!);
    }

    [Fact]
    public void PlanValidation_InvalidQuery_WithCode()
    {
        var e = ErrorMapper.Map(new PlanValidationException("CURSOR_ARITY", "bad"));
        Assert.Equal("INVALID_QUERY", e.Status);
        Assert.Equal("CURSOR_ARITY", (string)e.Details!["code"]!);
    }

    [Fact]
    public void Fallbacks_Map()
    {
        Assert.Equal("INVALID_ARGUMENT", ErrorMapper.Map(new WireFormatException("m")).Status);
        Assert.Equal("INVALID_ARGUMENT", ErrorMapper.Map(new ArgumentException("m")).Status);
        Assert.Equal("INVALID_ARGUMENT", ErrorMapper.Map(new System.Text.Json.JsonException("m")).Status);
        Assert.Equal("INTERNAL", ErrorMapper.Map(new InvalidOperationException("m")).Status);
    }

    [Fact]
    public void UnauthorizedAccess_MapsUnauthenticated()
    {
        var e = ErrorMapper.Map(new UnauthorizedAccessException("token expired"));
        Assert.Equal("UNAUTHENTICATED", e.Status);
        Assert.Equal("token expired", e.Message);
    }

    // ── Phase 4a: native Winche.Database.Authorization.AccessDeniedException ──

    [Fact]
    public void NativeAccessDenied_MapsPermissionDenied()
    {
        var ex = new AccessDeniedException("users/u1", "get");
        var wire = ErrorMapper.Map(ex);
        Assert.Equal("PERMISSION_DENIED", wire.Status);
        Assert.Equal("Access denied.", wire.Message);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("list")]
    public void NativeAccessDenied_AllOperations_MapPermissionDenied(string operation)
    {
        var wire = ErrorMapper.Map(new AccessDeniedException("col/doc", operation));
        Assert.Equal("PERMISSION_DENIED", wire.Status);
    }
}
