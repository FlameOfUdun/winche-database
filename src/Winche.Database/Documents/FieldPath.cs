namespace Winche.Database.Documents;

/// <summary>A dotted field path ("address.city"). The only type that knows path syntax.</summary>
public sealed record FieldPath
{
    public IReadOnlyList<string> Segments { get; }

    private FieldPath(IReadOnlyList<string> segments) => Segments = segments;

    public static FieldPath Parse(string dotted)
    {
        if (string.IsNullOrEmpty(dotted))
            throw new ArgumentException("Field path cannot be empty.", nameof(dotted));

        var segments = dotted.Split('.');
        if (segments.Any(string.IsNullOrEmpty))
            throw new ArgumentException($"Field path '{dotted}' contains an empty segment.", nameof(dotted));

        return new FieldPath(segments);
    }

    public override string ToString() => string.Join('.', Segments);

    public bool Equals(FieldPath? other) =>
        other is not null && Segments.SequenceEqual(other.Segments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var s in Segments) hash.Add(s);
        return hash.ToHashCode();
    }
}
