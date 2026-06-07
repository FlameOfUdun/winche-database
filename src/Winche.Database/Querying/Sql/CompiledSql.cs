using Npgsql;

namespace Winche.Database.Querying.Sql;

public sealed record CompiledSql(string Sql, NpgsqlParameter[] Parameters)
{
    public NpgsqlCommand Apply(NpgsqlCommand cmd)
    {
        cmd.CommandText = Sql;
        cmd.Parameters.AddRange(Parameters);
        return cmd;
    }
}
