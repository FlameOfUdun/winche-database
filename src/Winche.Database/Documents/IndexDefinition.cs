using Winche.Database.Querying.Ast;

namespace Winche.Database.Documents;

public sealed record IndexField(string Path, SortDirection Direction = SortDirection.Asc);

/// <summary>
/// A composite expression index over the winche_* family for one collection.
/// Field paths become DDL literals — segments must match ^[A-Za-z0-9_\-]{1,128}$.
/// </summary>
public abstract class IndexDefinition
{
    public abstract string Collection { get; }
    public abstract IReadOnlyList<IndexField> Fields { get; }
    public virtual string? Name => null;
}
