using WincheDatabase.AST.Models;

namespace WincheDatabase.Sample.Configurations;

public sealed class WildcardIndexDefinition : IndexDefinition
{
    public override string Collection => "orders/{region}";

    public override List<SortNode> Fields => 
    [
        new SortNode("finishedAt", SortDirection.Desc, FieldType.Timestamp),
    ];
}
