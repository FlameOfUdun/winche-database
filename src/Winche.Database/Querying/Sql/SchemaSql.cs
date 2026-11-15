using Winche.Database.Constants;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// DDL for the winche_* ordering helper functions — the SQL mirror of Values.TypeRank.
/// Canonical cross-type total order: null(10) &lt; bool(20) &lt; NaN(29) &lt; number(30) &lt; timestamp(40)
/// &lt; string(50) &lt; bytes(60) &lt; reference(70) &lt; geopoint(80) &lt; array(90) &lt; map(100).
/// Phase 1 ships scalar ordering; winche_key (arrays/maps) ships in Phase 2.
/// Reference ordering compares full path bytes (not per-segment); diverges from the reference ordering only for ids containing chars below '/' (0x2F).
/// IMMUTABLE note: winche_num/winche_rank cast text→timestamptz, which is only truly immutable for offset-bearing strings; the engine always writes 'Z'-suffixed timestamps, so expression indexes are safe — externally-written offset-less strings would not be.
/// </summary>
public static class SchemaSql
{
    /// <summary>
    /// Table DDL: table, three standard indexes. The notify_document_change trigger was removed
    /// in Plan 3 (the durable changes feed is the only notifier now; see ChangesDdl).
    /// Cleanup lines drop the old trigger/function on existing databases.
    /// </summary>
    public static string TableDdl() => $$"""
        CREATE TABLE IF NOT EXISTS {{WincheDatabaseTables.Documents}} (
            document_path   TEXT        PRIMARY KEY,
            document_id     TEXT        NOT NULL,
            collection_path TEXT        NOT NULL,
            collection_id   TEXT        NOT NULL,
            data            JSONB       NOT NULL DEFAULT '{}',
            created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            version         BIGINT      NOT NULL DEFAULT 1
        );

        CREATE INDEX IF NOT EXISTS idx_{{WincheDatabaseTables.Documents}}_document_id
            ON {{WincheDatabaseTables.Documents}}(document_id);

        CREATE INDEX IF NOT EXISTS idx_{{WincheDatabaseTables.Documents}}_collection_docid
            ON {{WincheDatabaseTables.Documents}}(collection_path, document_id ASC);

        -- Open optimistic transactions (TransactionStore.Database, 10.5.0): any node serves any step.
        CREATE TABLE IF NOT EXISTS {{WincheDatabaseTables.Transactions}} (
            id            TEXT PRIMARY KEY,
            reads         JSONB NOT NULL DEFAULT '{}',
            created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
            last_activity TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        DROP TRIGGER IF EXISTS document_change_trigger ON {{WincheDatabaseTables.Documents}};
        DROP FUNCTION IF EXISTS public.notify_document_change() CASCADE;
        """;

    /// <summary>
    /// Idempotent in-place upgrade from the legacy schema (pre-collection-ID release) to the current
    /// shape: renames the document/feed columns and tables and backfills <c>collection_id</c>. Runs
    /// FIRST in <see cref="Schema.ISchemaManager.EnsureCreatedAsync"/>, before the CREATE-IF-NOT-EXISTS
    /// DDL, so an existing database is migrated before the additive DDL would create new-named objects.
    /// Every step is guarded on current-schema existence checks, so it is a no-op on a fresh database
    /// and safe to run on every startup (idempotent). Uses literal historical object names on purpose
    /// (not <c>WincheDatabaseTables</c>, which now holds the new names).
    /// </summary>
    /// <summary>
    /// Index for subtree reads and cascade deletes (<see cref="DocumentSql.SubtreeWhere"/>) and collection
    /// listing: the path in byte order (COLLATE "C"). The primary key can't serve a prefix range under the
    /// usual en_US-style database collation, so without this every cascade scanned the whole table.
    /// </summary>
    public const string PathIndex = "idx_" + WincheDatabaseTables.Documents + "_path_c";

    /// <summary>
    /// Builds <see cref="PathIndex"/> without blocking writes (CONCURRENTLY: one statement per command, outside
    /// a transaction). A concurrent build that failed leaves an invalid index behind, which IF NOT EXISTS would
    /// keep for ever, so that is dropped first.
    /// </summary>
    public static string PathIndexIsInvalid() => $"""
        SELECT EXISTS (
            SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relname = '{PathIndex}' AND c.relnamespace = current_schema()::regnamespace AND NOT i.indisvalid)
        """;

    /// <summary>
    /// The GIN index over whole documents that older versions created: no query uses the operators it
    /// serves (filters go through the winche_* functions), so it only slowed every write. Dropped at startup.
    /// </summary>
    public static string DropUnusedDataIndex() =>
        $"DROP INDEX CONCURRENTLY IF EXISTS idx_{WincheDatabaseTables.Documents}_data";

    public static string DropPathIndex() => $"DROP INDEX CONCURRENTLY IF EXISTS {PathIndex}";

    public static string CreatePathIndex() =>
        $"CREATE INDEX CONCURRENTLY IF NOT EXISTS {PathIndex} ON {WincheDatabaseTables.Documents} (document_path COLLATE \"C\")";

    public static string MigrationDdl() => """
        DO $migrate$
        BEGIN
            -- winche_documents: legacy column renames + collection_id backfill
            IF to_regclass('winche_documents') IS NOT NULL THEN
                IF EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema = current_schema() AND table_name = 'winche_documents' AND column_name = 'path') THEN
                    ALTER TABLE winche_documents RENAME COLUMN path TO document_path;
                END IF;
                IF EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema = current_schema() AND table_name = 'winche_documents' AND column_name = 'id') THEN
                    ALTER TABLE winche_documents RENAME COLUMN id TO document_id;
                END IF;
                IF EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema = current_schema() AND table_name = 'winche_documents' AND column_name = 'collection') THEN
                    ALTER TABLE winche_documents RENAME COLUMN collection TO collection_path;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                               WHERE table_schema = current_schema() AND table_name = 'winche_documents' AND column_name = 'collection_id') THEN
                    ALTER TABLE winche_documents ADD COLUMN collection_id TEXT;
                    UPDATE winche_documents SET collection_id = (regexp_match(collection_path, '[^/]+$'))[1];
                    ALTER TABLE winche_documents ALTER COLUMN collection_id SET NOT NULL;
                END IF;
            END IF;

            ALTER INDEX IF EXISTS idx_winche_documents_id RENAME TO idx_winche_documents_document_id;
            ALTER INDEX IF EXISTS idx_winche_documents_collection_id RENAME TO idx_winche_documents_collection_docid;

            -- change-feed tables: rename under the winche_documents_ prefix
            IF to_regclass('winche_changes') IS NOT NULL THEN
                ALTER TABLE winche_changes RENAME TO winche_documents_changes;
            END IF;
            -- The legacy table of that name, not Winche.Core's (which has a feed column).
            IF to_regclass('winche_feed_cursors') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema()
                               AND table_name = 'winche_feed_cursors' AND column_name = 'feed') THEN
                ALTER TABLE winche_feed_cursors RENAME TO winche_documents_feed_cursors;
            END IF;

            -- change-feed columns + drop the legacy trigger (ChangesDdl recreates the new trigger/function)
            IF to_regclass('winche_documents_changes') IS NOT NULL THEN
                IF EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema = current_schema() AND table_name = 'winche_documents_changes' AND column_name = 'path') THEN
                    ALTER TABLE winche_documents_changes RENAME COLUMN path TO document_path;
                END IF;
                IF EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema = current_schema() AND table_name = 'winche_documents_changes' AND column_name = 'collection') THEN
                    ALTER TABLE winche_documents_changes RENAME COLUMN collection TO collection_path;
                END IF;
                DROP TRIGGER IF EXISTS winche_changes_trigger ON winche_documents_changes;
            END IF;
            ALTER INDEX IF EXISTS idx_winche_changes_commit_time RENAME TO idx_winche_documents_changes_commit_time;
        END
        $migrate$;
        """;

    /// <summary>
    /// The durable change feed: one row per affected document, appended in the SAME
    /// transaction as the write (WriteApplier). The trigger emits pg_notify as a
    /// wake-up only — the rows are the durable truth (spec §2/§4).
    /// </summary>
    public static string ChangesDdl() => $$"""
        DO $v$
        BEGIN
            IF current_setting('server_version_num')::int < 130000 THEN
                RAISE EXCEPTION 'Winche.Database needs PostgreSQL 13 or later (the change feed uses xid8); this server is %', version();
            END IF;
        END
        $v$;

        CREATE TABLE IF NOT EXISTS {{WincheDatabaseTables.Changes}} (
            seq             BIGSERIAL PRIMARY KEY,
            type            TEXT NOT NULL,
            document_path   TEXT NOT NULL,
            collection_path TEXT NOT NULL,
            version         BIGINT NOT NULL,
            commit_time     TIMESTAMPTZ NOT NULL,
            -- The writing transaction: readers order by (txid, seq) and stop at the oldest transaction
            -- still running, so a row that commits late can't land behind a cursor (see ChangeFeedReader).
            txid            xid8 NOT NULL DEFAULT pg_current_xact_id()
        );

        CREATE INDEX IF NOT EXISTS idx_{{WincheDatabaseTables.Changes}}_commit_time
            ON {{WincheDatabaseTables.Changes}} (commit_time);

        -- Upgrade from a feed without txid (one statement, so one transaction). Existing rows and cursors get
        -- txid 0: those rows keep their seq order and sort before every later row. Stamping them with this
        -- transaction's id instead would let a write that was already running land behind the cursors.
        -- The constant default is instant (no table rewrite); new rows then default to their own transaction.
        DO $m$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema = current_schema() AND table_name = '{{WincheDatabaseTables.Changes}}' AND column_name = 'txid') THEN
                ALTER TABLE {{WincheDatabaseTables.Changes}} ADD COLUMN txid xid8 NOT NULL DEFAULT '0';
                ALTER TABLE {{WincheDatabaseTables.Changes}} ALTER COLUMN txid SET DEFAULT pg_current_xact_id();
            END IF;
        END
        $m$;

        CREATE INDEX IF NOT EXISTS idx_{{WincheDatabaseTables.Changes}}_txid_seq
            ON {{WincheDatabaseTables.Changes}} (txid, seq);

        -- The document before and after the change, for hooks registered withSnapshots (10.5.0): stored
        -- only for writes to paths such a hook covers, NULL otherwise. {fields, createdAt, updatedAt, version}.
        ALTER TABLE {{WincheDatabaseTables.Changes}}
            ADD COLUMN IF NOT EXISTS before jsonb,
            ADD COLUMN IF NOT EXISTS after  jsonb;

        -- Who made the change, for document history (11.1.0): the caller's user id, or DocumentWriteActor's.
        ALTER TABLE {{WincheDatabaseTables.Changes}} ADD COLUMN IF NOT EXISTS actor text;
        -- A document's history, or everything under a path (byte order, like the documents' path index).
        CREATE INDEX IF NOT EXISTS idx_{{WincheDatabaseTables.Changes}}_path_seq
            ON {{WincheDatabaseTables.Changes}} (document_path COLLATE "C", seq);

        -- How far pruning has reached: the newest transaction any deleted feed row came from. A resume token
        -- at or after it can be checked against the feed; an older one may have lost rows.
        CREATE TABLE IF NOT EXISTS {{WincheDatabaseTables.FeedPruned}} (
            id   INT PRIMARY KEY CHECK (id = 1),
            txid xid8 NOT NULL
        );

        CREATE OR REPLACE FUNCTION winche_notify_change() RETURNS TRIGGER AS $t$
        BEGIN
            PERFORM pg_notify('{{WincheDatabaseTables.ChangesNotifyChannel}}', NEW.seq::text);
            RETURN NEW;
        END;
        $t$ LANGUAGE plpgsql;

        DROP TRIGGER IF EXISTS winche_documents_changes_trigger ON {{WincheDatabaseTables.Changes}};
        CREATE TRIGGER winche_documents_changes_trigger
        AFTER INSERT ON {{WincheDatabaseTables.Changes}}
        FOR EACH ROW EXECUTE FUNCTION winche_notify_change();
        """;

    public static string HelperFunctions() => $"""
        CREATE OR REPLACE FUNCTION winche_rank(v jsonb) RETURNS smallint AS $f$
        SELECT CASE
            WHEN v ? 'nullValue' THEN 10
            WHEN v ? 'booleanValue' THEN 20
            WHEN v ? 'doubleValue' AND (v->>'doubleValue') = 'NaN' THEN 29
            WHEN v ? 'integerValue' OR v ? 'doubleValue' THEN 30
            WHEN v ? 'timestampValue' THEN 40
            WHEN v ? 'stringValue' THEN 50
            WHEN v ? 'bytesValue' THEN 60
            WHEN v ? 'docRefValue' THEN 70
            WHEN v ? 'fileRefValue' THEN 72
            WHEN v ? 'userRefValue' THEN 74
            WHEN v ? 'geoPointValue' THEN 80
            WHEN v ? 'arrayValue' THEN 90
            WHEN v ? 'mapValue' THEN 100
        END::smallint
        $f$ LANGUAGE sql IMMUTABLE STRICT;

        CREATE OR REPLACE FUNCTION winche_num(v jsonb) RETURNS numeric AS $f$
        SELECT CASE
            WHEN v ? 'booleanValue' THEN CASE WHEN (v->>'booleanValue')::boolean THEN 1 ELSE 0 END
            WHEN v ? 'integerValue' THEN (v->>'integerValue')::numeric
            WHEN v ? 'doubleValue' THEN CASE (v->>'doubleValue')
                WHEN 'NaN' THEN NULL
                WHEN 'Infinity' THEN 'Infinity'::numeric
                WHEN '-Infinity' THEN '-Infinity'::numeric
                ELSE (v->>'doubleValue')::numeric END
            WHEN v ? 'timestampValue' THEN extract(epoch FROM (v->>'timestampValue')::timestamptz) * 1000000
            WHEN v ? 'geoPointValue' THEN (v->'geoPointValue'->>'latitude')::numeric
        END
        $f$ LANGUAGE sql IMMUTABLE STRICT;

        CREATE OR REPLACE FUNCTION winche_num2(v jsonb) RETURNS numeric AS $f$
        SELECT (v->'geoPointValue'->>'longitude')::numeric
        $f$ LANGUAGE sql IMMUTABLE STRICT;

        -- Strict numeric accessor for sum/average: ONLY integer/double contribute (others/missing → NULL,
        -- ignored by SUM/AVG). Unlike winche_num, 'NaN'/'Infinity' are deliberately PRESERVED (not mapped to
        -- NULL) so they propagate through aggregation unchanged — do not add NaN guards here.
        CREATE OR REPLACE FUNCTION winche_agg_num(v jsonb) RETURNS numeric AS $f$
        SELECT CASE
            WHEN v ? 'integerValue' THEN (v->>'integerValue')::numeric
            WHEN v ? 'doubleValue'  THEN (v->>'doubleValue')::numeric
        END
        $f$ LANGUAGE sql IMMUTABLE STRICT;

        -- winche_text returns text under the database's default collation.
        -- Strings are ordered by UTF-8 byte order: ORDER BY / comparison sites
        -- MUST apply COLLATE "C" (the Phase 2 compiler emits this; tests do too).
        CREATE OR REPLACE FUNCTION winche_text(v jsonb) RETURNS text AS $f$
        SELECT CASE
            WHEN v ? 'stringValue' THEN v->>'stringValue'
            WHEN v ? 'docRefValue' THEN v->>'docRefValue'
            WHEN v ? 'fileRefValue' THEN v->>'fileRefValue'
            WHEN v ? 'userRefValue' THEN v->>'userRefValue'
        END
        $f$ LANGUAGE sql IMMUTABLE STRICT;

        CREATE OR REPLACE FUNCTION winche_bytes(v jsonb) RETURNS bytea AS $f$
        SELECT decode(v->>'bytesValue', 'base64')
        $f$ LANGUAGE sql IMMUTABLE STRICT;

        -- Order-preserving 8-byte encoding of a float8 (IEEE sign-flip trick).
        CREATE OR REPLACE FUNCTION winche_f8key(d float8) RETURNS bytea AS $f$
        DECLARE b bytea := float8send(d); i int;
        BEGIN
            IF get_byte(b, 0) >= 128 THEN
                FOR i IN 0..7 LOOP b := set_byte(b, i, 255 - get_byte(b, i)); END LOOP;
            ELSE
                b := set_byte(b, 0, get_byte(b, 0) | 128);
            END IF;
            RETURN b;
        END $f$ LANGUAGE plpgsql IMMUTABLE STRICT;

        -- Escape 0x00 as 0x00 0xFF and terminate with 0x00 0x00 (prefix-safe var-length encoding).
        CREATE OR REPLACE FUNCTION winche_eskey(b bytea) RETURNS bytea AS $f$
        DECLARE r bytea := '\x'::bytea; i int;
        BEGIN
            FOR i IN 0..octet_length(b) - 1 LOOP
                IF get_byte(b, i) = 0 THEN r := r || '\x00ff'::bytea;
                ELSE r := r || substring(b FROM i + 1 FOR 1);
                END IF;
            END LOOP;
            RETURN r || '\x0000'::bytea;
        END $f$ LANGUAGE plpgsql IMMUTABLE STRICT;

        -- Recursive order-preserving key for ANY tagged value. bytea comparison of keys
        -- equals the canonical value ordering (numbers approximated via float8 beyond 2^53).
        -- SET search_path FROM CURRENT is REQUIRED: this is the only helper that calls sibling
        -- winche_* functions by unqualified name, and CREATE INDEX evaluates it on existing rows
        -- under a hardened maintenance-time search_path that excludes the install schema
        -- (CVE-2018-1058). Pinning the function's own search_path (to whatever schema the store is
        -- installed into) lets winche_rank/winche_num/winche_key resolve during the index build.
        -- Any future helper that calls another winche_* function needs the same clause.
        CREATE OR REPLACE FUNCTION winche_key(v jsonb) RETURNS bytea
        SET search_path FROM CURRENT
        AS $f$
        DECLARE
            r int := winche_rank(v);
            res bytea;
            e jsonb;
            k text;
        BEGIN
            IF r IS NULL THEN RETURN NULL; END IF;
            res := set_byte('\x00'::bytea, 0, r);
            CASE r
                WHEN 10, 29 THEN RETURN res;                          -- null, NaN: rank byte only
                WHEN 20 THEN RETURN res || set_byte('\x00'::bytea, 0,
                    CASE WHEN (v->>'booleanValue')::boolean THEN 1 ELSE 0 END);
                WHEN 30 THEN RETURN res || winche_f8key(winche_num(v)::float8);
                WHEN 40 THEN RETURN res || int8send((winche_num(v))::int8 # -9223372036854775808);
                WHEN 50 THEN RETURN res || winche_eskey(convert_to(v->>'stringValue', 'UTF8'));
                WHEN 60 THEN RETURN res || winche_eskey(decode(v->>'bytesValue', 'base64'));
                WHEN 70, 72, 74 THEN RETURN res || winche_eskey(convert_to(winche_text(v), 'UTF8'));   -- doc, file, user refs
                WHEN 80 THEN RETURN res
                    || winche_f8key((v->'geoPointValue'->>'latitude')::float8)
                    || winche_f8key((v->'geoPointValue'->>'longitude')::float8);
                WHEN 90 THEN
                    FOR e IN SELECT * FROM jsonb_array_elements(v->'arrayValue'->'values') LOOP
                        res := res || winche_key(e);
                    END LOOP;
                    RETURN res || '\x00'::bytea;
                WHEN 100 THEN
                    FOR k IN SELECT k2 FROM jsonb_object_keys(v->'mapValue'->'fields') AS t(k2)
                             ORDER BY convert_to(k2, 'UTF8') LOOP
                        res := res || winche_eskey(convert_to(k, 'UTF8'))
                                   || winche_key(v->'mapValue'->'fields'->k);
                    END LOOP;
                    RETURN res || '\x00'::bytea;
            END CASE;
        END $f$ LANGUAGE plpgsql IMMUTABLE STRICT;

        -- The winche_key of every element of an array value (NULL for anything else): what an
        -- ArrayMembership GIN index stores. Array filters match elements by winche_key, so
        -- "winche_array_keys(f) @> ARRAY[winche_key(x)]" holds exactly when f contains x.
        -- search_path pinned for the same reason as winche_key (it calls siblings during index builds).
        CREATE OR REPLACE FUNCTION winche_array_keys(v jsonb) RETURNS bytea[]
        SET search_path FROM CURRENT
        AS $f$
        SELECT CASE WHEN winche_rank(v) = 90
            THEN ARRAY(SELECT winche_key(e) FROM jsonb_array_elements(v->'arrayValue'->'values') AS t(e))
        END
        $f$ LANGUAGE sql IMMUTABLE STRICT;
        """;

    /// <summary>
    /// Durable consumers' cursors and dead letters live in Winche.Core's feed tables (feed <c>database</c>) since 11.1.0:
    /// this moves those of an older database there and drops the old tables. Run after the core tables exist.
    /// </summary>
    internal static string MoveFeedStateToCore() => $$"""
        DO $m$
        BEGIN
            IF to_regclass('winche_documents_feed_cursors') IS NOT NULL THEN
                INSERT INTO {{Winche.Core.Feeds.WincheFeedTables.Cursors}}
                    (feed, consumer, seq, txid, updated_at, failing_txid, failing_seq, failing_since, failing_attempts, failing_error)
                SELECT '{{Runtime.ChangeFeed.DatabaseFeed.Name}}', consumer, seq, txid, updated_at, failing_txid, failing_seq, failing_since, failing_attempts, failing_error
                FROM winche_documents_feed_cursors
                ON CONFLICT DO NOTHING;
                DROP TABLE winche_documents_feed_cursors;
            END IF;
            IF to_regclass('winche_documents_dead_letters') IS NOT NULL THEN
                INSERT INTO {{Winche.Core.Feeds.WincheFeedTables.DeadLetters}}
                    (feed, consumer, txid, seq, record, error, attempts, first_failed_at, dead_lettered_at)
                SELECT '{{Runtime.ChangeFeed.DatabaseFeed.Name}}', consumer, txid, seq,
                       jsonb_build_object('seq', seq, 'txid', txid::text, 'type', type, 'path', document_path,
                                          'collection', collection_path, 'version', version, 'commitTime', commit_time),
                       error, attempts, first_failed_at, dead_lettered_at
                FROM winche_documents_dead_letters
                ON CONFLICT DO NOTHING;
                DROP TABLE winche_documents_dead_letters;
            END IF;
        END
        $m$;
        """;
}
