using Winche.Database.Values;

namespace Winche.Database.Documents;

/// <summary>Recursive typed merge: maps merge per-key, everything else (incl. arrays and NullValue) replaces.</summary>
public static class DocumentMerger
{
    public static IReadOnlyDictionary<string, Value> Merge(
        IReadOnlyDictionary<string, Value> target,
        IReadOnlyDictionary<string, Value> patch)
    {
        var result = target.ToDictionary(kv => kv.Key, kv => kv.Value);

        foreach (var (key, patchValue) in patch)
        {
            result[key] = target.TryGetValue(key, out var existing)
                          && existing is MapValue em && patchValue is MapValue pm
                ? new MapValue(Merge(em.Fields, pm.Fields))
                : patchValue;
        }

        return result;
    }
}
