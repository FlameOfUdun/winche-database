using WincheDb.Core.Ast;

namespace WincheDb.SqlBuilder.FieldMapping
{
    /// <summary>
    /// Maps field path strings to ResolvedField instances.
    /// Metadata columns are resolved directly; all other paths are treated as JSONB data fields.
    /// </summary>
    public static class FieldResolver
    {
        private static readonly Dictionary<string, (string Column, FieldType Cast)> Columns =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ("id", FieldType.Text),
                ["path"] = ("path", FieldType.Text),
                ["collection"] = ("collection", FieldType.Text),
                ["created_at"] = ("created_at", FieldType.Timestamp),
                ["updated_at"] = ("updated_at", FieldType.Timestamp),
                ["version"] = ("version", FieldType.BigInt),
            };

        /// <summary>Resolve by path. Metadata columns are typed automatically; JSONB fields default to Text.</summary>
        public static ResolvedField Resolve(string path, string? alias = null)
        {
            if (Columns.TryGetValue(path, out var meta))
                return new ResolvedField(meta.Column, isColumn: true, meta.Cast, alias);

            return new ResolvedField(path, isColumn: false, FieldType.Text, alias);
        }

        /// <summary>Resolve with an explicit cast override.</summary>
        public static ResolvedField Resolve(string path, FieldType cast, string? alias = null) =>
            Resolve(path, alias).WithCast(cast);

        /// <summary>Resolve inferring cast from a generic CLR type.</summary>
        public static ResolvedField Resolve<T>(string path, string? alias = null) =>
            Resolve(path, alias).WithCast<T>();

        /// <summary>Resolve inferring cast from a runtime CLR type.</summary>
        public static ResolvedField Resolve(string path, Type type, string? alias = null) =>
            Resolve(path, alias).WithCast(type);
    }
}
