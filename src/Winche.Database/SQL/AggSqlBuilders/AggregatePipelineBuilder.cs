using Winche.Database.AST.Models;
using Winche.Database.SQL.Infrastructure;

namespace Winche.Database.SQL.AggSqlBuilders;

public static class AggregatePipelineBuilder
{
    public static SqlBuildResult Build(List<PipelineStage> pipeline, string tableName)
    {
        if (pipeline.Count == 0)
            throw new ArgumentException("Pipeline must contain at least one stage.", nameof(pipeline));

        var bag = new ParameterBag();
        var ctes = new List<string>();
        var hasGroupOrProject = false;
        var prevAlias = tableName;

        for (var i = 0; i < pipeline.Count; i++)
        {
            var currentAlias = $"s{i}";

            var cteSql = pipeline[i] switch
            {
                MatchStage ms => MatchStageBuilder.Build(ms, prevAlias, bag),
                LookupStage ls => LookupStageBuilder.Build(ls, prevAlias, tableName, bag),
                UnwindStage us => UnwindStageBuilder.Build(us, prevAlias),
                GroupStage gs => GroupStageBuilder.Build(gs, prevAlias, bag),
                ProjectStage ps => ProjectStageBuilder.Build(ps, prevAlias, bag, hasGroupOrProject),
                SortStage ss => SortStageBuilder.Build(ss, prevAlias, !hasGroupOrProject && i == pipeline.Count - 1, hasGroupOrProject),
                LimitStage ls => LimitStageBuilder.Build(ls, prevAlias),
                SkipStage ss => SkipStageBuilder.Build(ss, prevAlias),
                var unknown => throw new NotSupportedException($"Unsupported pipeline stage: {unknown.GetType().Name}")
            };

            if (pipeline[i] is GroupStage or ProjectStage)
                hasGroupOrProject = true;

            ctes.Add($"{currentAlias} AS ({cteSql})");
            prevAlias = currentAlias;
        }

        var sql = $"WITH {string.Join(", ", ctes)} SELECT * FROM {prevAlias}";
        return new SqlBuildResult(sql, bag.ToArray());
    }
}