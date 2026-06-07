namespace Winche.Database.Querying.Sql;

/// <summary>
/// DDL for the winche_* ordering helper functions — the SQL mirror of Values.TypeRank.
/// Firestore total order: null(10) &lt; bool(20) &lt; NaN(29) &lt; number(30) &lt; timestamp(40)
/// &lt; string(50) &lt; bytes(60) &lt; reference(70) &lt; geopoint(80) &lt; array(90) &lt; map(100).
/// Phase 1 ships scalar ordering; winche_key (arrays/maps) ships in Phase 2.
/// Reference ordering compares full path bytes (not per-segment); diverges from Firestore only for ids containing chars below '/' (0x2F).
/// </summary>
public static class SchemaSql
{
    /// <summary>
    /// Table DDL: trigger function, table, three standard indexes, and the trigger.
    /// Ported from the old TableSqlBuilder minus the parse_timestamp helper (no longer used).
    /// </summary>
    public static string TableDdl(string table) => $$"""
        CREATE OR REPLACE FUNCTION public.notify_document_change() RETURNS TRIGGER AS $$

        DECLARE payload JSONB;
        BEGIN
            IF TG_OP = 'DELETE' THEN
                payload := jsonb_build_object(
                    'type',       'removed',
                    'path',       OLD.path,
                    'collection', OLD.collection,
                    'id',         OLD.id,
                    'created_at', OLD.created_at,
                    'updated_at', OLD.updated_at,
                    'version',    OLD.version
                );
            ELSE
                payload := jsonb_build_object(
                    'type',  CASE WHEN TG_OP = 'INSERT' THEN 'added' ELSE 'modified' END,
                    'path',       NEW.path,
                    'collection', NEW.collection,
                    'id',         NEW.id,
                    'created_at', NEW.created_at,
                    'updated_at', NEW.updated_at,
                    'version',    NEW.version
                );
            END IF;

            PERFORM pg_notify('document_changes', payload::text);
            RETURN COALESCE(NEW, OLD);
        END;
        $$ LANGUAGE plpgsql
        SET search_path = public;

        CREATE TABLE IF NOT EXISTS {{table}} (
            path        TEXT        PRIMARY KEY,
            id          TEXT        NOT NULL,
            collection  TEXT        NOT NULL,
            data        JSONB       NOT NULL DEFAULT '{}',
            created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            version     BIGINT      NOT NULL DEFAULT 1
        );

        CREATE INDEX IF NOT EXISTS idx_{{table}}_id
            ON {{table}}(id);

        CREATE INDEX IF NOT EXISTS idx_{{table}}_collection_id
            ON {{table}}(collection, id ASC);

        CREATE INDEX IF NOT EXISTS idx_{{table}}_data
            ON {{table}} USING GIN(data);

        DROP TRIGGER IF EXISTS document_change_trigger ON {{table}};
        CREATE TRIGGER document_change_trigger
        AFTER INSERT OR UPDATE OR DELETE ON {{table}}
        FOR EACH ROW EXECUTE FUNCTION public.notify_document_change();
        """;

    public static string HelperFunctions(string schema = "public") => $"""
        CREATE OR REPLACE FUNCTION {schema}.winche_rank(v jsonb) RETURNS smallint AS $f$
        SELECT CASE
            WHEN v ? 'nullValue' THEN 10
            WHEN v ? 'booleanValue' THEN 20
            WHEN v ? 'doubleValue' AND (v->>'doubleValue') = 'NaN' THEN 29
            WHEN v ? 'integerValue' OR v ? 'doubleValue' THEN 30
            WHEN v ? 'timestampValue' THEN 40
            WHEN v ? 'stringValue' THEN 50
            WHEN v ? 'bytesValue' THEN 60
            WHEN v ? 'referenceValue' THEN 70
            WHEN v ? 'geoPointValue' THEN 80
            WHEN v ? 'arrayValue' THEN 90
            WHEN v ? 'mapValue' THEN 100
        END::smallint
        $f$ LANGUAGE sql IMMUTABLE STRICT;

        CREATE OR REPLACE FUNCTION {schema}.winche_num(v jsonb) RETURNS numeric AS $f$
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

        CREATE OR REPLACE FUNCTION {schema}.winche_num2(v jsonb) RETURNS numeric AS $f$
        SELECT (v->'geoPointValue'->>'longitude')::numeric
        $f$ LANGUAGE sql IMMUTABLE STRICT;

        -- winche_text returns text under the database's default collation.
        -- Firestore orders strings by UTF-8 byte order: ORDER BY / comparison sites
        -- MUST apply COLLATE "C" (the Phase 2 compiler emits this; tests do too).
        CREATE OR REPLACE FUNCTION {schema}.winche_text(v jsonb) RETURNS text AS $f$
        SELECT CASE
            WHEN v ? 'stringValue' THEN v->>'stringValue'
            WHEN v ? 'referenceValue' THEN v->>'referenceValue'
        END
        $f$ LANGUAGE sql IMMUTABLE STRICT;

        CREATE OR REPLACE FUNCTION {schema}.winche_bytes(v jsonb) RETURNS bytea AS $f$
        SELECT decode(v->>'bytesValue', 'base64')
        $f$ LANGUAGE sql IMMUTABLE STRICT;

        -- Order-preserving 8-byte encoding of a float8 (IEEE sign-flip trick).
        CREATE OR REPLACE FUNCTION {schema}.winche_f8key(d float8) RETURNS bytea AS $f$
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
        CREATE OR REPLACE FUNCTION {schema}.winche_eskey(b bytea) RETURNS bytea AS $f$
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
        -- equals Firestore value ordering (numbers approximated via float8 beyond 2^53).
        CREATE OR REPLACE FUNCTION {schema}.winche_key(v jsonb) RETURNS bytea AS $f$
        DECLARE
            r int := {schema}.winche_rank(v);
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
                WHEN 30 THEN RETURN res || {schema}.winche_f8key({schema}.winche_num(v)::float8);
                WHEN 40 THEN RETURN res || int8send(({schema}.winche_num(v))::int8 # -9223372036854775808);
                WHEN 50 THEN RETURN res || {schema}.winche_eskey(convert_to(v->>'stringValue', 'UTF8'));
                WHEN 60 THEN RETURN res || {schema}.winche_eskey(decode(v->>'bytesValue', 'base64'));
                WHEN 70 THEN RETURN res || {schema}.winche_eskey(convert_to(v->>'referenceValue', 'UTF8'));
                WHEN 80 THEN RETURN res
                    || {schema}.winche_f8key((v->'geoPointValue'->>'latitude')::float8)
                    || {schema}.winche_f8key((v->'geoPointValue'->>'longitude')::float8);
                WHEN 90 THEN
                    FOR e IN SELECT * FROM jsonb_array_elements(v->'arrayValue'->'values') LOOP
                        res := res || {schema}.winche_key(e);
                    END LOOP;
                    RETURN res || '\x00'::bytea;
                WHEN 100 THEN
                    FOR k IN SELECT k2 FROM jsonb_object_keys(v->'mapValue'->'fields') AS t(k2)
                             ORDER BY convert_to(k2, 'UTF8') LOOP
                        res := res || {schema}.winche_eskey(convert_to(k, 'UTF8'))
                                   || {schema}.winche_key(v->'mapValue'->'fields'->k);
                    END LOOP;
                    RETURN res || '\x00'::bytea;
            END CASE;
        END $f$ LANGUAGE plpgsql IMMUTABLE STRICT;
        """;
}
