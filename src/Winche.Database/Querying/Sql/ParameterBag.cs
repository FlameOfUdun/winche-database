using Npgsql;
using NpgsqlTypes;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// Collects positional parameters ($1..$n). THE rule: every value is a parameter;
/// string interpolation is allowed only for identifiers and enum-derived SQL.
/// </summary>
internal sealed class ParameterBag
{
    private readonly List<NpgsqlParameter> _params = [];

    public string Add(object? value)
    {
        _params.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return $"${_params.Count}";
    }

    public string AddJsonb(string json)
    {
        _params.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = json });
        return $"${_params.Count}";
    }

    public NpgsqlParameter[] ToArray() => [.. _params];
}
