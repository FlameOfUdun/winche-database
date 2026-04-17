using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WincheDatabase.AST.Models;
using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Infrastructure;

public static class QueryMatcher
{
    public static bool CouldAffect(Query query, QuerySnapshot snapshot, DocumentChange change)
    {
        var inSnapshot = snapshot.DocumentIds.Contains(change.Id);

        return change.Type switch
        {
            DocumentChangeType.Removed => inSnapshot,
            DocumentChangeType.Added => MatchesWhere(query.Where, change),
            DocumentChangeType.Modified => inSnapshot || MatchesWhere(query.Where, change),
            _ => true,
        };
    }

    private static bool MatchesWhere(WhereNode? where, DocumentChange change)
    {
        if (where == null || change.Data == null)
            return true;

        return Evaluate(where, change);
    }

    private static bool Evaluate(WhereNode node, DocumentChange change) => node switch
    {
        FieldFilter f => EvaluateFieldFilter(f, change),
        LogicGroup g => EvaluateLogicGroup(g, change),
        FieldCompare c => EvaluateFieldCompare(c, change),
        _ => true,
    };

    private static bool EvaluateLogicGroup(LogicGroup group, DocumentChange change)
    {
        if (group.Children.Count == 0)
            return true;

        return group.Operator switch
        {
            LogicalOperator.And => group.Children.All(c => Evaluate(c, change)),
            LogicalOperator.Or => group.Children.Any(c => Evaluate(c, change)),
            LogicalOperator.Not when group.Children.Count == 1 => !Evaluate(group.Children[0], change),
            _ => true,
        };
    }

    private static bool EvaluateFieldFilter(FieldFilter filter, DocumentChange change)
    {
        var field = ResolveField(filter.Field, change);
        var value = ToJsonNode(filter.Value);

        return filter.Operator switch
        {
            ConditionalOperator.Exists => EvaluateExists(field, filter.Value),
            ConditionalOperator.Eq => JsonEquals(field, value),
            ConditionalOperator.Ne => !JsonEquals(field, value),
            ConditionalOperator.Gt => JsonCompare(field, value) > 0,
            ConditionalOperator.Gte => JsonCompare(field, value) >= 0,
            ConditionalOperator.Lt => JsonCompare(field, value) < 0,
            ConditionalOperator.Lte => JsonCompare(field, value) <= 0,
            ConditionalOperator.In => EvaluateIn(field, value),
            ConditionalOperator.Nin => !EvaluateIn(field, value),
            ConditionalOperator.Contains => EvaluateStringOp(field, value, (s, v) => s.Contains(v, StringComparison.OrdinalIgnoreCase)),
            ConditionalOperator.StartsWith => EvaluateStringOp(field, value, (s, v) => s.StartsWith(v, StringComparison.OrdinalIgnoreCase)),
            ConditionalOperator.EndsWith => EvaluateStringOp(field, value, (s, v) => s.EndsWith(v, StringComparison.OrdinalIgnoreCase)),
            ConditionalOperator.Regex => EvaluateRegex(field, value),
            ConditionalOperator.ArrContains or ConditionalOperator.ArrContainsAll => EvaluateArrContainsAll(field, value),
            ConditionalOperator.ArrContainsAny => EvaluateArrContainsAny(field, value),
            _ => true,
        };
    }

    private static bool EvaluateFieldCompare(FieldCompare compare, DocumentChange change)
    {
        var left = ResolveField(compare.Left, change);
        var right = ResolveField(compare.Right, change);

        if (left == null || right == null)
            return true;

        var cmp = JsonCompare(left, right);
        return compare.Operator switch
        {
            ConditionalOperator.Eq => cmp == 0,
            ConditionalOperator.Ne => cmp != 0,
            ConditionalOperator.Gt => cmp > 0,
            ConditionalOperator.Gte => cmp >= 0,
            ConditionalOperator.Lt => cmp < 0,
            ConditionalOperator.Lte => cmp <= 0,
            _ => true,
        };
    }

    private static readonly Dictionary<string, Func<DocumentChange, JsonNode?>> MetadataColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = c => JsonValue.Create(c.Id),
            ["path"] = c => JsonValue.Create(c.Path),
            ["collection"] = c => JsonValue.Create(c.Collection),
            ["created_at"] = c => JsonValue.Create(c.CreatedAt),
            ["createdAt"] = c => JsonValue.Create(c.CreatedAt),
            ["updated_at"] = c => JsonValue.Create(c.UpdatedAt),
            ["updatedAt"] = c => JsonValue.Create(c.UpdatedAt),
            ["version"] = c => JsonValue.Create(c.Version),
        };

    private static JsonNode? ResolveField(string path, DocumentChange change)
    {
        if (MetadataColumns.TryGetValue(path, out var accessor))
            return accessor(change);

        return ResolveJsonPath(change.Data, path);
    }

    private static JsonNode? ResolveJsonPath(JsonObject? data, string path)
    {
        if (data == null)
            return null;

        var segments = path.Split('.');
        JsonNode? current = data;

        for (var i = 0; i < segments.Length; i++)
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segments[i], out current) || current == null)
                return null;
        }

        return current;
    }

    private static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        JsonNode n => n,
        JsonElement el => JsonNode.Parse(el.GetRawText()),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        float f => JsonValue.Create(f),
        double d => JsonValue.Create(d),
        decimal d => JsonValue.Create(d),
        DateTime dt => JsonValue.Create(dt),
        DateTimeOffset dto => JsonValue.Create(dto),
        IEnumerable<object?> list => new JsonArray(list.Select(ToJsonNode).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };

    private static JsonValueKind KindOf(JsonNode? node)
    {
        if (node == null) return JsonValueKind.Null;
        var el = node.GetValue<JsonElement>();
        return el.ValueKind;
    }

    private static bool JsonEquals(JsonNode? a, JsonNode? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        var ka = KindOf(a);
        var kb = KindOf(b);

        if (ka == JsonValueKind.Null && kb == JsonValueKind.Null) return true;
        if (ka != kb) return TryCrossKindEquals(a, ka, b, kb);

        return ka switch
        {
            JsonValueKind.String => string.Equals(a.GetValue<string>(), b.GetValue<string>(), StringComparison.Ordinal),
            JsonValueKind.Number => a.GetValue<double>() == b.GetValue<double>(),
            JsonValueKind.True or JsonValueKind.False => ka == kb,
            _ => false,
        };
    }

    private static bool TryCrossKindEquals(JsonNode a, JsonValueKind ka, JsonNode b, JsonValueKind kb)
    {
        // number vs string: try parsing the string as a number
        if (ka == JsonValueKind.Number && kb == JsonValueKind.String)
            return double.TryParse(b.GetValue<string>(), out var d) && a.GetValue<double>() == d;
        if (ka == JsonValueKind.String && kb == JsonValueKind.Number)
            return double.TryParse(a.GetValue<string>(), out var d) && d == b.GetValue<double>();

        return false;
    }

    private static int JsonCompare(JsonNode? a, JsonNode? b)
    {
        if (a == null || b == null)
            return a == null && b == null ? 0 : int.MinValue;

        var ka = KindOf(a);
        var kb = KindOf(b);

        // Same kind: straightforward comparison
        if (ka == kb)
        {
            return ka switch
            {
                JsonValueKind.Number => a.GetValue<double>().CompareTo(b.GetValue<double>()),
                JsonValueKind.String => string.Compare(a.GetValue<string>(), b.GetValue<string>(), StringComparison.Ordinal),
                _ => int.MinValue,
            };
        }

        // Cross-kind: try numeric coercion (string ↔ number)
        if (TryGetNumber(a, ka, out var na) && TryGetNumber(b, kb, out var nb))
            return na.CompareTo(nb);

        return int.MinValue;
    }

    private static bool TryGetNumber(JsonNode node, JsonValueKind kind, out double result)
    {
        if (kind == JsonValueKind.Number)
        {
            result = node.GetValue<double>();
            return true;
        }

        if (kind == JsonValueKind.String)
            return double.TryParse(node.GetValue<string>(), out result);

        result = 0;
        return false;
    }

    private static bool EvaluateExists(JsonNode? fieldValue, object? filterValue)
    {
        var exists = fieldValue != null && KindOf(fieldValue) != JsonValueKind.Null;
        return filterValue is true ? exists : !exists;
    }

    private static bool EvaluateIn(JsonNode? fieldValue, JsonNode? filterValue)
    {
        if (fieldValue == null || filterValue is not JsonArray arr)
            return false;

        return arr.Any(item => JsonEquals(fieldValue, item));
    }

    private static bool EvaluateStringOp(JsonNode? fieldValue, JsonNode? filterValue, Func<string, string, bool> op)
    {
        if (fieldValue == null || filterValue == null)
            return true;
        if (KindOf(fieldValue) != JsonValueKind.String || KindOf(filterValue) != JsonValueKind.String)
            return true;

        return op(fieldValue.GetValue<string>(), filterValue.GetValue<string>());
    }

    private static bool EvaluateRegex(JsonNode? fieldValue, JsonNode? filterValue)
    {
        if (fieldValue == null || filterValue == null)
            return true;
        if (KindOf(fieldValue) != JsonValueKind.String || KindOf(filterValue) != JsonValueKind.String)
            return true;

        try
        {
            return Regex.IsMatch(fieldValue.GetValue<string>(), filterValue.GetValue<string>());
        }
        catch
        {
            return true;
        }
    }

    private static bool EvaluateArrContainsAll(JsonNode? fieldValue, JsonNode? filterValue)
    {
        if (fieldValue is not JsonArray arr || filterValue == null)
            return true;

        if (filterValue is not JsonArray targets)
            return arr.Any(item => JsonEquals(item, filterValue));

        return targets.All(target => arr.Any(item => JsonEquals(item, target)));
    }

    private static bool EvaluateArrContainsAny(JsonNode? fieldValue, JsonNode? filterValue)
    {
        if (fieldValue is not JsonArray arr || filterValue == null)
            return true;

        if (filterValue is not JsonArray targets)
            return arr.Any(item => JsonEquals(item, filterValue));

        return targets.Any(target => arr.Any(item => JsonEquals(item, target)));
    }
}
