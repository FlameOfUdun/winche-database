using Winche.Rules;

namespace Winche.Database.Authorization;

/// <summary>
/// Builds the Firestore-style <c>request</c> map passed to the rules engine.
/// </summary>
/// <remarks>
/// Produced map structure:
/// <code>
/// {
///   "auth"     : Null  — when <paramref name="claims"/> is null or empty
///              | Map { "uid": String|Null, "token": Map of all claims }
///   "resource" : the incoming document resource (or <see cref="RuleValue.Null"/> for deletes)
///   "method"   : String (e.g. "create", "update", "delete", "get", "list")
///   "time"     : Timestamp(<see cref="DateTimeOffset.UtcNow"/>)
/// }
/// </code>
/// </remarks>
internal static class RequestBuilder
{
    /// <summary>Builds the request map using <see cref="DateTimeOffset.UtcNow"/> as <c>request.time</c>.</summary>
    /// <param name="claims">Authentication claims — pass <c>null</c> or an empty dictionary for unauthenticated.</param>
    /// <param name="method">The method string (e.g. <c>"create"</c>, <c>"get"</c>, <c>"list"</c>).</param>
    /// <param name="requestResource">The incoming resource map; use <see cref="RuleValue.Null"/> for deletes/reads with no body.</param>
    /// <returns>A <see cref="RuleValue.Map"/> representing the full <c>request</c> binding.</returns>
    public static RuleValue Build(
        IReadOnlyDictionary<string, object?>? claims,
        string method,
        RuleValue requestResource) =>
        Build(claims, method, requestResource, DateTimeOffset.UtcNow);

    /// <summary>
    /// Builds the request map with an explicit <paramref name="now"/> timestamp for <c>request.time</c>.
    /// Use this overload to ensure <c>request.time</c> equals the same instant used to resolve
    /// <c>serverTimestamp</c> transforms in <c>request.resource</c>.
    /// </summary>
    /// <param name="claims">Authentication claims — pass <c>null</c> or an empty dictionary for unauthenticated.</param>
    /// <param name="method">The method string (e.g. <c>"create"</c>, <c>"get"</c>, <c>"list"</c>).</param>
    /// <param name="requestResource">The incoming resource map; use <see cref="RuleValue.Null"/> for deletes/reads with no body.</param>
    /// <param name="now">The timestamp to use for <c>request.time</c>.</param>
    /// <returns>A <see cref="RuleValue.Map"/> representing the full <c>request</c> binding.</returns>
    public static RuleValue Build(
        IReadOnlyDictionary<string, object?>? claims,
        string method,
        RuleValue requestResource,
        DateTimeOffset now)
    {
        var auth = BuildAuth(claims);

        return RuleValue.Map(new Dictionary<string, RuleValue>
        {
            ["auth"] = auth,
            ["resource"] = requestResource,
            ["method"] = RuleValue.String(method),
            ["time"] = RuleValue.Timestamp(now),
        });
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static RuleValue BuildAuth(IReadOnlyDictionary<string, object?>? claims)
    {
        if (claims is null || claims.Count == 0)
            return RuleValue.Null;

        var uid = claims.TryGetValue("uid", out var rawUid) && rawUid is not null
            ? RuleValue.String(rawUid.ToString()!)
            : RuleValue.Null;

        var token = RuleValue.Map(
            claims.ToDictionary(kv => kv.Key, kv => ConvertClaimValue(kv.Value)));

        return RuleValue.Map(new Dictionary<string, RuleValue>
        {
            ["uid"] = uid,
            ["token"] = token,
        });
    }

    /// <summary>
    /// Converts a claim value (typically string/bool/long/double or nested dictionary/list)
    /// to a <see cref="RuleValue"/>. Null claim values map to <see cref="RuleValue.Null"/>.
    /// </summary>
    private static RuleValue ConvertClaimValue(object? value) => value switch
    {
        null => RuleValue.Null,
        bool b => RuleValue.Bool(b),
        long l => RuleValue.Int(l),
        int i => RuleValue.Int(i),
        double d => RuleValue.Double(d),
        float f => RuleValue.Double(f),
        string s => RuleValue.String(s),
        byte[] bytes => RuleValue.Bytes(bytes),
        DateTimeOffset dto => RuleValue.Timestamp(dto),
        IReadOnlyDictionary<string, object?> nested =>
            RuleValue.Map(nested.ToDictionary(kv => kv.Key, kv => ConvertClaimValue(kv.Value))),
        IDictionary<string, object?> nested =>
            RuleValue.Map(nested.ToDictionary(kv => kv.Key, kv => ConvertClaimValue(kv.Value))),
        IEnumerable<object?> list =>
            RuleValue.List(list.Select(ConvertClaimValue).ToList()),
        _ => RuleValue.String(value.ToString() ?? string.Empty),
    };
}
