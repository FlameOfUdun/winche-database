// src/Winche.Database/Querying/Sql/PipelineCompiler.cs
using System.Text;
using Winche.Database.Querying.Planning;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// LogicalPlan (pipeline shape) → CTE chain (s0, s1, …). Each stage receives an input
/// StageSchema and declares its output schema — this replaces the old engine's
/// hasGroupOrProject/isPostTransformation flags. Adjacent [Sort][Skip?][Limit?] fuse into
/// one unit so ordering never crosses a CTE boundary unguaranteed; a trailing unit attaches
/// to the outer SELECT. Limits without any reachable sort select arbitrary rows (documented contract).
/// </summary>
/// <remarks>`table` is an identifier from server configuration — never user input.</remarks>
internal static class PipelineCompiler
{
    private const string DocColumns = "path, id, collection, data, created_at, updated_at, version";

    internal static (CompiledSql Sql, StageSchema FinalSchema) Compile(LogicalPlan plan, string table)
    {
        if (plan.Nodes.Count == 0 || plan.Nodes[0] is not CollectionScan scan)
            throw new InvalidOperationException("Pipeline plan must start with CollectionScan.");

        var bag = new ParameterBag();
        var ctes = new List<string>();
        StageSchema schema = DocumentSchema.Plain;

        ctes.Add($"s0 AS (SELECT {DocColumns} FROM {table} WHERE collection = {bag.Add(scan.Collection)})");
        var prev = "s0";
        var idx = 1;

        var nodes = plan.Nodes;
        var i = 1;
        string? finalTail = null;

        // Track the most recent sort so a non-adjacent skip/limit can re-emit ORDER BY.
        // Reset to null when group/project replaces the row shape (old sort keys lose meaning).
        SortNode? lastSort = null;

        while (i < nodes.Count)
        {
            if (nodes[i] is SortNode or SkipNode or LimitNode)
            {
                SortNode? sort = null; int? skip = null; int? limit = null;
                if (nodes[i] is SortNode sn) { sort = sn; lastSort = sn; i++; }
                if (i < nodes.Count && nodes[i] is SkipNode sk) { skip = sk.Count; i++; }
                if (i < nodes.Count && nodes[i] is LimitNode ln) { limit = ln.Count; i++; }

                // If this unit has no sort but we have skip/limit and a preceding sort exists,
                // re-emit the last sort's ORDER BY to guarantee deterministic row selection.
                var effectiveSort = sort ?? (((skip ?? 0) > 0 || limit is not null) ? lastSort : null);

                var tail = BuildTail(effectiveSort, skip, limit, schema, prev, bag);
                if (i >= nodes.Count)
                {
                    finalTail = tail;
                }
                else
                {
                    ctes.Add($"s{idx} AS (SELECT * FROM {prev}{tail})");
                    prev = $"s{idx}"; idx++;
                }
                continue;
            }

            var (body, outSchema) = CompileStage(nodes[i], prev, schema, bag, table);
            ctes.Add($"s{idx} AS ({body})");
            prev = $"s{idx}"; idx++;
            schema = outSchema;
            // Group/project replace the row shape; old sort keys lose meaning
            if (nodes[i] is GroupNode or ProjectNode)
                lastSort = null;
            i++;
        }

        var sql = $"WITH {string.Join(",\n", ctes)}\nSELECT * FROM {prev}{finalTail ?? ""}";
        return (new CompiledSql(sql, bag.ToArray()), schema);
    }

    private static string BuildTail(SortNode? sort, int? skip, int? limit, StageSchema schema, string prev, ParameterBag bag)
    {
        var sb = new StringBuilder();
        if (sort is not null)
            sb.Append($" ORDER BY {OrderingSql.Build(sort.Keys, bag, new SchemaResolver(schema, prev))}");
        if (skip is > 0)
            sb.Append($" OFFSET {bag.Add(skip.Value)}");
        if (limit is not null)
            sb.Append($" LIMIT {bag.Add(limit.Value)}");
        return sb.ToString();
    }

    private static (string Body, StageSchema Out) CompileStage(
        PlanNode node, string prev, StageSchema schema, ParameterBag bag, string table)
    {
        var resolver = new SchemaResolver(schema, prev);

        switch (node)
        {
            case FilterNode f:
                return ($"SELECT * FROM {prev} WHERE {OperatorRegistry.Emit(f.Predicate, bag, resolver)}", schema);

            case UnwindNode u:
            {
                var field = TaggedSql(resolver.Resolve(u.Field, bag));
                var join = u.PreserveNullAndEmpty
                    ? $"LEFT JOIN LATERAL jsonb_array_elements(({field})->'arrayValue'->'values') AS _u(v) ON TRUE"
                    : $"CROSS JOIN LATERAL jsonb_array_elements(({field})->'arrayValue'->'values') AS _u(v)";
                return ($"SELECT {prev}.*, _u.v AS \"{u.As}\" FROM {prev} {join}", AddColumn(schema, u.As));
            }

            case LookupNode l:
            {
                var local = resolver.Resolve(l.LocalField, bag);
                var fResolver = new SchemaResolver(DocumentSchema.Plain, "_f");
                var foreign = fResolver.Resolve(l.ForeignField, bag);

                var joinCond = JoinCondition(local, foreign);
                var filterSql = l.Filter is null ? "" : $" AND ({OperatorRegistry.Emit(l.Filter, bag, fResolver)})";

                // each foreign doc → tagged mapValue of its fields + __name__ as referenceValue
                const string Doc = "jsonb_build_object('mapValue', jsonb_build_object('fields', " +
                    "_f.data || jsonb_build_object('__name__', jsonb_build_object('referenceValue', _f.path))))";

                string sub;
                if (l.OrderBy is { Count: > 0 })
                {
                    // Use ROW_NUMBER() OVER (ORDER BY ...) so jsonb_agg can ORDER BY the ordinal,
                    // guaranteeing that the aggregated array respects the requested order regardless
                    // of Postgres planner choices.
                    var orderClause = OrderingSql.Build(l.OrderBy, bag, fResolver);
                    sub =
                        "SELECT jsonb_build_object('arrayValue', jsonb_build_object('values', " +
                        "COALESCE(jsonb_agg(_sub.doc ORDER BY _sub.ord), '[]'::jsonb))) " +
                        $"FROM (SELECT {Doc} AS doc, ROW_NUMBER() OVER (ORDER BY {orderClause}) AS ord " +
                        $"FROM {table} _f " +
                        $"WHERE _f.collection = {bag.Add(l.Collection)} AND {joinCond}{filterSql} " +
                        $"ORDER BY ord LIMIT {bag.Add(l.Limit)}) _sub";
                }
                else
                {
                    sub =
                        "SELECT jsonb_build_object('arrayValue', jsonb_build_object('values', " +
                        "COALESCE(jsonb_agg(_sub.doc), '[]'::jsonb))) " +
                        $"FROM (SELECT {Doc} AS doc FROM {table} _f " +
                        $"WHERE _f.collection = {bag.Add(l.Collection)} AND {joinCond}{filterSql} " +
                        $"LIMIT {bag.Add(l.Limit)}) _sub";
                }

                return ($"SELECT {prev}.*, ({sub}) AS \"{l.As}\" FROM {prev}", AddColumn(schema, l.As));
            }

            case GroupNode g:
            {
                var selects = new List<string>();
                var groupBys = new List<string>();
                var cols = new Dictionary<string, ColumnShape>();

                foreach (var k in g.Keys)
                {
                    var expr = TaggedSql(resolver.Resolve(k.Field, bag));
                    selects.Add($"(array_agg({expr}))[1] AS \"{k.As}\"");
                    groupBys.Add($"winche_key({expr})");
                    cols[k.As] = ColumnShape.TaggedValue;
                }

                foreach (var a in g.Accumulators)
                {
                    var tagged = a.Field is null ? null : TaggedSql(resolver.Resolve(a.Field, bag));
                    selects.Add($"{AggregateSql.Emit(a.Fn, tagged, windowed: false)} AS \"{a.As}\"");
                    cols[a.As] = ColumnShape.TaggedValue;
                }

                var body = $"SELECT {string.Join(", ", selects)} FROM {prev}"
                    + (groupBys.Count > 0 ? $" GROUP BY {string.Join(", ", groupBys)}" : "");
                return (body, new RowSchema(cols));
            }

            case ProjectNode p:
            {
                var selects = new List<string>();
                var cols = new Dictionary<string, ColumnShape>();

                foreach (var pr in p.Projections)
                {
                    var expr = pr.Expr switch
                    {
                        FieldRefExpr fr => TaggedSql(resolver.Resolve(fr.Field, bag)),
                        LiteralExpr lit => ValueSql.CanonicalJsonb(lit.Value, bag),
                        AggFuncExpr ag => AggregateSql.Emit(ag.Fn,
                            ag.Field is null ? null : TaggedSql(resolver.Resolve(ag.Field, bag)), windowed: true),
                        _ => throw new NotSupportedException($"Unknown projection: {pr.Expr.GetType().Name}"),
                    };
                    selects.Add($"{expr} AS \"{pr.As}\"");
                    cols[pr.As] = ColumnShape.TaggedValue;
                }

                return ($"SELECT {string.Join(", ", selects)} FROM {prev}", new RowSchema(cols));
            }

            default:
                throw new NotSupportedException($"Cannot compile {node.GetType().Name} in a pipeline.");
        }
    }

    /// <summary>__name__ used where a tagged value is needed → wrap the path as a referenceValue.</summary>
    private static string TaggedSql(FieldRef r) => r switch
    {
        TaggedRef t => t.Sql,
        PathRef p => $"jsonb_build_object('referenceValue', {p.Sql})",
        _ => throw new NotSupportedException($"Unknown FieldRef: {r.GetType().Name}"),
    };

    /// <summary>Typed lookup join: tagged↔tagged via winche_key; __name__ joins path against winche_text.</summary>
    private static string JoinCondition(FieldRef local, FieldRef foreign) => (local, foreign) switch
    {
        (TaggedRef l, TaggedRef f) => $"winche_key({f.Sql}) = winche_key({l.Sql})",
        (PathRef l, TaggedRef f) => $"{l.Sql} = winche_text({f.Sql})",
        (TaggedRef l, PathRef f) => $"{f.Sql} = winche_text({l.Sql})",
        (PathRef l, PathRef f) => $"{f.Sql} = {l.Sql}",
        _ => throw new NotSupportedException("Unsupported lookup join combination"),
    };

    private static StageSchema AddColumn(StageSchema schema, string name) => schema switch
    {
        DocumentSchema d => d.With(name),
        RowSchema r => r.With(name),
        _ => throw new NotSupportedException($"Unknown schema: {schema.GetType().Name}"),
    };
}
