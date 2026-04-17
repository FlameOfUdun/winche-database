using Npgsql;

namespace WincheDatabase.SQL
{
    public sealed record SqlBuildResult(string Sql, NpgsqlParameter[] Parameters)
    {
        public NpgsqlCommand Apply(NpgsqlCommand cmd)
        {
            cmd.CommandText = Sql;
            cmd.Parameters.AddRange(Parameters);
            return cmd;
        }
    }
}
