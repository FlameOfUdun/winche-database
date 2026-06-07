using Winche.Database.Core.Infrastructure;
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
                foreach (var (key, value) in s.Fields)
                {
                    if (value is DeleteFieldValue && !s.Merge)
                        throw Invalid($"deleteField ('{key}') requires SetWrite(Merge: true).");
                    if (value is not DeleteFieldValue)
                        RejectNestedSentinel(value, key);
                }
                ValidateTransforms(s.Transforms);
                break;

            case UpdateWrite u:
                if (u.Fields.Count == 0 && (u.Transforms is null || u.Transforms.Count == 0))
                    throw Invalid("UpdateWrite needs at least one field or transform.");
                foreach (var (path, value) in u.Fields)
                    if (value is not DeleteFieldValue)
                        RejectNestedSentinel(value, path.ToString());
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
                    RejectNestedSentinel(arr, t.Field.ToString());
                    break;
            }
        }
    }

    private static void RejectNestedSentinel(Value value, string where)
    {
        switch (value)
        {
            case DeleteFieldValue:
                throw Invalid($"deleteField is not allowed nested inside '{where}'.");
            case ArrayValue a:
                foreach (var e in a.Values) RejectNestedSentinel(e, where);
                break;
            case MapValue m:
                foreach (var v in m.Fields.Values) RejectNestedSentinel(v, where);
                break;
        }
    }

    private static RuntimeException Invalid(string message) =>
        new(RuntimeStatus.InvalidArgument, message);
}
