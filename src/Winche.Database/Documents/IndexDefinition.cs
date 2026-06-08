using Winche.Database.Querying.Ast;

namespace Winche.Database.Documents;

public sealed record IndexField(string Path, SortDirection Direction = SortDirection.Asc);

/// <summary>
/// A composite expression index over the winche_* family for one collection.
/// Field paths become DDL literals — segments must match ^[A-Za-z0-9_\-]{1,128}$.
/// </summary>
public abstract class IndexDefinition
{
    /// <summary>
    /// Collection path the index applies to: an exact path ("users",
    /// "userData/alice/sessionHistory") or a wildcard pattern with '*' at document-id positions
    /// ("userData/*/sessionHistory"). See <see cref="DocumentPathParser.IsValidIndexPath"/>.
    /// </summary>
    public abstract string Path { get; }
    public abstract IReadOnlyList<IndexField> Fields { get; }
    public virtual string? Name => null;

    /// <summary>
    /// Optional predicate for a filtered index (spec D). Must be expressible as a restricted
    /// literal SQL fragment via <see cref="Querying.Sql.IndexPredicateSql"/>.
    /// Null means no predicate (full-collection index).
    /// </summary>
    public virtual Filter? Where => null;
}
