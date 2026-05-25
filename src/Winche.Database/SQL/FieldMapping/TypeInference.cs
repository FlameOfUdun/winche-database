using System.Text.Json;
using System.Text.Json.Nodes;
using Winche.Database.AST.Models;

namespace Winche.Database.SQL.FieldMapping
{
    public static class TypeInference
    {
        public static FieldType For<T>() => For(typeof(T));

        public static FieldType For(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;

            if (type == typeof(string)) return FieldType.Text;
            if (type == typeof(bool)) return FieldType.Boolean;
            if (type == typeof(int) || type == typeof(short)) return FieldType.Integer;
            if (type == typeof(long)) return FieldType.BigInt;
            if (type == typeof(float) || type == typeof(double)) return FieldType.Double;
            if (type == typeof(decimal)) return FieldType.Numeric;
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return FieldType.Timestamp;
            if (type == typeof(DateOnly)) return FieldType.Date;
            if (type == typeof(Guid)) return FieldType.Uuid;
            if (type == typeof(JsonElement) || type == typeof(JsonNode) || type == typeof(JsonObject) || type == typeof(JsonArray)) return FieldType.Jsonb;
            if (type.IsPrimitive) return FieldType.Numeric;
            if (type.IsClass || type.IsInterface) return FieldType.Jsonb;

            return FieldType.Text;
        }
    }
}
