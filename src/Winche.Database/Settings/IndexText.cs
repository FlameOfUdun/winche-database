using System.Text;

namespace Winche.Database.Settings;

/// <summary>Where an index file is wrong; <see cref="Line"/>/<see cref="Column"/> are 1-based.</summary>
public sealed record IndexTextError(string Message, int Line, int Column, int Offset);

/// <summary>A parsed index file: the indexes and the expiry rules it declares.</summary>
public sealed record IndexTextResult(IReadOnlyList<IndexSettings> Indexes, IReadOnlyList<TtlSettings> Ttl);

/// <summary>
/// The database's indexes and expiry rules as text, the way rules are written:
/// <code>
/// // Sessions: by user, newest first; gone once they expire.
/// collection sessions {
///   index (userId, startedAt desc)
///   expire when expiresAt
/// }
///
/// // Every "notes" collection, wherever it is.
/// collection group notes {
///   index (tags contains)
/// }
/// </code>
/// A field is a dotted path (<c>address.city</c>; a segment with other characters in backticks). Fields are ascending
/// unless followed by <c>desc</c>; <c>contains</c> makes an array index, on that one field. A collection may appear in
/// several blocks; its indexes add up. Expiry is per collection: not in a group block, once per collection.
/// </summary>
public static class IndexText
{
    private enum Kind { Word, Symbol, Path, End }

    private sealed record Token(Kind Kind, string Text, int Offset);

    private sealed class IndexTextException(string message, int offset) : Exception(message)
    {
        public int Offset { get; } = offset;
    }

    /// <summary>Parses <paramref name="text"/>; the result, or the first error.</summary>
    public static (IndexTextResult? Result, IndexTextError? Error) Parse(string text)
    {
        try
        {
            return (new Parser(Tokenize(text)).File(), null);
        }
        catch (IndexTextException ex)
        {
            var (line, column) = Position(text, ex.Offset);
            return (null, new IndexTextError(ex.Message, line, column, ex.Offset));
        }
    }

    private static (int Line, int Column) Position(string text, int offset)
    {
        int line = 1, column = 1;
        for (var i = 0; i < Math.Min(offset, text.Length); i++)
        {
            if (text[i] == '\n') { line++; column = 1; } else column++;
        }
        return (line, column);
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c is '{' or '}' or '(' or ')' or ',') { tokens.Add(new(Kind.Symbol, c.ToString(), i)); i++; continue; }
            if (char.IsLetter(c) || c == '_' || c == '`')
            {
                // A word, or a dotted path whose segments may be backtick-quoted.
                var start = i;
                var segments = new List<string>();
                while (true)
                {
                    if (i < text.Length && text[i] == '`')
                    {
                        var close = text.IndexOf('`', i + 1);
                        if (close < 0) throw new IndexTextException("A backtick-quoted name isn't closed.", i);
                        if (close == i + 1) throw new IndexTextException("A quoted name can't be empty.", i);
                        segments.Add(text[(i + 1)..close]);
                        i = close + 1;
                    }
                    else if (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    {
                        var s = i;
                        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '-')) i++;
                        segments.Add(text[s..i]);
                    }
                    else throw new IndexTextException("A name is missing after the dot.", i);
                    if (i < text.Length && text[i] == '.') { i++; continue; }
                    break;
                }
                var raw = text[start..i];
                tokens.Add(segments.Count == 1 && !raw.StartsWith('`') ? new(Kind.Word, raw, start) : new(Kind.Path, Join(segments), start));
                continue;
            }
            throw new IndexTextException(c == '/'
                ? "A collection is named by its id alone (sessions), not a path."
                : $"'{c}' isn't expected here.", i);
        }
        tokens.Add(new(Kind.End, "", text.Length));
        return tokens;
    }

    // Paths are kept as their segments joined with dots; a segment with other characters is backticked.
    private static string Join(IEnumerable<string> segments) => string.Join('.', segments.Select(Quote));

    private static string Quote(string segment) =>
        segment.Length > 0 && (char.IsLetter(segment[0]) || segment[0] == '_') && segment.All(ch => char.IsLetterOrDigit(ch) || ch == '_')
            ? segment : $"`{segment}`";

    private sealed class Parser(List<Token> tokens)
    {
        private int _at;
        private Token Peek => tokens[_at];
        private Token Next() => tokens[_at++];

        private Token Expect(Kind kind, string? text, string what)
        {
            var t = Peek;
            if (t.Kind != kind || (text is not null && t.Text != text))
                throw new IndexTextException(t.Kind == Kind.End ? $"The file ends where {what} was expected." : $"Expected {what}, found '{t.Text}'.", t.Offset);
            return Next();
        }

        private bool IsWord(string word) => Peek.Kind == Kind.Word && Peek.Text == word;

        public IndexTextResult File()
        {
            var indexes = new List<IndexSettings>();
            var ttl = new List<TtlSettings>();
            var expiryAt = new Dictionary<string, int>(StringComparer.Ordinal);
            while (Peek.Kind != Kind.End)
            {
                Expect(Kind.Word, "collection", "'collection'");
                var group = false;
                if (IsWord("group") && tokens[_at + 1].Kind is Kind.Word or Kind.Path) { Next(); group = true; }
                var name = Peek;
                if (name.Kind is not (Kind.Word or Kind.Path)) throw new IndexTextException("Expected the collection's id.", name.Offset);
                Next();
                if (name.Text.Contains('/') || (name.Kind == Kind.Path && name.Text.Contains('.')))
                    throw new IndexTextException("A collection is named by its id alone (sessions), not a path.", name.Offset);
                var collection = name.Text.Trim('`');
                Expect(Kind.Symbol, "{", "'{'");
                while (!(Peek.Kind == Kind.Symbol && Peek.Text == "}"))
                {
                    var statement = Peek;
                    if (IsWord("index"))
                    {
                        Next();
                        indexes.Add(Index(collection, group));
                    }
                    else if (IsWord("expire"))
                    {
                        Next();
                        if (group) throw new IndexTextException("Expiry is per collection: put it in a 'collection' block, not a group.", statement.Offset);
                        Expect(Kind.Word, "when", "'when' (expire when <timestamp field>)");
                        var field = Peek;
                        if (field.Kind is not (Kind.Word or Kind.Path)) throw new IndexTextException("Expected the timestamp field.", field.Offset);
                        Next();
                        if (expiryAt.ContainsKey(collection))
                            throw new IndexTextException($"{collection} already expires: one expiry per collection.", statement.Offset);
                        expiryAt[collection] = statement.Offset;
                        ttl.Add(new TtlSettings { Collection = collection, Field = field.Text });
                    }
                    else throw new IndexTextException(Peek.Kind == Kind.End ? "The block isn't closed: '}' is missing." : $"Expected 'index' or 'expire', found '{Peek.Text}'.", Peek.Offset);
                }
                Next();
            }
            // The same index twice is a mistake (the database would build it once).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in indexes)
                if (!seen.Add($"{i.Collection}|{i.CollectionGroup}|{i.Kind}|{string.Join(",", i.Fields.Select(f => f.Path + " " + f.Direction))}"))
                    throw new IndexTextException($"An index on {i.Collection} ({string.Join(", ", i.Fields.Select(f => f.Path))}) is listed twice.", 0);
            return new(indexes, ttl);
        }

        private IndexSettings Index(string collection, bool group)
        {
            Expect(Kind.Symbol, "(", "'(' and the index's fields");
            var fields = new List<IndexFieldSettings>();
            var contains = false;
            int? containsAt = null;
            while (true)
            {
                var field = Peek;
                if (field.Kind is not (Kind.Word or Kind.Path)) throw new IndexTextException("Expected a field.", field.Offset);
                Next();
                var direction = "asc";
                if (IsWord("asc") || IsWord("desc")) direction = Next().Text;
                else if (IsWord("contains")) { containsAt = Next().Offset; contains = true; }
                fields.Add(new IndexFieldSettings { Path = field.Text, Direction = direction });
                if (Peek.Kind == Kind.Symbol && Peek.Text == ",") { Next(); continue; }
                Expect(Kind.Symbol, ")", "',' or ')'");
                break;
            }
            if (contains && fields.Count > 1)
                throw new IndexTextException("An array index ('contains') is on one field alone.", containsAt ?? 0);
            return new IndexSettings { Collection = collection, Fields = fields, Kind = contains ? "array" : "ordered", CollectionGroup = group };
        }
    }

    /// <summary>
    /// The text for <paramref name="indexes"/> and <paramref name="ttl"/>: one block per collection (then per group), in
    /// the order they first appear, expiry last in its block.
    /// </summary>
    public static string Format(IReadOnlyList<IndexSettings> indexes, IReadOnlyList<TtlSettings> ttl)
    {
        var blocks = new List<(string Collection, bool Group)>();
        void Add(string collection, bool group) { if (!blocks.Contains((collection, group))) blocks.Add((collection, group)); }
        foreach (var i in indexes) Add(i.Collection, i.CollectionGroup);
        foreach (var t in ttl) Add(t.Collection, false);

        var b = new StringBuilder();
        foreach (var (collection, group) in blocks)
        {
            if (b.Length > 0) b.Append('\n');
            b.Append("collection ").Append(group ? "group " : "").Append(Quote(collection)).Append(" {\n");
            foreach (var i in indexes.Where(i => i.Collection == collection && i.CollectionGroup == group))
            {
                b.Append("  index (");
                b.Append(i.Kind == "array"
                    ? $"{i.Fields.FirstOrDefault()?.Path} contains"
                    : string.Join(", ", i.Fields.Select(f => f.Direction == "desc" ? $"{f.Path} desc" : f.Path)));
                b.Append(")\n");
            }
            if (!group)
                foreach (var t in ttl.Where(t => t.Collection == collection))
                    b.Append("  expire when ").Append(t.Field).Append('\n');
            b.Append("}\n");
        }
        return b.ToString();
    }
}
