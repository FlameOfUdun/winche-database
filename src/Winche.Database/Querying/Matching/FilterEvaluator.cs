using System.Text.RegularExpressions;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Querying.Matching;

/// <summary>
/// In-memory mirror of OperatorRegistry semantics, over typed fields. Used by change
/// detection (ChangeMatcher). Same rules as SQL: typed equality with int/double equivalence,
/// same-class inequalities, Ne/NotIn exclude missing/null/NaN, contains-family case-insensitive,
/// regex case-SENSITIVE, __name__ compares the document path by code points.
/// </summary>
public static class FilterEvaluator
{
    public static bool Matches(FilterAst filter, string path, IReadOnlyDictionary<string, Value> fields) => filter switch
    {
        CompositeFilterAst { Op: CompositeOp.And } c => c.Filters.All(f => Matches(f, path, fields)),
        CompositeFilterAst { Op: CompositeOp.Or } c => c.Filters.Any(f => Matches(f, path, fields)),
        CompositeFilterAst { Op: CompositeOp.Not } c => !Matches(c.Filters[0], path, fields),
        UnaryFilterAst u => MatchesUnary(u, fields),
        FieldFilterAst f => MatchesField(f, path, fields),
        FieldCompareAst cmp => MatchesCompare(cmp, fields),
        _ => false,
    };

    private static bool MatchesUnary(UnaryFilterAst u, IReadOnlyDictionary<string, Value> fields)
    {
        var v = ResolveField(u.Field, fields);
        return u.Op switch
        {
            UnaryOp.IsNull => v is NullValue,
            UnaryOp.IsNan => v is DoubleValue d && double.IsNaN(d.Value),
            UnaryOp.Exists => v is not null,
            _ => false,
        };
    }

    private static bool MatchesField(FieldFilterAst f, string path, IReadOnlyDictionary<string, Value> fields)
    {
        if (IsName(f.Field))
            return MatchesName(f.Op, path, f.Operand);

        var v = ResolveField(f.Field, fields);
        return f.Op switch
        {
            FilterOperator.Eq => v is not null && TypedEquals(v, f.Operand),
            FilterOperator.Gt or FilterOperator.Gte or FilterOperator.Lt or FilterOperator.Lte =>
                SameClassInequality(v, f.Op, f.Operand),
            FilterOperator.Ne =>
                v is not null and not NullValue && !IsNaN(v) && !TypedEquals(v, f.Operand),
            FilterOperator.In =>
                v is not null && ((ArrayValue)f.Operand).Values.Any(o => TypedEquals(v, o)),
            FilterOperator.NotIn =>
                v is not null and not NullValue && !IsNaN(v)
                && !((ArrayValue)f.Operand).Values.Any(o => TypedEquals(v, o)),
            FilterOperator.ArrayContains =>
                v is ArrayValue arr && arr.Values.Any(e => TypedEquals(e, f.Operand)),
            FilterOperator.ArrayContainsAny =>
                v is ArrayValue arrAny && ((ArrayValue)f.Operand).Values
                    .Any(o => arrAny.Values.Any(e => TypedEquals(e, o))),
            FilterOperator.ArrayContainsAll =>
                v is ArrayValue arrAll && ((ArrayValue)f.Operand).Values
                    .All(o => arrAll.Values.Any(e => TypedEquals(e, o))),
            FilterOperator.Contains => StringOp(v, f.Operand, (s, o) => s.Contains(o, StringComparison.OrdinalIgnoreCase)),
            FilterOperator.StartsWith => StringOp(v, f.Operand, (s, o) => s.StartsWith(o, StringComparison.OrdinalIgnoreCase)),
            FilterOperator.EndsWith => StringOp(v, f.Operand, (s, o) => s.EndsWith(o, StringComparison.OrdinalIgnoreCase)),
            FilterOperator.Regex => StringOp(v, f.Operand, (s, o) => Regex.IsMatch(s, o, RegexOptions.None, TimeSpan.FromSeconds(1))),
            _ => false,
        };
    }

    private static bool MatchesCompare(FieldCompareAst cmp, IReadOnlyDictionary<string, Value> fields)
    {
        var l = ResolveField(cmp.Left, fields);
        var r = ResolveField(cmp.Right, fields);
        if (l is null || r is null) return false;
        var c = ValueComparer.Instance.Compare(l, r);
        return cmp.Op switch
        {
            FilterOperator.Eq => c == 0,
            FilterOperator.Ne => c != 0,
            FilterOperator.Gt => c > 0,
            FilterOperator.Gte => c >= 0,
            FilterOperator.Lt => c < 0,
            FilterOperator.Lte => c <= 0,
            _ => false,
        };
    }

    private static bool MatchesName(FilterOperator op, string path, Value operand)
    {
        var s = operand switch
        {
            StringValue sv => sv.Value,
            ReferenceValue rv => rv.Path,
            _ => null,
        };
        if (s is null) return false;
        var c = ValueComparer.CompareCodePoints(path, s);   // Unicode code-point order matches SQL
        return op switch
        {
            FilterOperator.Eq => c == 0,
            FilterOperator.Ne => c != 0,
            FilterOperator.Gt => c > 0,
            FilterOperator.Gte => c >= 0,
            FilterOperator.Lt => c < 0,
            FilterOperator.Lte => c <= 0,
            _ => false,
        };
    }

    private static bool SameClassInequality(Value? v, FilterOperator op, Value operand)
    {
        if (v is null) return false;
        if (IsNaN(v) || IsNaN(operand) || operand is NullValue) return false;
        if (v.Rank != operand.Rank) return false;            // same type-class only (Number covers int+double)
        var c = ValueComparer.Instance.Compare(v, operand);
        return op switch
        {
            FilterOperator.Gt => c > 0,
            FilterOperator.Gte => c >= 0,
            FilterOperator.Lt => c < 0,
            FilterOperator.Lte => c <= 0,
            _ => false,
        };
    }

    private static bool TypedEquals(Value a, Value b) =>
        a.Rank == b.Rank && ValueComparer.Instance.Compare(a, b) == 0;

    private static bool StringOp(Value? v, Value operand, Func<string, string, bool> op) =>
        v is StringValue s && operand is StringValue o && op(s.Value, o.Value);

    private static bool IsNaN(Value v) => v is DoubleValue d && double.IsNaN(d.Value);

    private static bool IsName(FieldPath p) => p.Segments.Count == 1 && p.Segments[0] == "__name__";

    /// <summary>Navigates dotted paths through MapValues. null = missing.</summary>
    public static Value? ResolveField(FieldPath path, IReadOnlyDictionary<string, Value> fields)
    {
        Value? current = fields.TryGetValue(path.Segments[0], out var root) ? root : null;
        foreach (var seg in path.Segments.Skip(1))
        {
            if (current is not MapValue m || !m.Fields.TryGetValue(seg, out var next))
                return null;
            current = next;
        }
        return current;
    }
}
