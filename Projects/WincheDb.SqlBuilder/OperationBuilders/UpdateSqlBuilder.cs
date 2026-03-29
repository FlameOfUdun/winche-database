using System.Text.Json.Nodes;
using WincheDb.SqlBuilder.Infrastructure;

namespace WincheDb.SqlBuilder.OperationBuilders
{
    public sealed class UpdateSqlBuilder(string table = "documents")
    {
        public SqlBuildResult Build(string path, JsonObject data)
        {
            var bag = new ParameterBag();
            bag.Add(path);
            bag.Add(data.ToJsonString());

            var sql = $"""
                UPDATE {table}
                SET data = $2::jsonb, updated_at = NOW(), version = version + 1
                WHERE path = $1
                RETURNING path, id, collection, data, created_at, updated_at, version
                """;

            return new SqlBuildResult(sql, bag.ToArray());
        }
    }
}
