namespace Winche.Database.SQL
{
    public sealed class TableSqlBuilder(string table = "documents")
    {
        public string Build()
        {
            return $$"""
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

                CREATE OR REPLACE FUNCTION public.parse_timestamp(val text)
                RETURNS timestamptz AS
                $$SELECT val::timestamptz$$
                LANGUAGE sql IMMUTABLE STRICT
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
        }
    }
}
