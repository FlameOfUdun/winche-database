using System.Globalization;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// LITERAL predicate emitter for filtered indexes (spec D). DDL cannot take parameters, so
/// this supports a deliberately restricted subset — And, Eq/Gt/Gte/Lt/Lte on
/// string/int/double/bool/timestamp, Exists/IsNull — with strict validation. Everything else
/// throws ArgumentException at index-sync time. Fragments mirror OperatorRegistry's same-class
/// forms so query filters implying the predicate can use the index.
/// </summary>
public static class IndexPredicateSql
{
    public static string Emit(Filter filter) => filter switch
    {
        CompositeFilter { Op: CompositeOp.And } and =>
            $"({string.Join(" AND ", and.Filters.Select(Emit))})",
        CompositeFilter c =>
            throw new ArgumentException($"Index predicates support 'and' only, got {c.Op}."),
        UnaryFilter u => EmitUnary(u),
        FieldFilter f => EmitField(f),
        _ => throw new ArgumentException($"Index predicates do not support {filter.GetType().Name}."),
    };

    private static string EmitUnary(UnaryFilter u)
    {
        var acc = Accessor(u.Field);
        return u.Op switch
        {
            UnaryOp.Exists => $"({acc}) IS NOT NULL",
            UnaryOp.IsNull => $"winche_rank({acc}) = 10",
            _ => throw new ArgumentException($"Index predicates do not support unary {u.Op}."),
        };
    }

    private static string EmitField(FieldFilter f)
    {
        if (f.Op is not (FilterOperator.Eq or FilterOperator.Gt or FilterOperator.Gte
            or FilterOperator.Lt or FilterOperator.Lte))
            throw new ArgumentException($"Index predicates support eq/gt/gte/lt/lte, got {f.Op}.");

        var acc = Accessor(f.Field);
        var op = f.Op switch
        {
            FilterOperator.Eq => "=", FilterOperator.Gt => ">", FilterOperator.Gte => ">=",
            FilterOperator.Lt => "<", _ => "<=",
        };

        return f.Operand switch
        {
            StringValue s =>
                $"(winche_rank({acc}) = 50 AND winche_text({acc}) COLLATE \"C\" {op} {StringLiteral(s.Value)})",
            IntegerValue i =>
                $"(winche_rank({acc}) = 30 AND winche_num({acc}) {op} {i.Value.ToString(CultureInfo.InvariantCulture)})",
            DoubleValue d when double.IsFinite(d.Value) =>
                $"(winche_rank({acc}) = 30 AND winche_num({acc}) {op} {ValueSql.DoubleRoundTrip(d.Value)})",
            DoubleValue d =>
                throw new ArgumentException($"Index predicate doubles must be finite, got {d.Value}."),
            BooleanValue b =>
                $"(winche_rank({acc}) = 20 AND winche_num({acc}) {op} {(b.Value ? 1 : 0)})",
            TimestampValue t =>
                $"(winche_rank({acc}) = 40 AND winche_num({acc}) {op} {ValueSql.EpochMicros(t).ToString(CultureInfo.InvariantCulture)})",
            _ => throw new ArgumentException(
                $"Index predicates support string/int/double/bool/timestamp operands, got {f.Operand.GetType().Name}."),
        };
    }

    private static string StringLiteral(string s)
    {
        if (s.Any(c => c < ' '))
            throw new ArgumentException("Index predicate strings may not contain control characters.");
        return $"'{s.Replace("'", "''")}'";
    }

    private static string Accessor(Documents.FieldPath field)
    {
        if (field.Segments is ["__name__"])
            throw new ArgumentException("Index predicates do not support __name__.");
        // reuse IndexSql's strict literal accessor (segments validated there)
        return IndexSql.LiteralAccessor(field.ToString());
    }
}
