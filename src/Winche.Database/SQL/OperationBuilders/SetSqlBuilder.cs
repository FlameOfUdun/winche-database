using System.Text.Json.Nodes;
using Winche.Database.SQL.Infrastructure;

namespace Winche.Database.SQL.OperationBuilders
{
    public sealed class SetSqlBuilder(string table = "documents")
    {
        public SqlBuildResult Build(string id, string collection, JsonObject data)
        {
            var bag = new ParameterBag();
            bag.Add($"{collection}/{id}");
            bag.Add(id);
            bag.Add(collection);
            bag.Add(data.ToJsonString());

            var sql = $"""
                INSERT INTO {table} (path, id, collection, data, created_at, updated_at, version)
                VALUES ($1, $2, $3, $4::jsonb, NOW(), NOW(), 1)
                ON CONFLICT (path) DO UPDATE SET data = EXCLUDED.data, updated_at = NOW(), version = {table}.version + 1
                RETURNING path, id, collection, data, created_at, updated_at, version
                """;

            return new SqlBuildResult(sql, bag.ToArray());
        }
    }
}
