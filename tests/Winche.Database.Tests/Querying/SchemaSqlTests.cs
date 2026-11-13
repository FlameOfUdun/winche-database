using Winche.Database.Querying.Sql;
using Winche.Values;

namespace Winche.Database.Tests.Querying;

public class SchemaSqlTests
{
    [Fact]
    public void WincheRank_EmitsEveryTypeRankValue()
    {
        var sql = SchemaSql.HelperFunctions();
        foreach (var rank in Enum.GetValues<TypeRank>())
            Assert.Contains($"THEN {(short)rank}", sql);
    }
}
