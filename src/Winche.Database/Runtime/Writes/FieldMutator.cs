using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Runtime.Writes;

/// <summary>
/// Immutable dotted-path mutations over typed field maps (Firestore update semantics):
/// Set creates intermediate maps and REPLACES non-map intermediates; Delete of a missing
/// path is a no-op. Inputs are never mutated.
/// </summary>
public static class FieldMutator
{
    public static Dictionary<string, Value> Set(
        IReadOnlyDictionary<string, Value> fields, FieldPath path, Value value) =>
        SetSegments(fields, path.Segments, 0, value);

    public static Dictionary<string, Value> Delete(
        IReadOnlyDictionary<string, Value> fields, FieldPath path) =>
        DeleteSegments(fields, path.Segments, 0);

    private static Dictionary<string, Value> SetSegments(
        IReadOnlyDictionary<string, Value> fields, IReadOnlyList<string> segments, int i, Value value)
    {
        var result = fields.ToDictionary(kv => kv.Key, kv => kv.Value);
        var seg = segments[i];

        if (i == segments.Count - 1)
        {
            result[seg] = value;
            return result;
        }

        var inner = result.TryGetValue(seg, out var existing) && existing is MapValue m
            ? m.Fields
            : new Dictionary<string, Value>();                  // create or replace non-map intermediate

        result[seg] = new MapValue(SetSegments(inner, segments, i + 1, value));
        return result;
    }

    private static Dictionary<string, Value> DeleteSegments(
        IReadOnlyDictionary<string, Value> fields, IReadOnlyList<string> segments, int i)
    {
        var result = fields.ToDictionary(kv => kv.Key, kv => kv.Value);
        var seg = segments[i];

        if (i == segments.Count - 1)
        {
            result.Remove(seg);
            return result;
        }

        if (result.TryGetValue(seg, out var existing) && existing is MapValue m)
            result[seg] = new MapValue(DeleteSegments(m.Fields, segments, i + 1));

        return result;                                          // non-map / missing intermediate → no-op
    }
}
