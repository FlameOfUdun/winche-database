using System.Collections;
using System.Text;
using WincheDatabase.AST.Models;

namespace WincheDatabase.Store.Infrastructure;

public static class QuerySerializer
{
    public static string Serialize(Query query)
    {
        var sb = new StringBuilder();

        sb.Append("c:").Append(Escape(query.Collection));
        sb.Append(";l:").Append(query.Limit);

        if (query.Where is not null)
            sb.Append(";w:").Append(SerializeWhere(query.Where));

        if (query.OrderBy.Count > 0)
            sb.Append(";o:").Append(string.Join(',', query.OrderBy.Select(SerializeSort)));

        if (query.StartAfter.Count > 0)
            sb.Append(";sa:").Append(SerializeCursor(query.StartAfter));

        if (query.StartAt.Count > 0)
            sb.Append(";sat:").Append(SerializeCursor(query.StartAt));

        if (query.EndBefore.Count > 0)
            sb.Append(";eb:").Append(SerializeCursor(query.EndBefore));

        if (query.EndAt.Count > 0)
            sb.Append(";ea:").Append(SerializeCursor(query.EndAt));

        return sb.ToString();
    }

    private static string SerializeWhere(WhereNode node) => node switch
    {
        FieldFilter f =>
            $"f:{Escape(f.Field)}:{f.Operator}:{SerializeValue(f.Value)}",

        FieldCompare c when c.Type is null =>
            $"fc:{Escape(c.Left)}:{c.Operator}:{Escape(c.Right)}",

        FieldCompare c =>
            $"fc:{Escape(c.Left)}:{c.Operator}:{Escape(c.Right)}:{c.Type}",

        // Not is unary — no sorting needed
        LogicGroup { Operator: LogicalOperator.Not } g =>
            $"not({SerializeWhere(g.Children[0])})",

        // And/Or are commutative — sort children for stability
        LogicGroup g =>
            $"{g.Operator.ToString().ToLowerInvariant()}({string.Join(',', g.Children.Select(SerializeWhere).Order())})",

        _ => throw new ArgumentOutOfRangeException(nameof(node), node.GetType().Name)
    };

    private static string SerializeSort(SortNode s) => s.Type is null
        ? $"{Escape(s.Field)}:{s.Direction}"
        : $"{Escape(s.Field)}:{s.Direction}:{s.Type}";

    private static string SerializeCursor(List<object?> values) =>
        string.Join(',', values.Select(SerializeValue));

    private static string SerializeValue(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => $"'{Escape(s)}'",
        DateTime dt => $"dt:{dt:O}",
        DateTimeOffset dto => $"dto:{dto:O}",
        IEnumerable list => $"[{string.Join(',', list.Cast<object?>().Select(SerializeValue))}]",
        _ => value.ToString() ?? "null"
    };

    private static string Escape(string s) => s
        .Replace("\\", "\\\\")
        .Replace(";", "\\;")
        .Replace(":", "\\:")
        .Replace(",", "\\,")
        .Replace("(", "\\(")
        .Replace(")", "\\)");
}
