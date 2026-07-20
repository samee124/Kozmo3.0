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
            reasoning_summary         TEXT,
            claim_key                 TEXT NOT NULL DEFAULT '',
            observed_at               TEXT,
            half_life_days            INTEGER,
            valid_until               TEXT,
            provenance_evidence_id    TEXT,
            provenance_locator        TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_beliefs_entity
            ON beliefs(entity_id, superseded_by);

        CREATE TABLE IF NOT EXISTS evidence (
            evidence_id  TEXT NOT NULL PRIMARY KEY,
            vendor_id    TEXT NOT NULL,
            doc_type     TEXT NOT NULL,
            source_tier  TEXT NOT NULL,
            ref          TEXT NOT NULL,
            doc_version  INTEGER NOT NULL,
            ingested_at  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_evidence_vendor
            ON evidence(vendor_id);

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

        CREATE TABLE IF NOT EXISTS vendors (
            id                  TEXT NOT NULL PRIMARY KEY,
            canonical_name      TEXT NOT NULL,
            renewal_date        TEXT,
            created_at          TEXT NOT NULL,
            comparison_key      TEXT,
            entity_type         TEXT,
            confidence          REAL,
            flags               TEXT,
            status              TEXT,
            rebrand_map_ref     TEXT,
            acquisition_map_ref TEXT
        );

        CREATE TABLE IF NOT EXISTS vendor_aliases (
            id                TEXT NOT NULL PRIMARY KEY,
            vendor_id         TEXT NOT NULL,
            raw_name          TEXT NOT NULL,
            provenance_doc_id TEXT,
            provenance_span   TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_vendor_aliases_vendor
            ON vendor_aliases(vendor_id);

        CREATE TABLE IF NOT EXISTS checkins (
            checkin_id        TEXT NOT NULL PRIMARY KEY,
            vendor_id         TEXT NOT NULL,
            program_run_id    TEXT NOT NULL,
            kind              TEXT NOT NULL,
            question          TEXT NOT NULL,
            response_shape    TEXT NOT NULL,
            target_field      TEXT,
            owner             TEXT NOT NULL,
            status            TEXT NOT NULL DEFAULT 'OPEN',
            raised_at         TEXT NOT NULL,
            answered_at       TEXT,
            expires_at        TEXT,
            response_value    TEXT,
            paired_vendor_id  TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_checkins_vendor
            ON checkins(vendor_id);
        CREATE INDEX IF NOT EXISTS ix_checkins_status
            ON checkins(status);

        CREATE TABLE IF NOT EXISTS oauth_tokens (
            provider       TEXT NOT NULL PRIMARY KEY,
            access_token   TEXT NOT NULL,
            refresh_token  TEXT NOT NULL,
            expires_at     TEXT NOT NULL,
            user_email     TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS owner_channel_prefs (
            owner_id          TEXT NOT NULL PRIMARY KEY,
            channel           TEXT NOT NULL DEFAULT 'Email',
            slack_destination TEXT
        );
        """;
}
