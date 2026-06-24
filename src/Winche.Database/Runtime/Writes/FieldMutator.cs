using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Runtime.Writes;

/// <summary>
/// Immutable dotted-path mutations over typed field maps (dotted-path update semantics):
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

    /// <summary>
    /// Deletes a field by LITERAL segments (not dotted-parsed FieldPath). Use this when
    /// the segments are map keys that may themselves contain dots (merge-set nested sentinels).
    /// </summary>
    public static Dictionary<string, Value> Delete(
        IReadOnlyDictionary<string, Value> fields, IReadOnlyList<string> segments) =>
        DeleteSegments(fields, segments, 0);

    /// <summary>
    /// Reads the value at a dotted path, descending nested maps per <see cref="FieldPath.Segments"/>.
    /// Returns false if any segment is absent or a non-map is encountered mid-path. Read-side
    /// analogue of <see cref="Set"/>.
    /// </summary>
    public static bool TryGet(IReadOnlyDictionary<string, Value> fields, FieldPath path, out Value value)
    {
        var segments = path.Segments;
        IReadOnlyDictionary<string, Value> current = fields;
        for (var i = 0; i < segments.Count; i++)        // iterative: a pure read needs no allocation
        {
            if (!current.TryGetValue(segments[i], out var v))
                break;
            if (i == segments.Count - 1)
            {
                value = v;
                return true;
            }
            if (v is not MapValue m)
                break;
            current = m.Fields;
        }
        value = null!; // not read by callers unless the method returned true
        return false;
    }

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
