// src/Winche.Database/Querying/Sql/StageSchema.cs
namespace Winche.Database.Querying.Sql;

internal enum ColumnShape
{
    TaggedValue,   // jsonb tagged value ({"integerValue":"5"}, …) or SQL NULL (missing)
}

internal abstract record StageSchema;

/// <summary>Document rows: metadata + tagged `data`, plus extra tagged columns added by lookup/unwind.</summary>
internal sealed record DocumentSchema(IReadOnlyDictionary<string, ColumnShape> Extra) : StageSchema
{
    public static readonly DocumentSchema Plain = new(new Dictionary<string, ColumnShape>());

    public DocumentSchema With(string name) =>
        new(new Dictionary<string, ColumnShape>(Extra) { [name] = ColumnShape.TaggedValue });
}

/// <summary>Projected rows: only named tagged columns remain (post Group/Project).</summary>
internal sealed record RowSchema(IReadOnlyDictionary<string, ColumnShape> Columns) : StageSchema
{
    public RowSchema With(string name) =>
        new(new Dictionary<string, ColumnShape>(Columns) { [name] = ColumnShape.TaggedValue });
}
