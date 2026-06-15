namespace Km.Store;

internal static class DbSchema
{
    internal const string Ddl = """
        CREATE TABLE IF NOT EXISTS beliefs (
            id                        TEXT NOT NULL PRIMARY KEY,
            entity_id                 TEXT NOT NULL,
            dimension                 TEXT NOT NULL,
            criterion                 TEXT NOT NULL,
            value                     REAL NOT NULL,
            source_tier               TEXT NOT NULL,
            confidence                REAL NOT NULL,
            freshness                 REAL NOT NULL,
            derivation                TEXT NOT NULL,
            source_signals            TEXT NOT NULL,
            version                   INTEGER NOT NULL,
            superseded_by             TEXT,
            created_at                TEXT NOT NULL,
            trace_id                  TEXT NOT NULL,
            classification_method     TEXT NOT NULL DEFAULT 'Rule',
            classification_confidence REAL,
            reasoning_summary         TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_beliefs_entity
            ON beliefs(entity_id, superseded_by);

        CREATE TABLE IF NOT EXISTS entity_indices (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            entity_id   TEXT NOT NULL,
            data        TEXT NOT NULL,
            version     INTEGER NOT NULL,
            computed_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_indices_entity
            ON entity_indices(entity_id, version DESC);

        CREATE TABLE IF NOT EXISTS postures (
            id          TEXT NOT NULL PRIMARY KEY,
            entity_id   TEXT NOT NULL,
            data        TEXT NOT NULL,
            assigned_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_postures_entity
            ON postures(entity_id, assigned_at DESC);

        CREATE TABLE IF NOT EXISTS signals (
            id          TEXT NOT NULL PRIMARY KEY,
            entity_id   TEXT NOT NULL,
            data        TEXT NOT NULL,
            received_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_signals_entity
            ON signals(entity_id, received_at);
        """;
}
