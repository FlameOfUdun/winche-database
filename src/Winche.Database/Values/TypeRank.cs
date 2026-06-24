namespace Winche.Database.Values;

/// <summary>
/// Canonical cross-type total order. Gaps left for future types.
/// This enum is THE source of truth — the SQL helper functions
/// (Querying/Sql/SchemaSql.cs) must emit these exact numbers.
/// </summary>
public enum TypeRank : short
{
    Null = 10,
    Boolean = 20,
    NaN = 29,        // NaN sorts before all numbers
    Number = 30,     // IntegerValue and DoubleValue compare together
    Timestamp = 40,
    String = 50,
    Bytes = 60,
    Reference = 70,
    GeoPoint = 80,
    Array = 90,
    Map = 100,
}
