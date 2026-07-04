using Microsoft.Data.Sqlite;

namespace Km.Store.Metadata;

/// <summary>
/// IMetadataStore over SQLite. Own connection, own table, own schema — deliberately not sharing
/// <c>DbSchema.cs</c> (the belief/evidence/vendor schema) so Step 4 adds zero diff to any existing
/// Km.Store file. Wiring this to share a connection/database with <see cref="SqliteEntityStore"/>
/// for a real KYV run is a Step 5 concern.
/// </summary>
public sealed class SqliteMetadataStore : IMetadataStore, IDisposable
{
    private readonly SqliteConnection _conn;

    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS document_metadata (
            id            TEXT NOT NULL PRIMARY KEY,
            entity_id     TEXT NOT NULL,
            document_id   TEXT NOT NULL,
            document_type TEXT NOT NULL,
            field_name    TEXT NOT NULL,
            value         TEXT NOT NULL,
            derivation    TEXT NOT NULL,
            observed_at   TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_document_metadata_entity
            ON document_metadata(entity_id);
        CREATE INDEX IF NOT EXISTS ix_document_metadata_document
            ON document_metadata(document_id);
        """;

    public SqliteMetadataStore(string connectionString)
    {
        _conn = new SqliteConnection(connectionString);
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = Ddl;
        cmd.ExecuteNonQuery();
    }

    public async Task WriteAsync(DocumentMetadata metadata, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO document_metadata
                (id, entity_id, document_id, document_type, field_name, value, derivation, observed_at)
            VALUES
                ($id, $entityId, $documentId, $documentType, $fieldName, $value, $derivation, $observedAt)
            """;
        cmd.Parameters.AddWithValue("$id",           metadata.Id.ToString());
        cmd.Parameters.AddWithValue("$entityId",     metadata.EntityId.ToString());
        cmd.Parameters.AddWithValue("$documentId",   metadata.DocumentId.ToString());
        cmd.Parameters.AddWithValue("$documentType", metadata.DocumentType);
        cmd.Parameters.AddWithValue("$fieldName",    metadata.FieldName);
        cmd.Parameters.AddWithValue("$value",        metadata.Value);
        cmd.Parameters.AddWithValue("$derivation",   metadata.Derivation);
        cmd.Parameters.AddWithValue("$observedAt",   metadata.ObservedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<EntityKnowledge> GetForEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, entity_id, document_id, document_type, field_name, value, derivation, observed_at
            FROM document_metadata
            WHERE entity_id = $entityId
            """;
        cmd.Parameters.AddWithValue("$entityId", entityId.ToString());

        var results = new List<DocumentMetadata>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new DocumentMetadata(
                Id:           Guid.Parse(reader.GetString(0)),
                EntityId:     Guid.Parse(reader.GetString(1)),
                DocumentId:   Guid.Parse(reader.GetString(2)),
                DocumentType: reader.GetString(3),
                FieldName:    reader.GetString(4),
                Value:        reader.GetString(5),
                Derivation:   reader.GetString(6),
                ObservedAt:   DateTimeOffset.Parse(reader.GetString(7))));
        }

        return new EntityKnowledge(entityId, results);
    }

    public void Dispose() => _conn.Dispose();
}
