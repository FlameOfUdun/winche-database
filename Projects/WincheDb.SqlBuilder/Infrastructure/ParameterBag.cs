using Npgsql;

namespace WincheDb.SqlBuilder.Infrastructure;

internal sealed class ParameterBag
{
    private readonly List<NpgsqlParameter> _params = [];

    public int Count => _params.Count;

    public string Add(object? value)
    {
        _params.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypeMapper.Infer(value),
            Value = value ?? DBNull.Value
        });
        return $"${_params.Count}";
    }

    public NpgsqlParameter[] ToArray() => [.. _params];
}