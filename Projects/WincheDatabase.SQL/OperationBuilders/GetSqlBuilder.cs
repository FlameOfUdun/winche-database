using WincheDatabase.SQL.Infrastructure;

namespace WincheDatabase.SQL.OperationBuilders
{
    public sealed class GetSqlBuilder(string table = "documents")
    {
        public SqlBuildResult Build(string path)
        {
            var bag = new ParameterBag();
            bag.Add(path);

            var sql = $"""
                SELECT path, id, collection, data, created_at, updated_at, version
                FROM {table}
                WHERE path = $1
                """;

            return new SqlBuildResult(sql, bag.ToArray());
        }
    }
}
