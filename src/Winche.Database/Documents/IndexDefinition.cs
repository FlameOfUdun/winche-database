using Winche.Database.Querying.Ast;

namespace Winche.Database.Documents;

public sealed record IndexField(string Path, SortDirection Direction = SortDirection.Asc);

/// <summary>
/// A composite expression index over the winche_* family for one collection ID.
/// Field paths become DDL literals — segments must match ^[A-Za-z0-9_\-]{1,128}$.
/// </summary>
/// <param name="CollectionId">
/// The collection ID the index applies to: the last segment of any collection path
/// (e.g. "sessionHistory" covers "userData/alice/sessionHistory", "userData/bob/sessionHistory",
/// and the top-level "sessionHistory"). Must match ^[A-Za-z0-9_-]+$ (no '/' or '*').
/// See <see cref="DocumentPathParser.IsValidCollectionId"/>.
/// </param>
/// <param name="Fields">Ordered list of fields to index.</param>
/// <param name="Name">
/// Optional explicit index name. When <see langword="null"/> (the default) the name is derived
/// from <paramref name="CollectionId"/> and <paramref name="Fields"/>.
/// </param>
/// <param name="Where">
/// Optional predicate for a filtered index (spec D). Must be expressible as a restricted
/// literal SQL fragment via <see cref="Querying.Sql.IndexPredicateSql"/>.
/// <see langword="null"/> means no predicate (full-collection index).
/// </param>
public sealed record IndexDefinition(
    string CollectionId,
    IReadOnlyList<IndexField> Fields,
    string? Name = null,
    Filter? Where = null);
