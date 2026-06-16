using Npgsql;
using Winche.Database.Querying.Sql;
using Xunit;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Verifies <see cref="SchemaSql.MigrationDdl"/> upgrades a legacy-shape database in place and is a
/// no-op when run again. Uses a throwaway schema so the legacy objects never collide with the
/// fixture's real schema.
/// </summary>
[Collection("postgres")]
public class SchemaMigrationTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    [Fact]
    public async Task Migration_UpgradesLegacySchema_BackfillsCollectionId_AndIsIdempotent()
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        var schema = "mig_" + Guid.NewGuid().ToString("N")[..12];
        await ExecAsync(conn, $"CREATE SCHEMA {schema};");
        await ExecAsync(conn, $"SET search_path = {schema};");

        try
        {
            // ── Legacy-shape tables with data ────────────────────────────────────
            await ExecAsync(conn, """
                CREATE TABLE winche_documents (
                    path TEXT PRIMARY KEY, id TEXT NOT NULL, collection TEXT NOT NULL,
                    data JSONB NOT NULL DEFAULT '{}', created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), version BIGINT NOT NULL DEFAULT 1);
                CREATE INDEX idx_winche_documents_id ON winche_documents(id);
                CREATE INDEX idx_winche_documents_collection_id ON winche_documents(collection, id ASC);
                INSERT INTO winche_documents (path, id, collection)
                    VALUES ('userData/alice/sessionHistory/s1', 's1', 'userData/alice/sessionHistory'),
                           ('users/bob', 'bob', 'users');
                CREATE TABLE winche_changes (
                    seq BIGSERIAL PRIMARY KEY, type TEXT NOT NULL, path TEXT NOT NULL,
                    collection TEXT NOT NULL, version BIGINT NOT NULL, commit_time TIMESTAMPTZ NOT NULL);
                CREATE INDEX idx_winche_changes_commit_time ON winche_changes(commit_time);
                CREATE TABLE winche_feed_cursors (
                    consumer TEXT PRIMARY KEY, seq BIGINT NOT NULL, updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
                """);

            // ── Run the migration twice (must be idempotent) ─────────────────────
            await ExecAsync(conn, SchemaSql.MigrationDdl());
            await ExecAsync(conn, SchemaSql.MigrationDdl());

            // ── Documents: renamed columns, collection_id backfilled, data intact ─
            Assert.Equal("sessionHistory", await ScalarAsync<string>(conn,
                "SELECT collection_id FROM winche_documents WHERE document_path = 'userData/alice/sessionHistory/s1'"));
            Assert.Equal("s1", await ScalarAsync<string>(conn,
                "SELECT document_id FROM winche_documents WHERE document_path = 'userData/alice/sessionHistory/s1'"));
            // top-level collection: collection_id == the single segment
            Assert.Equal("users", await ScalarAsync<string>(conn,
                "SELECT collection_id FROM winche_documents WHERE collection_path = 'users'"));
            // collection_id is NOT NULL after migration
            Assert.Equal(0L, await ScalarAsync<long>(conn,
                "SELECT count(*) FROM winche_documents WHERE collection_id IS NULL"));

            // ── Feed tables renamed under the winche_documents_ prefix ────────────
            Assert.True(await ScalarAsync<bool>(conn, "SELECT to_regclass('winche_documents_changes') IS NOT NULL"));
            Assert.True(await ScalarAsync<bool>(conn, "SELECT to_regclass('winche_documents_feed_cursors') IS NOT NULL"));
            Assert.True(await ScalarAsync<bool>(conn, "SELECT to_regclass('winche_changes') IS NULL"));

            // ── Old document indexes renamed (no duplicate/leftover) ─────────────
            Assert.True(await ScalarAsync<bool>(conn,
                "SELECT to_regclass('idx_winche_documents_document_id') IS NOT NULL"));
            Assert.True(await ScalarAsync<bool>(conn,
                "SELECT to_regclass('idx_winche_documents_id') IS NULL"));
        }
        finally
        {
            await ExecAsync(conn, "RESET search_path;");
            await ExecAsync(conn, $"DROP SCHEMA {schema} CASCADE;");
        }
    }

    [Fact]
    public async Task Migration_OnFreshSchema_IsNoOp()
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        var schema = "mig_" + Guid.NewGuid().ToString("N")[..12];
        await ExecAsync(conn, $"CREATE SCHEMA {schema};");
        await ExecAsync(conn, $"SET search_path = {schema};");
        try
        {
            // No tables exist — migration must run cleanly and create nothing.
            await ExecAsync(conn, SchemaSql.MigrationDdl());
            Assert.True(await ScalarAsync<bool>(conn, "SELECT to_regclass('winche_documents') IS NULL"));
            Assert.True(await ScalarAsync<bool>(conn, "SELECT to_regclass('winche_documents_changes') IS NULL"));
        }
        finally
        {
            await ExecAsync(conn, "RESET search_path;");
            await ExecAsync(conn, $"DROP SCHEMA {schema} CASCADE;");
        }
    }
}
