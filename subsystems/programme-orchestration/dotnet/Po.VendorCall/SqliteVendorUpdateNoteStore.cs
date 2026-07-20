using Microsoft.Data.Sqlite;

namespace Po.VendorCall;

public sealed class SqliteVendorUpdateNoteStore : IVendorUpdateNoteStore
{
    private readonly string _connectionString;

    public SqliteVendorUpdateNoteStore(string connectionString)
    {
        _connectionString = connectionString;
        EnsureTable();
    }

    private void EnsureTable()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS vendor_update_notes (
                id                TEXT PRIMARY KEY,
                vendor_id         TEXT NOT NULL,
                vendor_call_run_id TEXT,
                note_text         TEXT NOT NULL,
                submitted_by_upn  TEXT NOT NULL,
                submitted_at_utc  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_vun_vendor ON vendor_update_notes(vendor_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task SaveAsync(VendorUpdateNote note, CancellationToken ct)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO vendor_update_notes
                (id, vendor_id, vendor_call_run_id, note_text, submitted_by_upn, submitted_at_utc)
            VALUES
                (@id, @vendorId, @runId, @noteText, @upn, @at)
            """;
        cmd.Parameters.AddWithValue("@id",       note.Id.ToString());
        cmd.Parameters.AddWithValue("@vendorId", note.VendorId.ToString());
        cmd.Parameters.AddWithValue("@runId",    (object?)note.VendorCallRunId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@noteText", note.NoteText);
        cmd.Parameters.AddWithValue("@upn",      note.SubmittedByUpn);
        cmd.Parameters.AddWithValue("@at",       note.SubmittedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<VendorUpdateNote>> GetForVendorAsync(
        Guid vendorId, CancellationToken ct)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, vendor_id, vendor_call_run_id, note_text, submitted_by_upn, submitted_at_utc
            FROM   vendor_update_notes
            WHERE  vendor_id = @vendorId
            ORDER  BY submitted_at_utc ASC
            """;
        cmd.Parameters.AddWithValue("@vendorId", vendorId.ToString());

        var result = new List<VendorUpdateNote>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new VendorUpdateNote(
                Id:              Guid.Parse(reader.GetString(0)),
                VendorId:        Guid.Parse(reader.GetString(1)),
                VendorCallRunId: reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                NoteText:        reader.GetString(3),
                SubmittedByUpn:  reader.GetString(4),
                SubmittedAtUtc:  DateTimeOffset.Parse(reader.GetString(5))));
        }
        return result;
    }
}
