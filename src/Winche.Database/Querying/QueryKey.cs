using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Ast.Serialization;

namespace Winche.Database.Querying;

/// <summary>Deterministic grouping key for subscriptions: canonical wire JSON of the query.</summary>
public static class QueryKey
{
    public static string Compute(Query query) => QueryAstWriter.Write(query).ToJsonString();
}
