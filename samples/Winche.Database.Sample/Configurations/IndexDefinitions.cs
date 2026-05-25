using Winche.Database.AST.Models;

namespace Winche.Database.Sample.Configurations;

public sealed class WildcardIndexDefinition : IndexDefinition
{
    public override string Collection => "orders/{region}";

    public override List<SortNode> Fields => 
    [
        new SortNode("finishedAt", SortDirection.Desc, FieldType.Timestamp),
    ];
}
