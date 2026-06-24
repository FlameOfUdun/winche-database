using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Runtime.Writes;

/// <summary>
/// Factory for field transforms and the delete sentinel. The transform helpers
/// return <see cref="FieldTransform"/> (place in SetWrite/UpdateWrite.Transforms); <see cref="Delete"/>
/// returns the <see cref="DeleteFieldValue"/> sentinel (place directly in the Fields map).
/// </summary>
public static class FieldValue
{
    public static FieldTransform ServerTimestamp(string field) =>
        new(FieldPath.Parse(field), TransformKind.ServerTimestamp);

    public static FieldTransform Increment(string field, long by) =>
        new(FieldPath.Parse(field), TransformKind.Increment, new IntegerValue(by));

    public static FieldTransform Increment(string field, double by) =>
        new(FieldPath.Parse(field), TransformKind.Increment, new DoubleValue(by));

    public static FieldTransform Maximum(string field, long value) =>
        new(FieldPath.Parse(field), TransformKind.Maximum, new IntegerValue(value));

    public static FieldTransform Maximum(string field, double value) =>
        new(FieldPath.Parse(field), TransformKind.Maximum, new DoubleValue(value));

    public static FieldTransform Minimum(string field, long value) =>
        new(FieldPath.Parse(field), TransformKind.Minimum, new IntegerValue(value));

    public static FieldTransform Minimum(string field, double value) =>
        new(FieldPath.Parse(field), TransformKind.Minimum, new DoubleValue(value));

    public static FieldTransform ArrayUnion(string field, params Value[] elements) =>
        new(FieldPath.Parse(field), TransformKind.ArrayUnion, new ArrayValue(elements));

    public static FieldTransform ArrayRemove(string field, params Value[] elements) =>
        new(FieldPath.Parse(field), TransformKind.ArrayRemove, new ArrayValue(elements));

    public static DeleteFieldValue Delete() => DeleteFieldValue.Instance;
}
