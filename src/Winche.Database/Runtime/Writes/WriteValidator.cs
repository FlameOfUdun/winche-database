using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Runtime.Writes;

/// <summary>Shape validation for a write batch. All violations are INVALID_ARGUMENT.</summary>
public static class WriteValidator
{
    public const int MaxBatchSize = 500;

    public static void Validate(IReadOnlyList<Write> writes)
    {
        if (writes.Count == 0)
            throw Invalid("A write batch must contain at least one write.");
        if (writes.Count > MaxBatchSize)
            throw Invalid($"A write batch may contain at most {MaxBatchSize} writes, got {writes.Count}.");

        foreach (var write in writes)
            ValidateWrite(write);
    }

    private static void ValidateWrite(Write write)
    {
        var isCascade = write is DeleteWrite { Cascade: true };
        if (isCascade)
        {
            if (!DocumentPathParser.IsValidPath(write.Path, out var pathErr))
                throw Invalid(pathErr!);
        }
        else if (!DocumentPathParser.IsValidDocumentPath(write.Path, out var docErr))
        {
            throw Invalid($"'{write.Path}': {docErr}");
        }

        if (write.Precondition is { Exists: null, UpdateTime: null })
            throw Invalid("A precondition must set Exists and/or UpdateTime.");

        switch (write)
        {
            case SetWrite s:
                if (s.MergeFields is not null && s.Merge)
                    throw Invalid("'mergeFields' cannot be combined with merge:true.");
                if (s.MergeFields is { Count: 0 })
                    throw Invalid("'mergeFields' must contain at least one field path.");
                // Either merge mode permits a top-level deleteField and nested sentinels in maps.
                var isMerge = s.Merge || s.MergeFields is not null;
                foreach (var (key, value) in s.Fields)
                {
                    if (value is DeleteFieldValue && !isMerge)
                        throw Invalid($"deleteField ('{key}') requires SetWrite(Merge: true) or mergeFields.");
                    if (value is not DeleteFieldValue)
                        RejectIllegalSentinels(value, key, allowInMaps: isMerge);
                }
                ValidateTransforms(s.Transforms);
                break;

            case UpdateWrite u:
                if (u.Fields.Count == 0 && (u.Transforms is null || u.Transforms.Count == 0))
                    throw Invalid("UpdateWrite needs at least one field or transform.");
                foreach (var (path, value) in u.Fields)
                    if (value is not DeleteFieldValue)
                        RejectIllegalSentinels(value, path.ToString(), allowInMaps: false);
                ValidateTransforms(u.Transforms);
                break;
        }
    }

    private static void ValidateTransforms(IReadOnlyList<FieldTransform>? transforms)
    {
        if (transforms is null) return;

        var seen = new HashSet<FieldPath>();
        foreach (var t in transforms)
        {
            if (!seen.Add(t.Field))
                throw Invalid($"Multiple transforms target field '{t.Field}'.");

            switch (t.Kind)
            {
                case TransformKind.ServerTimestamp:
                    if (t.Operand is not null)
                        throw Invalid("serverTimestamp takes no operand.");
                    break;
                case TransformKind.Increment or TransformKind.Maximum or TransformKind.Minimum:
                    if (t.Operand is not (IntegerValue or DoubleValue))
                        throw Invalid($"{t.Kind} requires a numeric operand.");
                    break;
                case TransformKind.ArrayUnion or TransformKind.ArrayRemove:
                    if (t.Operand is not ArrayValue arr)
                        throw Invalid($"{t.Kind} requires an array operand.");
                    RejectIllegalSentinels(arr, t.Field.ToString(), allowInMaps: false);
                    break;
            }
        }
    }

    /// <summary>
    /// Validates nested sentinels in a value:
    /// - allowInMaps=true (merge-set): sentinel directly or under MapValue chains is OK;
    ///   sentinel inside any ArrayValue (at any depth) is rejected.
    /// - allowInMaps=false (non-merge set, update nested values): any nested sentinel is invalid.
    /// </summary>
    private static void RejectIllegalSentinels(Value value, string where, bool allowInMaps)
    {
        switch (value)
        {
            case DeleteFieldValue:
                if (!allowInMaps)
                    throw Invalid($"deleteField is not allowed nested inside '{where}'.");
                break;
            case ArrayValue a:
                // Arrays always reject sentinels, even in merge-sets
                foreach (var e in a.Values) RejectIllegalSentinels(e, where, allowInMaps: false);
                break;
            case MapValue m:
                foreach (var v in m.Fields.Values) RejectIllegalSentinels(v, where, allowInMaps);
                break;
        }
    }

    private static RuntimeException Invalid(string message) =>
        new(RuntimeStatus.InvalidArgument, message);
}
