using WincheDb.SqlBuilder.Infrastructure;

namespace WincheDb.SqlBuilder
{
    public sealed class DeleteSqlBuilder(string table = "documents")
    {
        public SqlBuildResult Build(string path)
        {
            var bag = new ParameterBag();
            bag.Add(path);

            var sql = $"""
                DELETE FROM {table}
                WHERE path = $1
                RETURNING path
                """;

            return new SqlBuildResult(sql, bag.ToArray());
        }
    }
}
