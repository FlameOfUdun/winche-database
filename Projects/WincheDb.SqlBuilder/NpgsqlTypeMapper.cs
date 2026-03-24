using System.Collections;
using NpgsqlTypes;

namespace WincheDb.SqlBuilder;

internal static class NpgsqlTypeMapper
{
    internal static NpgsqlDbType Infer(object? value) => value switch
    {
        null => NpgsqlDbType.Text,
        string => NpgsqlDbType.Text,
        bool => NpgsqlDbType.Boolean,
        short or int => NpgsqlDbType.Integer,
        long => NpgsqlDbType.Bigint,
        float or double => NpgsqlDbType.Double,
        decimal => NpgsqlDbType.Numeric,
        DateTime => NpgsqlDbType.TimestampTz,
        DateTimeOffset => NpgsqlDbType.TimestampTz,
        DateOnly => NpgsqlDbType.Date,
        Guid => NpgsqlDbType.Uuid,
        IList => NpgsqlDbType.Jsonb,
        _ => NpgsqlDbType.Text
    };
}