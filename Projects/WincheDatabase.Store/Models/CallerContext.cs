namespace WincheDatabase.Store.Models;

public static class CallerContext
{
    private static readonly AsyncLocal<IReadOnlyDictionary<string, object?>?> _claims = new();

    public static IReadOnlyDictionary<string, object?>? Claims => _claims.Value;

    public static void SetClaims(IReadOnlyDictionary<string, object?>? claims) => _claims.Value = claims;
}
