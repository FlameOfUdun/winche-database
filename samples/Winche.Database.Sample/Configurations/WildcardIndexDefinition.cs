using Winche.Database.Documents;
using Winche.Database.Querying.Ast;

namespace Winche.Database.Sample.Configurations;

public class WildcardIndexDefinition : IndexDefinition
{
    public override string Path => "users";

    public override IReadOnlyList<IndexField> Fields =>
    [
        new IndexField("name"),
        new IndexField("age", SortDirection.Desc),
    ];
}
