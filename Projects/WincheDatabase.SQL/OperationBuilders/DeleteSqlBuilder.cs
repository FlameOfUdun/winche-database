using WincheDatabase.SQL.Infrastructure;

namespace WincheDatabase.SQL.OperationBuilders
{
    public sealed class DeleteSqlBuilder(string table = "documents")
    {
        public SqlBuildResult Build(string path)
        {
            var bag = new ParameterBag();
            var pathParam = bag.Add(path);
            var prefixParam = bag.Add(LikePatternEscaper.Escape(path) + "/%");

            var sql = $"""
                DELETE FROM {table}
                WHERE path = {pathParam} OR path LIKE {prefixParam} ESCAPE '\'
                RETURNING path
                """;

            return new SqlBuildResult(sql, bag.ToArray());
        }

        public SqlBuildResult BuildSelectForUpdate(string path)
        {
            var bag = new ParameterBag();
            var pathParam = bag.Add(path);
            var prefixParam = bag.Add(LikePatternEscaper.Escape(path) + "/%");

            var sql = $"""
                SELECT path FROM {table}
                WHERE path = {pathParam} OR path LIKE {prefixParam} ESCAPE '\'
                FOR UPDATE
                """;

            return new SqlBuildResult(sql, bag.ToArray());
        }
    }
}
