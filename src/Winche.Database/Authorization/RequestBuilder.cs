using Winche.Rules;

namespace Winche.Database.Authorization;

/// <summary>
/// Builds the <c>request</c> map passed to the rules engine.
/// </summary>
/// <remarks>
/// Produced map structure:
/// <code>
/// {
///   "auth"     : Null  — when <paramref name="claims"/> is null, empty, or has no usable uid
///              | Map { "uid": String, "token": Map of all claims }
///   "resource" : the incoming document resource (or <see cref="RuleValue.Null"/> for deletes)
///   "method"   : String (e.g. "create", "update", "delete", "get", "list")
///   "time"     : Timestamp(<see cref="DateTimeOffset.UtcNow"/>)
/// }
/// </code>
/// A non-null <c>auth</c> always carries a non-empty <c>uid</c> — a claims dictionary with a
/// missing, null, or blank <c>uid</c> is treated as unauthenticated regardless of what other
/// claims it carries.
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

    /// <summary>
    /// Extracts a usable <c>uid</c> from <paramref name="claims"/>, applying the SAME
    /// authenticated-caller rule this class uses to populate <c>request.auth</c>: a <c>"uid"</c>
    /// claim must be present, non-null, and non-blank — anything else (no claims, missing uid,
    /// null uid, blank uid) is unauthenticated. Shared with
    /// <see cref="RuleGuardedDocumentDatabase"/>'s transaction-ownership tracking so the two
    /// notions of "authenticated" cannot drift apart.
    /// </summary>
    /// <param name="claims">Authentication claims — pass <c>null</c> or an empty dictionary for unauthenticated.</param>
    /// <param name="uid">The extracted uid, or <see cref="string.Empty"/> when unauthenticated.</param>
    /// <returns><see langword="true"/> if a usable uid was found; otherwise <see langword="false"/>.</returns>
    internal static bool TryGetUid(IReadOnlyDictionary<string, object?>? claims, out string uid)
    {
        uid = string.Empty;
        if (claims is null || claims.Count == 0)
            return false;

        if (!claims.TryGetValue("uid", out var rawUid) || rawUid is null)
            return false;

        var s = rawUid.ToString();
        if (string.IsNullOrWhiteSpace(s))
            return false;

        uid = s;
        return true;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static RuleValue BuildAuth(IReadOnlyDictionary<string, object?>? claims)
    {
        if (!TryGetUid(claims, out var uid))
            return RuleValue.Null;

        var token = RuleValue.Map(
            claims!.ToDictionary(kv => kv.Key, kv => ConvertClaimValue(kv.Value)));

        return RuleValue.Map(new Dictionary<string, RuleValue>
        {
            ["uid"] = RuleValue.String(uid),
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
        IReadOnlyDictionary<string, object?> nested => RuleValue.Map(nested.ToDictionary(kv => kv.Key, kv => ConvertClaimValue(kv.Value))),
        IDictionary<string, object?> nested => RuleValue.Map(nested.ToDictionary(kv => kv.Key, kv => ConvertClaimValue(kv.Value))),
        IEnumerable<object?> list => RuleValue.List(list.Select(ConvertClaimValue).ToList()),
        _ => RuleValue.String(value.ToString() ?? string.Empty),
    };
}
