using System.Text;
using Winche.Database.Values;

namespace Winche.Database.Runtime.Writes;

/// <summary>
/// Post-apply validation of a resulting document against <see cref="WriteLimits"/>: published
/// byte-budget size, nesting depth, and reserved field-name (__*__) rules. All violations are
/// INVALID_ARGUMENT. Runs on the already-applied field map (no sentinels present).
/// </summary>
public static class DocumentLimits
{
    /// <summary>Validates the resulting <paramref name="fields"/> map of the document at
    /// <paramref name="documentPath"/> against <paramref name="limits"/>; throws INVALID_ARGUMENT on violation.</summary>
    public static void Validate(string documentPath, IReadOnlyDictionary<string, Value> fields, WriteLimits limits)
    {
        var size = NameSize(documentPath) + 32 + MapBudget(fields); // 32 = per-document overhead (documented byte budget)
        if (size > limits.MaxDocumentSizeBytes)
            throw new RuntimeException(RuntimeStatus.InvalidArgument,
                $"Document '{documentPath}' is {size} bytes, exceeding the {limits.MaxDocumentSizeBytes}-byte limit.");

        foreach (var (key, value) in fields)
            CheckField(documentPath, key, value, depth: 1, limits);
    }

    // ── size (published byte budget) ──
    private static long MapBudget(IReadOnlyDictionary<string, Value> fields)
    {
        long sum = 0;
        foreach (var (key, value) in fields)
            sum += Utf8(key) + 1 + ValueBudget(value);
        return sum;
    }

    private static long ValueBudget(Value value) => value switch
    {
        NullValue or BooleanValue => 1,
        IntegerValue or DoubleValue or TimestampValue => 8,
        GeoPointValue => 16,
        StringValue s => Utf8(s.Value) + 1,
        BytesValue b => b.Value.Length,
        ReferenceValue r => NameSize(r.Path) + 16,
        ArrayValue a => ArrayBudget(a.Values),
        MapValue m => MapBudget(m.Fields),
        _ => throw new InvalidOperationException($"Unhandled Value subtype in size budget: {value.GetType().Name}"),
    };

    private static long ArrayBudget(IReadOnlyList<Value> values)
    {
        long sum = 0;
        foreach (var v in values)
            sum += ValueBudget(v);
        return sum;
    }

    private static long NameSize(string path)
    {
        long sum = 16;
        foreach (var segment in path.Split('/'))
            sum += Utf8(segment) + 1;
        return sum;
    }

    private static int Utf8(string s) => Encoding.UTF8.GetByteCount(s);

    // ── depth + field-name ──
    private static void CheckField(string docPath, string key, Value value, int depth, WriteLimits limits)
    {
        if (key.Length == 0)
            throw new RuntimeException(RuntimeStatus.InvalidArgument, $"Document '{docPath}' has an empty field name.");
        if (limits.RejectReservedFieldNames && key.Length >= 4 && key.StartsWith("__") && key.EndsWith("__"))
            throw new RuntimeException(RuntimeStatus.InvalidArgument,
                $"Document '{docPath}' field name '{key}' is reserved (matches __*__).");
        CheckDepth(docPath, value, depth, limits);
    }

    private static void CheckDepth(string docPath, Value value, int depth, WriteLimits limits)
    {
        if (depth > limits.MaxDepth)
            throw new RuntimeException(RuntimeStatus.InvalidArgument,
                $"Document '{docPath}' exceeds the maximum nesting depth of {limits.MaxDepth}.");
        switch (value)
        {
            case MapValue m:
                foreach (var (k, v) in m.Fields)
                    CheckField(docPath, k, v, depth + 1, limits);
                break;
            case ArrayValue a:
                foreach (var v in a.Values)
                    CheckDepth(docPath, v, depth + 1, limits);
                break;
        }
    }
}
