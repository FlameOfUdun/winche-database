using Winche.Database.Authorization;
using Winche.Rules;

namespace Winche.Database.Tests.Authorization;

public class RequestBuilderTests
{
    private static readonly RuleValue AnyResource = RuleValue.Null;

    [Fact]
    public void Build_Unauthenticated_NullClaims_AuthIsNull()
    {
        var request = RequestBuilder.Build(null, "get", AnyResource);
        Assert.Equal(RuleValueKind.Map, request.Kind);
        Assert.Equal(RuleValueKind.Null, request.AsMap["auth"].Kind);
    }

    [Fact]
    public void Build_Unauthenticated_EmptyClaims_AuthIsNull()
    {
        var request = RequestBuilder.Build(new Dictionary<string, object?>(), "get", AnyResource);
        Assert.Equal(RuleValueKind.Null, request.AsMap["auth"].Kind);
    }

    [Fact]
    public void Build_Authenticated_AuthContainsUidAndToken()
    {
        var claims = new Dictionary<string, object?>
        {
            ["uid"] = "user123",
            ["email"] = "user@example.com",
        };
        var request = RequestBuilder.Build(claims, "create", AnyResource);
        var auth = request.AsMap["auth"];

        Assert.Equal(RuleValueKind.Map, auth.Kind);
        Assert.Equal("user123", auth.AsMap["uid"].AsString);
        Assert.Equal(RuleValueKind.Map, auth.AsMap["token"].Kind);
        Assert.Equal("user@example.com", auth.AsMap["token"].AsMap["email"].AsString);
    }

    [Fact]
    public void Build_Authenticated_MissingUidClaim_UidIsNull()
    {
        var claims = new Dictionary<string, object?> { ["role"] = "admin" };
        var request = RequestBuilder.Build(claims, "delete", AnyResource);
        var auth = request.AsMap["auth"];
        Assert.Equal(RuleValueKind.Null, auth.AsMap["uid"].Kind);
    }

    [Fact]
    public void Build_Authenticated_NullUidValue_UidIsNull()
    {
        var claims = new Dictionary<string, object?> { ["uid"] = null };
        var request = RequestBuilder.Build(claims, "get", AnyResource);
        Assert.Equal(RuleValueKind.Null, request.AsMap["auth"].AsMap["uid"].Kind);
    }

    [Fact]
    public void Build_MethodAndResource_ArePresentInMap()
    {
        var resource = RuleValue.Map(new Dictionary<string, RuleValue>
        {
            ["ownerId"] = RuleValue.String("u1"),
        });
        var request = RequestBuilder.Build(null, "list", resource);

        Assert.Equal("list", request.AsMap["method"].AsString);
        Assert.Equal(RuleValueKind.Map, request.AsMap["resource"].Kind);
    }

    [Fact]
    public void Build_TimeField_IsTimestamp_CloseToNow()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-2);
        var request = RequestBuilder.Build(null, "get", AnyResource);
        var after = DateTimeOffset.UtcNow.AddSeconds(2);

        var time = request.AsMap["time"];
        Assert.Equal(RuleValueKind.Timestamp, time.Kind);
        Assert.True(time.AsTimestamp >= before);
        Assert.True(time.AsTimestamp <= after);
    }

    [Fact]
    public void Build_ClaimBoolValue_IsConvertedToRuleBool()
    {
        var claims = new Dictionary<string, object?> { ["uid"] = "u1", ["emailVerified"] = true };
        var request = RequestBuilder.Build(claims, "write", AnyResource);
        var token = request.AsMap["auth"].AsMap["token"];
        Assert.Equal(RuleValueKind.Bool, token.AsMap["emailVerified"].Kind);
        Assert.True(token.AsMap["emailVerified"].AsBool);
    }

    [Fact]
    public void Build_ClaimLongValue_IsConvertedToRuleInt()
    {
        var claims = new Dictionary<string, object?> { ["uid"] = "u1", ["level"] = 5L };
        var request = RequestBuilder.Build(claims, "write", AnyResource);
        var token = request.AsMap["auth"].AsMap["token"];
        Assert.Equal(RuleValueKind.Int, token.AsMap["level"].Kind);
        Assert.Equal(5L, token.AsMap["level"].AsInt);
    }
}
