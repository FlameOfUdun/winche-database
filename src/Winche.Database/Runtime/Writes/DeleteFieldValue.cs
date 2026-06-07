using Winche.Database.Values;

namespace Winche.Database.Runtime.Writes;

/// <summary>
/// The deleteField sentinel. Legal ONLY as the immediate value of a field entry in
/// UpdateWrite.Fields or SetWrite(Merge:true).Fields — WriteValidator enforces this,
/// and Rank throws so an escaped sentinel can never be ordered or stored.
/// </summary>
public sealed record DeleteFieldValue : Value
{
    public static readonly DeleteFieldValue Instance = new();
    private DeleteFieldValue() { }
    public override TypeRank Rank =>
        throw new InvalidOperationException("DeleteField sentinel cannot be stored or compared.");
}
