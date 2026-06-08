// src/Winche.Database/Querying/Sql/OperatorRegistry.cs
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// THE single source of operator SQL. Filters (Emit) and cursors (EmitCrossTypeComparison)
/// share the same per-type-class comparison fragments, so they agree by construction.
/// Assumes winche_* functions are on the search_path (installed by SchemaManager).
/// </summary>
internal static class OperatorRegistry
{
    // ── Filter tree ───────────────────────────────────────────────────────────

    internal static string Emit(Filter filter, ParameterBag bag, string alias) =>
        Emit(filter, bag, new SchemaResolver(DocumentSchema.Plain, alias));

    internal static string Emit(Filter filter, ParameterBag bag, SchemaResolver resolver) => filter switch
    {
        CompositeFilter c => EmitComposite(c, bag, resolver),
        UnaryFilter u => EmitUnary(u, bag, resolver),
        FieldFilter f => EmitFieldFilter(f, bag, resolver),
        FieldCompare cmp => EmitFieldCompare(cmp, bag, resolver),
        _ => throw new NotSupportedException($"Unknown filter: {filter.GetType().Name}"),
    };

    private static string EmitComposite(CompositeFilter c, ParameterBag bag, SchemaResolver resolver) => c.Op switch
    {
        CompositeOp.And => $"({string.Join(" AND ", c.Filters.Select(f => Emit(f, bag, resolver)))})",
        CompositeOp.Or => $"({string.Join(" OR ", c.Filters.Select(f => Emit(f, bag, resolver)))})",
        CompositeOp.Not => $"NOT ({Emit(c.Filters[0], bag, resolver)})",
        _ => throw new NotSupportedException($"Unknown composite op: {c.Op}"),
    };

    private static string EmitUnary(UnaryFilter u, ParameterBag bag, SchemaResolver resolver)
    {
        var f = RequireTagged(resolver.Resolve(u.Field, bag)).Sql;
        return u.Op switch
        {
            UnaryOp.IsNull => $"winche_rank({f}) = 10",
            UnaryOp.IsNan => $"winche_rank({f}) = 29",
            UnaryOp.Exists => $"({f}) IS NOT NULL",
            _ => throw new NotSupportedException($"Unknown unary op: {u.Op}"),
        };
    }

    private static string EmitFieldFilter(FieldFilter f, ParameterBag bag, SchemaResolver resolver) => f.Op switch
    {
        FilterOperator.Eq => EmitEq(f.Field, f.Operand, bag, resolver),
        FilterOperator.Gt or FilterOperator.Gte or FilterOperator.Lt or FilterOperator.Lte =>
            EmitSameClassInequality(f.Field, f.Op, f.Operand, bag, resolver),
        FilterOperator.Ne => EmitNe(f.Field, f.Operand, bag, resolver),
        FilterOperator.In => EmitIn(f.Field, (ArrayValue)f.Operand, bag, resolver),
        FilterOperator.NotIn => EmitNotIn(f.Field, (ArrayValue)f.Operand, bag, resolver),
        FilterOperator.ArrayContains => EmitArrayContains(f.Field, f.Operand, bag, resolver),
        FilterOperator.ArrayContainsAny => EmitArrayContainsAny(f.Field, (ArrayValue)f.Operand, bag, resolver),
        FilterOperator.ArrayContainsAll => EmitArrayContainsAll(f.Field, (ArrayValue)f.Operand, bag, resolver),
        FilterOperator.Contains => EmitLike(f.Field, f.Operand, bag, resolver, "%", "%"),
        FilterOperator.StartsWith => EmitLike(f.Field, f.Operand, bag, resolver, "", "%"),
        FilterOperator.EndsWith => EmitLike(f.Field, f.Operand, bag, resolver, "%", ""),
        FilterOperator.Regex => EmitRegex(f.Field, f.Operand, bag, resolver),
        _ => throw new NotSupportedException($"Unknown operator: {f.Op}"),
    };

    private static string EmitFieldCompare(FieldCompare cmp, ParameterBag bag, SchemaResolver resolver)
    {
        var leftRef = resolver.Resolve(cmp.Left, bag);
        var rightRef = resolver.Resolve(cmp.Right, bag);

        string l, r;

        if (leftRef is PathRef lp && rightRef is PathRef rp)
        {
            // both sides are __name__: compare path columns directly
            return cmp.Op switch
            {
                FilterOperator.Eq => $"{lp.Sql} COLLATE \"C\" = {rp.Sql}",
                FilterOperator.Ne => $"({lp.Sql} COLLATE \"C\" <> {rp.Sql})",
                FilterOperator.Gt => $"{lp.Sql} COLLATE \"C\" > {rp.Sql}",
                FilterOperator.Gte => $"{lp.Sql} COLLATE \"C\" >= {rp.Sql}",
                FilterOperator.Lt => $"{lp.Sql} COLLATE \"C\" < {rp.Sql}",
                FilterOperator.Lte => $"{lp.Sql} COLLATE \"C\" <= {rp.Sql}",
                _ => throw new NotSupportedException($"compare does not support {cmp.Op}"),
            };
        }

        if (leftRef is PathRef lPath)
        {
            var rTagged = ((TaggedRef)rightRef).Sql;
            var sqlOp = SqlOp(cmp.Op);
            return $"{lPath.Sql} COLLATE \"C\" {sqlOp} winche_text({rTagged})";
        }

        if (rightRef is PathRef rPath)
        {
            var lTagged = ((TaggedRef)leftRef).Sql;
            var sqlOp = SqlOp(cmp.Op);
            return $"winche_text({lTagged}) COLLATE \"C\" {sqlOp} {rPath.Sql}";
        }

        l = $"winche_key({((TaggedRef)leftRef).Sql})";
        r = $"winche_key({((TaggedRef)rightRef).Sql})";
        return cmp.Op switch
        {
            FilterOperator.Eq => $"{l} = {r}",
            FilterOperator.Ne => $"({l} IS NOT NULL AND {r} IS NOT NULL AND {l} <> {r})",
            FilterOperator.Gt => $"{l} > {r}",
            FilterOperator.Gte => $"{l} >= {r}",
            FilterOperator.Lt => $"{l} < {r}",
            FilterOperator.Lte => $"{l} <= {r}",
            _ => throw new NotSupportedException($"compare does not support {cmp.Op}"),
        };
    }

    // ── Comparison fragments (shared with CursorSql) ──────────────────────────

    /// <summary>Typed equality: field equals operand (same type-class; int 5 == double 5.0).</summary>
    internal static string EmitEq(FieldPath field, Value operand, ParameterBag bag, string alias) =>
        EmitEq(field, operand, bag, new SchemaResolver(DocumentSchema.Plain, alias));

    internal static string EmitEq(FieldPath field, Value operand, ParameterBag bag, SchemaResolver resolver)
    {
        var fieldRef = resolver.Resolve(field, bag);
        if (fieldRef is PathRef p)
            return $"{p.Sql} COLLATE \"C\" = {bag.Add(NameOperand(operand))}";

        var f = ((TaggedRef)fieldRef).Sql;
        return operand switch
        {
            NullValue => $"winche_rank({f}) = 10",
            DoubleValue d when double.IsNaN(d.Value) => $"winche_rank({f}) = 29",
            BooleanValue or IntegerValue or DoubleValue or TimestampValue =>
                $"(winche_rank({f}) = {RankOf(operand)} AND winche_num({f}) = {ValueSql.NumOperand(operand, bag)})",
            StringValue s => $"(winche_rank({f}) = 50 AND winche_text({f}) COLLATE \"C\" = {bag.Add(s.Value)})",
            BytesValue b => $"(winche_rank({f}) = 60 AND winche_bytes({f}) = {bag.Add(b.Value)})",
            ReferenceValue r => $"(winche_rank({f}) = 70 AND winche_text({f}) COLLATE \"C\" = {bag.Add(r.Path)})",
            GeoPointValue g =>
                $"(winche_rank({f}) = 80 AND winche_num({f}) = {ValueSql.DoubleOperand(g.Latitude, bag)} AND winche_num2({f}) = {ValueSql.DoubleOperand(g.Longitude, bag)})",
            ArrayValue or MapValue =>
                $"winche_key({f}) = winche_key({ValueSql.CanonicalJsonb(operand, bag)})",
            _ => throw new NotSupportedException($"Eq not supported for {operand.GetType().Name}"),
        };
    }

    /// <summary>Same-class inequality (Firestore rule 1): only the operand's type-class matches.</summary>
    internal static string EmitSameClassInequality(FieldPath field, FilterOperator op, Value operand, ParameterBag bag, string alias) =>
        EmitSameClassInequality(field, op, operand, bag, new SchemaResolver(DocumentSchema.Plain, alias));

    internal static string EmitSameClassInequality(FieldPath field, FilterOperator op, Value operand, ParameterBag bag, SchemaResolver resolver)
    {
        var sqlOp = SqlOp(op);

        var fieldRef = resolver.Resolve(field, bag);
        if (fieldRef is PathRef p)
            return $"{p.Sql} COLLATE \"C\" {sqlOp} {bag.Add(NameOperand(operand))}";

        var f = ((TaggedRef)fieldRef).Sql;
        return operand switch
        {
            NullValue => "FALSE",                                       // Firestore: range over null matches nothing
            DoubleValue d when double.IsNaN(d.Value) => "FALSE",        // comparisons with NaN match nothing
            BooleanValue or IntegerValue or DoubleValue or TimestampValue =>
                $"(winche_rank({f}) = {RankOf(operand)} AND winche_num({f}) {sqlOp} {ValueSql.NumOperand(operand, bag)})",
            StringValue s =>
                $"(winche_rank({f}) = 50 AND winche_text({f}) COLLATE \"C\" {sqlOp} {bag.Add(s.Value)})",
            BytesValue b =>
                $"(winche_rank({f}) = 60 AND winche_bytes({f}) {sqlOp} {bag.Add(b.Value)})",
            ReferenceValue r =>
                $"(winche_rank({f}) = 70 AND winche_text({f}) COLLATE \"C\" {sqlOp} {bag.Add(r.Path)})",
            GeoPointValue g => EmitGeoInequality(f, op, g, bag),
            ArrayValue or MapValue =>
                $"(winche_rank({f}) = {RankOf(operand)} AND winche_key({f}) {sqlOp} winche_key({ValueSql.CanonicalJsonb(operand, bag)}))",
            _ => throw new NotSupportedException($"{op} not supported for {operand.GetType().Name}"),
        };
    }

    /// <summary>
    /// Cross-type comparison for cursors: row's value strictly beyond the boundary value in the
    /// TOTAL order (rank first, then same-class comparison). op must be Gt/Gte/Lt/Lte.
    /// </summary>
    internal static string EmitCrossTypeComparison(FieldPath field, FilterOperator op, Value operand, ParameterBag bag, string alias) =>
        EmitCrossTypeComparison(field, op, operand, bag, new SchemaResolver(DocumentSchema.Plain, alias));

    internal static string EmitCrossTypeComparison(FieldPath field, FilterOperator op, Value operand, ParameterBag bag, SchemaResolver resolver)
    {
        var fieldRef = resolver.Resolve(field, bag);
        if (fieldRef is PathRef p)
            return $"{p.Sql} COLLATE \"C\" {SqlOp(op)} {bag.Add(NameOperand(operand))}";

        var f = ((TaggedRef)fieldRef).Sql;
        var rank = RankOf(operand);
        var rankCmp = op is FilterOperator.Gt or FilterOperator.Gte ? ">" : "<";

        // ranks 10 (null) and 29 (NaN) have no intra-class payload: rank comparison decides everything
        if (operand is NullValue || (operand is DoubleValue dv && double.IsNaN(dv.Value)))
        {
            var inclusive = op is FilterOperator.Gte or FilterOperator.Lte;
            return inclusive
                ? $"(winche_rank({f}) {rankCmp} {rank} OR winche_rank({f}) = {rank})"
                : $"winche_rank({f}) {rankCmp} {rank}";
        }

        var sameClass = EmitSameClassInequality(field, op, operand, bag, resolver);
        return $"(winche_rank({f}) {rankCmp} {rank} OR {sameClass})";
    }

    /// <summary>GeoPoint range: latitude first, longitude breaks ties (Firestore order).</summary>
    private static string EmitGeoInequality(string f, FilterOperator op, GeoPointValue g, ParameterBag bag)
    {
        var lat = ValueSql.DoubleOperand(g.Latitude, bag);
        var lng = ValueSql.DoubleOperand(g.Longitude, bag);
        var strict = op is FilterOperator.Gt or FilterOperator.Gte ? ">" : "<";
        var last = SqlOp(op);
        return $"(winche_rank({f}) = 80 AND (winche_num({f}) {strict} {lat} OR (winche_num({f}) = {lat} AND winche_num2({f}) {last} {lng})))";
    }

    // ── Operator bodies ───────────────────────────────────────────────────────

    private static string EmitNe(FieldPath field, Value operand, ParameterBag bag, SchemaResolver resolver)
    {
        var fieldRef = resolver.Resolve(field, bag);
        if (fieldRef is PathRef p)
            // __name__ ne: path column comparison (no rank/nan check)
            return $"({p.Sql} COLLATE \"C\" <> {bag.Add(NameOperand(operand))})";

        var f = RequireTagged(fieldRef).Sql;
        var eq = EmitEq(field, operand, bag, resolver);
        return $"(({f}) IS NOT NULL AND winche_rank({f}) <> 10 AND winche_rank({f}) <> 29 AND NOT ({eq}))";
    }

    private static string EmitIn(FieldPath field, ArrayValue operand, ParameterBag bag, SchemaResolver resolver) =>
        $"({string.Join(" OR ", operand.Values.Select(v => EmitEq(field, v, bag, resolver)))})";

    private static string EmitNotIn(FieldPath field, ArrayValue operand, ParameterBag bag, SchemaResolver resolver)
    {
        var f = RequireTagged(resolver.Resolve(field, bag)).Sql;
        var anyEq = EmitIn(field, operand, bag, resolver);
        return $"(({f}) IS NOT NULL AND winche_rank({f}) <> 10 AND winche_rank({f}) <> 29 AND NOT {anyEq})";
    }

    private static string EmitArrayContains(FieldPath field, Value operand, ParameterBag bag, SchemaResolver resolver)
    {
        var f = RequireTagged(resolver.Resolve(field, bag)).Sql;
        var pj = ValueSql.CanonicalJsonb(operand, bag);
        return $"(winche_rank({f}) = 90 AND EXISTS (SELECT 1 FROM jsonb_array_elements(({f})->'arrayValue'->'values') AS _e(v) WHERE winche_key(_e.v) = winche_key({pj})))";
    }

    private static string EmitArrayContainsAny(FieldPath field, ArrayValue operand, ParameterBag bag, SchemaResolver resolver)
    {
        var f = RequireTagged(resolver.Resolve(field, bag)).Sql;
        var keys = string.Join(", ", operand.Values.Select(v => $"winche_key({ValueSql.CanonicalJsonb(v, bag)})"));
        return $"(winche_rank({f}) = 90 AND EXISTS (SELECT 1 FROM jsonb_array_elements(({f})->'arrayValue'->'values') AS _e(v) WHERE winche_key(_e.v) IN ({keys})))";
    }

    private static string EmitArrayContainsAll(FieldPath field, ArrayValue operand, ParameterBag bag, SchemaResolver resolver)
    {
        var f = RequireTagged(resolver.Resolve(field, bag)).Sql;
        var pj = ValueSql.CanonicalJsonb(operand, bag);
        return $"(winche_rank({f}) = 90 AND NOT EXISTS (" +
               $"SELECT 1 FROM jsonb_array_elements(({pj})->'arrayValue'->'values') AS _p(v) " +
               $"WHERE NOT EXISTS (SELECT 1 FROM jsonb_array_elements(({f})->'arrayValue'->'values') AS _e(v) " +
               $"WHERE winche_key(_e.v) = winche_key(_p.v))))";
    }

    private static string EmitLike(FieldPath field, Value operand, ParameterBag bag, SchemaResolver resolver, string prefix, string suffix)
    {
        var f = RequireTagged(resolver.Resolve(field, bag)).Sql;
        var pattern = prefix + LikePatternEscaper.Escape(((StringValue)operand).Value) + suffix;
        return $"(winche_rank({f}) = 50 AND winche_text({f}) ILIKE {bag.Add(pattern)} ESCAPE '\\')";
    }

    private static string EmitRegex(FieldPath field, Value operand, ParameterBag bag, SchemaResolver resolver)
    {
        var f = RequireTagged(resolver.Resolve(field, bag)).Sql;
        return $"(winche_rank({f}) = 50 AND winche_text({f}) ~ {bag.Add(((StringValue)operand).Value)})";
    }

    /// <summary>
    /// Asserts that <paramref name="r"/> is a <see cref="TaggedRef"/> (i.e. a regular data field),
    /// rather than a <see cref="PathRef"/> (__name__ path column). Operators that work on tagged
    /// values only (array, like, regex, notIn…) should use this instead of a raw cast so that
    /// any __name__ that slips past validation gets a clear <see cref="NotSupportedException"/>
    /// rather than an <see cref="InvalidCastException"/>.
    /// </summary>
    private static TaggedRef RequireTagged(FieldRef r) =>
        r as TaggedRef ?? throw new NotSupportedException("__name__ supports eq/ne and range comparisons only");

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static short RankOf(Value v) => (short)v.Rank;

    private static string SqlOp(FilterOperator op) => op switch
    {
        FilterOperator.Gt => ">",
        FilterOperator.Gte => ">=",
        FilterOperator.Lt => "<",
        FilterOperator.Lte => "<=",
        _ => throw new NotSupportedException($"Not a comparison operator: {op}"),
    };

    private static string NameOperand(Value v) => v switch
    {
        StringValue s => s.Value,
        ReferenceValue r => r.Path,
        _ => throw new NotSupportedException("__name__ comparisons require a string or reference value"),
    };
}
