using Winche.Database.Querying.Ast;

namespace Winche.Database.Documents;

public sealed record IndexField(string Path, SortDirection Direction = SortDirection.Asc);

/// <summary>
/// A composite expression index over the winche_* family for one collection.
/// Field paths become DDL literals — segments must match ^[A-Za-z0-9_\-]{1,128}$.
/// </summary>
/// <param name="Path">
/// Collection path the index applies to: an exact path ("users",
/// "userData/alice/sessionHistory") or a wildcard pattern with '*' at document-id positions
/// ("userData/*/sessionHistory"). See <see cref="DocumentPathParser.IsValidIndexPath"/>.
/// </param>
/// <param name="Fields">Ordered list of fields to index.</param>
/// <param name="Name">
/// Optional explicit index name. When <see langword="null"/> (the default) the name is derived
/// from <paramref name="Path"/> and <paramref name="Fields"/>.
/// </param>
/// <param name="Where">
/// Optional predicate for a filtered index (spec D). Must be expressible as a restricted
/// literal SQL fragment via <see cref="Querying.Sql.IndexPredicateSql"/>.
/// <see langword="null"/> means no predicate (full-collection index).
/// </param>
public sealed record IndexDefinition(
    string Path,
    IReadOnlyList<IndexField> Fields,
    string? Name = null,
    Filter? Where = null);
