using Winche.Database.Querying.Planning;

namespace Winche.Database.Querying.Sql;

/// <summary>Which documents a query reads: one collection by path, or a collection group by id.</summary>
internal static class ScanSql
{
    /// <summary>
    /// The WHERE condition for <paramref name="scan"/>. A collection group reads every collection with that id
    /// (<c>collection_id = id</c>), which is also what a collection-id partial index needs; a collection reads
    /// its path, plus <c>collection_id</c> when <paramref name="collectionIdScope"/> says an index covers it.
    /// </summary>
    public static string Where(CollectionScan scan, string? collectionIdScope, ParameterBag bag, string alias) =>
        scan.Group
            ? $"{alias}.collection_id = {bag.Add(scan.Collection)}"
            : collectionIdScope is null
                ? $"{alias}.collection_path = {bag.Add(scan.Collection)}"
                : $"{alias}.collection_path = {bag.Add(scan.Collection)} AND {alias}.collection_id = {bag.Add(collectionIdScope)}";
}
