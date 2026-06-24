using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Runtime.Listening;

/// <summary>Builds the single-document listen query: a <c>__name__ == path</c> filter over the parent collection.</summary>
public static class DocumentListenQuery
{
    public static Query For(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new RuntimeException(RuntimeStatus.InvalidArgument, "Path cannot be null or empty.");
        if (!DocumentPathParser.IsValidDocumentPath(path, out var error))
            throw new RuntimeException(RuntimeStatus.InvalidArgument, error!);
        var collection = path[..path.LastIndexOf('/')];
        return new Query(collection,
            Where: new FieldFilter(FieldPath.Parse("__name__"), FilterOperator.Eq, new ReferenceValue(path)));
    }
}
