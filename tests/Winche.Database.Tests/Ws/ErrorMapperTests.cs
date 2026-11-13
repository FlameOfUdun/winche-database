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
    [InlineData(RuntimeStatus.NotFound, "not-found")]
    [InlineData(RuntimeStatus.AlreadyExists, "already-exists")]
    [InlineData(RuntimeStatus.FailedPrecondition, "failed-precondition")]
    [InlineData(RuntimeStatus.Aborted, "aborted")]
    [InlineData(RuntimeStatus.InvalidArgument, "invalid-argument")]
    [InlineData(RuntimeStatus.DeadlineExceeded, "deadline-exceeded")]
    public void RuntimeStatuses_Map(RuntimeStatus status, string wire) =>
        Assert.Equal(wire, ErrorMapper.Map(new RuntimeException(status, "m")).Code);

    [Fact]
    public void TransactionAborted_MapsAborted() =>
        Assert.Equal("aborted", ErrorMapper.Map(new TransactionAbortedException("m")).Code);

    [Fact]
    public void QueryParse_InvalidQuery_WithJsonPath()
    {
        var e = ErrorMapper.Map(new QueryParseException("bad", "$.where.op"));
        Assert.Equal(("invalid-argument", "invalid-query"), (e.Code, e.Reason));
        Assert.Equal("$.where.op", (string)e.Details!["jsonPath"]!);
    }

    [Fact]
    public void PlanValidation_InvalidQuery_WithCode()
    {
        var e = ErrorMapper.Map(new PlanValidationException("CURSOR_ARITY", "bad"));
        Assert.Equal(("invalid-argument", "invalid-query"), (e.Code, e.Reason));
        Assert.Equal("CURSOR_ARITY", (string)e.Details!["code"]!);
    }

    [Fact]
    public void Fallbacks_Map()
    {
        Assert.Equal("invalid-argument", ErrorMapper.Map(new WireFormatException("m")).Code);
        Assert.Equal("invalid-argument", ErrorMapper.Map(new ArgumentException("m")).Code);
        Assert.Equal("invalid-argument", ErrorMapper.Map(new System.Text.Json.JsonException("m")).Code);
        Assert.Equal("internal", ErrorMapper.Map(new InvalidOperationException("m")).Code);
    }

    [Fact]
    public void UnauthorizedAccess_MapsUnauthenticated()
    {
        var e = ErrorMapper.Map(new UnauthorizedAccessException("token expired"));
        Assert.Equal("unauthenticated", e.Code);
        Assert.Equal("token expired", e.Message);
    }

    // ── Phase 4a: native Winche.Database.Authorization.AccessDeniedException ──

    [Fact]
    public void NativeAccessDenied_MapsPermissionDenied()
    {
        var ex = new AccessDeniedException("users/u1", "get");
        var wire = ErrorMapper.Map(ex);
        Assert.Equal("permission-denied", wire.Code);
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
        Assert.Equal("permission-denied", wire.Code);
    }
}
