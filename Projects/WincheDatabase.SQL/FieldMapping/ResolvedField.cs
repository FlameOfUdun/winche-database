using WincheDatabase.AST.Models;

namespace WincheDatabase.SQL.FieldMapping
{
    public sealed class ResolvedField
    {
        public string Path { get; }
        public bool IsColumn { get; }
        public FieldType Cast { get; }
        public string? Alias { get; }

        public bool IsJsonb => !IsColumn;

        internal ResolvedField(string path, bool isColumn, FieldType cast, string? alias)
        {
            Path = path;
            IsColumn = isColumn;
            Cast = cast;
            Alias = alias;
        }

        public ResolvedField WithAlias(string alias) => new(Path, IsColumn, Cast, alias);
        public ResolvedField WithCast(FieldType cast) => new(Path, IsColumn, cast, Alias);
        public ResolvedField WithCast<T>() => WithCast(TypeInference.For<T>());
        public ResolvedField WithCast(Type t) => WithCast(TypeInference.For(t));
    }
}
