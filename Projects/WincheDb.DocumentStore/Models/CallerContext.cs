namespace WincheDb.DocumentStore.Models;

public static class CallerContext
{
    private static readonly AsyncLocal<IReadOnlyDictionary<string, object?>?> _claims = new();

    public static IReadOnlyDictionary<string, object?>? Claims
    {
        get => _claims.Value;
        set => _claims.Value = value;
    }
}
