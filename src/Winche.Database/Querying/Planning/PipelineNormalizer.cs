// src/Winche.Database/Querying/Planning/PipelineNormalizer.cs
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
// Alias to disambiguate the leaf types that exist in BOTH layers (Ast.ProjectExpr/FieldRefExpr/…
// vs the identically-named plan records). Bare names resolve to the plan types (this namespace);
// `Ast.` reaches the AST inputs. (System.Text.RegularExpressions is fully qualified below so its
// Match/Group types don't collide with the Match/Group pipeline stages.)
using Ast = Winche.Database.Querying.Ast;

namespace Winche.Database.Querying.Planning;

/// <summary>
/// Pipeline → LogicalPlan. Match must be first (it is the scan); Having becomes a
/// FilterNode after GroupNode; `as` names are validated because they become SQL identifiers.
/// Pipelines are explicit: no implicit tiebreaker, no implicit Exists, no implicit Page.
/// </summary>
internal static partial class PipelineNormalizer
{
    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial System.Text.RegularExpressions.Regex AsName();

    /// <summary>
    /// Base document column names that are always in scope when the row is document-shaped.
    /// `lookup`/`unwind` `as` names must not collide with these.
    /// </summary>
    private static readonly HashSet<string> DocumentColumns =
        new(["path", "id", "collection", "data", "created_at", "updated_at", "version", "__name__"],
            StringComparer.Ordinal);

    public static LogicalPlan Normalize(Pipeline pipeline)
    {
        if (pipeline.Stages.Count == 0)
            throw new PlanValidationException("PIPELINE_EMPTY", "Pipeline must contain at least one stage.");
        if (pipeline.Stages[0] is not Match match)
            throw new PlanValidationException("MATCH_FIRST", "The first pipeline stage must be 'match'.");
        if (pipeline.Stages.Skip(1).Any(s => s is Match))
            throw new PlanValidationException("MATCH_FIRST", "'match' is only allowed as the first stage.");

        if (!DocumentPathParser.IsValidCollectionPath(match.Collection, out var pathError))
            throw new PlanValidationException("BAD_COLLECTION_PATH", pathError!);

        var nodes = new List<PlanNode> { new CollectionScan(match.Collection) };
        if (match.Where is not null)
            nodes.Add(new FilterNode(FilterRules.Prepare(match.Where)));

        // Track cumulative output names. Initialized to document-shaped reserved columns.
        var outputNames = new HashSet<string>(DocumentColumns, StringComparer.Ordinal);

        foreach (var stage in pipeline.Stages.Skip(1))
            Append(nodes, stage, outputNames);

        return new LogicalPlan(nodes);
    }

    private static void Append(List<PlanNode> nodes, Stage stage, HashSet<string> outputNames)
    {
        switch (stage)
        {
            case Where f:
                nodes.Add(new FilterNode(FilterRules.Prepare(f.Predicate)));
                break;

            case Lookup l:
                if (!DocumentPathParser.IsValidCollectionPath(l.Collection, out var lookupPathErr))
                    throw new PlanValidationException("BAD_COLLECTION_PATH", lookupPathErr!);
                ValidateAs(l.As);
                if (outputNames.Contains(l.As))
                    throw new PlanValidationException("DUPLICATE_AS",
                        $"Output name '{l.As}' already exists in the pipeline row.");
                if (l.Limit < 1)
                    throw new PlanValidationException("BAD_LIMIT", $"lookup 'limit' must be >= 1, got {l.Limit}.");
                outputNames.Add(l.As);
                nodes.Add(new LookupNode(l.Collection, l.LocalField, l.ForeignField, l.As,
                    l.Where is null ? null : FilterRules.Prepare(l.Where),
                    l.OrderBy?.Select(o => new SortKey(o.Field, o.Direction)).ToList(),
                    l.Limit));
                break;

            case Unwind u:
                ValidateAs(u.As);
                if (outputNames.Contains(u.As))
                    throw new PlanValidationException("DUPLICATE_AS",
                        $"Output name '{u.As}' already exists in the pipeline row.");
                outputNames.Add(u.As);
                nodes.Add(new UnwindNode(u.Field, u.As, u.PreserveNullAndEmpty));
                break;

            case Group g:
                ValidateGroup(g);
                // group/project REPLACE the row shape — reset cumulative set to this stage's outputs
                outputNames.Clear();
                foreach (var k in g.Keys) outputNames.Add(k.As);
                foreach (var a in g.Accumulators) outputNames.Add(a.As);
                nodes.Add(new GroupNode(
                    [.. g.Keys.Select(k => new GroupKey(k.As, k.Field))],
                    [.. g.Accumulators.Select(a => new Accumulator(a.As, a.Fn, a.Field))]));
                if (g.Having is not null)
                    nodes.Add(new FilterNode(FilterRules.Prepare(g.Having)));
                break;

            case Project p:
                ValidateProject(p);
                // project REPLACES the row shape — reset cumulative set to this stage's outputs
                outputNames.Clear();
                foreach (var f in p.Fields) outputNames.Add(f.As);
                nodes.Add(new ProjectNode([.. p.Fields.Select(f => new Projection(f.As, ToExpr(f.Expr)))]));
                break;

            case Sort s:
                nodes.Add(new SortNode([.. s.Fields.Select(o => new SortKey(o.Field, o.Direction))]));
                break;

            case Limit l:
                if (l.Count < 1)
                    throw new PlanValidationException("BAD_LIMIT", $"'limit' must be >= 1, got {l.Count}.");
                nodes.Add(new LimitNode(l.Count));
                break;

            case Skip s:
                if (s.Count < 0)
                    throw new PlanValidationException("BAD_SKIP", $"'skip' must be >= 0, got {s.Count}.");
                nodes.Add(new SkipNode(s.Count));
                break;

            default:
                throw new PlanValidationException("UNKNOWN_STAGE", $"Unknown stage: {stage.GetType().Name}");
        }
    }

    private static void ValidateGroup(Group g)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in g.Keys)
        {
            ValidateAs(k.As);
            if (!names.Add(k.As))
                throw new PlanValidationException("DUPLICATE_AS", $"Duplicate output name '{k.As}' in group.");
        }
        foreach (var a in g.Accumulators)
        {
            ValidateAs(a.As);
            if (!names.Add(a.As))
                throw new PlanValidationException("DUPLICATE_AS", $"Duplicate output name '{a.As}' in group.");
            if (a.Fn != AggFunction.Count && a.Field is null)
                throw new PlanValidationException("ACC_FIELD", $"'{a.Fn}' requires a 'field'.");
        }
        if (g.Accumulators.Count == 0 && g.Keys.Count == 0)
            throw new PlanValidationException("GROUP_EMPTY", "group needs at least one key or accumulator.");
    }

    private static void ValidateProject(Project p)
    {
        if (p.Fields.Count == 0)
            throw new PlanValidationException("PROJECT_EMPTY", "project needs at least one field.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in p.Fields)
        {
            ValidateAs(f.As);
            if (!names.Add(f.As))
                throw new PlanValidationException("DUPLICATE_AS", $"Duplicate output name '{f.As}' in project.");
            if (f.Expr is Ast.AggFuncExpr agg)
            {
                if (agg.Fn is not (AggFunction.Count or AggFunction.Sum or AggFunction.Avg))
                    throw new PlanValidationException("PROJECT_AGG",
                        $"project aggregates support count/sum/avg only, got {agg.Fn}.");
                if (agg.Fn != AggFunction.Count && agg.Field is null)
                    throw new PlanValidationException("ACC_FIELD", $"'{agg.Fn}' requires a 'field'.");
            }
        }
    }

    /// <summary>
    /// Validates that the name is a legal SQL identifier AND is not the reserved literal
    /// <c>__name__</c> (which is a built-in column that must not be shadowed as an output alias).
    /// </summary>
    private static void ValidateAs(string name)
    {
        if (!AsName().IsMatch(name))
            throw new PlanValidationException("AS_NAME",
                $"'{name}' is not a valid output name (^[A-Za-z_][A-Za-z0-9_]{{0,62}}$).");
        if (name == "__name__")
            throw new PlanValidationException("AS_NAME",
                $"'__name__' is a reserved name and cannot be used as an output alias.");
    }

    private static ProjectExpr ToExpr(Ast.ProjectExpr e) => e switch
    {
        Ast.FieldRefExpr f => new FieldRefExpr(f.Field),
        Ast.LiteralExpr l => new LiteralExpr(l.Value),
        Ast.AggFuncExpr a => new AggFuncExpr(a.Fn, a.Field),
        _ => throw new PlanValidationException("UNKNOWN_STAGE", $"Unknown project expr: {e.GetType().Name}"),
    };
}
