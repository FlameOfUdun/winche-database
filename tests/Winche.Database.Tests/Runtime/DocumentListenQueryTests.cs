using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Listening;
using Winche.Database.Values;

namespace Winche.Database.Tests.Runtime;

public class DocumentListenQueryTests
{
    [Fact]
    public void For_NestedPath_BuildsNameEqualsFilter_OverParentCollection()
    {
        var query = DocumentListenQuery.For("a/b/c/d");

        Assert.Equal("a/b/c", query.Collection);
        var filter = Assert.IsType<FieldFilter>(query.Where);
        Assert.Equal(FilterOperator.Eq, filter.Op);
        Assert.Equal(FieldPath.Parse("__name__"), filter.Field);
        Assert.Equal(new ReferenceValue("a/b/c/d"), filter.Operand);
    }

    [Fact]
    public void For_CollectionPath_Throws_InvalidArgument()
    {
        var ex = Assert.Throws<RuntimeException>(() => DocumentListenQuery.For("c")); // even slash count
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);
    }

    [Fact]
    public void For_EmptyPath_Throws_InvalidArgument()
    {
        var ex = Assert.Throws<RuntimeException>(() => DocumentListenQuery.For(""));
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);
    }
}
