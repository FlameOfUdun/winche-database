-- One-time upgrade to the collection-ID indexing + rename release.
--
-- NOTE: This migration is normally applied AUTOMATICALLY on app startup —
-- SchemaManager.EnsureCreatedAsync runs SchemaSql.MigrationDdl() (an idempotent, guarded
-- equivalent of this script) before the rest of the schema DDL. This standalone script is kept
-- for reference and for operators who prefer to migrate manually/out-of-band (e.g. during a
-- maintenance window, before deploying the new app version). It is safe to run once, in a
-- transaction. Old `*`-pattern indexes whose definitions no longer exist should be dropped
-- manually.
BEGIN;

-- documents: rename columns + indexes, add collection_id (app-computed last segment of collection_path)
ALTER TABLE winche_documents RENAME COLUMN path TO document_path;
ALTER TABLE winche_documents RENAME COLUMN id TO document_id;
ALTER TABLE winche_documents RENAME COLUMN collection TO collection_path;
ALTER INDEX idx_winche_documents_id RENAME TO idx_winche_documents_document_id;
ALTER INDEX idx_winche_documents_collection_id RENAME TO idx_winche_documents_collection_docid;
ALTER TABLE winche_documents ADD COLUMN collection_id TEXT;
UPDATE winche_documents SET collection_id = (regexp_match(collection_path, '[^/]+$'))[1];
ALTER TABLE winche_documents ALTER COLUMN collection_id SET NOT NULL;

-- change-feed tables: rename tables, then columns + index + trigger
ALTER TABLE winche_changes RENAME TO winche_documents_changes;
ALTER TABLE winche_feed_cursors RENAME TO winche_documents_feed_cursors;
ALTER TABLE winche_documents_changes RENAME COLUMN path TO document_path;
ALTER TABLE winche_documents_changes RENAME COLUMN collection TO collection_path;
ALTER INDEX idx_winche_changes_commit_time RENAME TO idx_winche_documents_changes_commit_time;
ALTER TRIGGER winche_changes_trigger ON winche_documents_changes RENAME TO winche_documents_changes_trigger;

COMMIT;

-- The notify trigger function and channel are redefined idempotently by the app on startup
-- (SchemaSql.ChangesDdl uses CREATE OR REPLACE FUNCTION + DROP/CREATE TRIGGER, and the
-- function now emits pg_notify('winche_documents_changes', ...)).
