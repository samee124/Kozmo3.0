using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Po.VendorCall;

/// <summary>
/// SQLite-backed IReviewCheckpointStore.
/// Follows the same connection-per-instance + CREATE TABLE IF NOT EXISTS pattern as
/// SqliteVendorCallRunStore. SourceReferenceIds is stored as a JSON array.
/// </summary>
public sealed class SqliteReviewCheckpointStore : IReviewCheckpointStore, IDisposable
{
    private readonly SqliteConnection _conn;

    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS review_checkpoints (
            id                       TEXT NOT NULL PRIMARY KEY,
            vendor_id                TEXT NOT NULL,
            vendor_call_run_id       TEXT,
            kind                     TEXT NOT NULL,
            created_at_utc           TEXT NOT NULL,
            status                   TEXT NOT NULL,
            movement                 TEXT NOT NULL,
            confidence               TEXT NOT NULL,
            q1_answer                TEXT NOT NULL,
            q2_answer                TEXT NOT NULL,
            q3_answer                TEXT NOT NULL,
            q4_answer                TEXT NOT NULL,
            q5_answer                TEXT NOT NULL,
            open_commitment_count    INTEGER NOT NULL,
            overdue_commitment_count INTEGER NOT NULL,
            unresolved_signal_count  INTEGER NOT NULL,
            source_reference_ids     TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_rcp_vendor_id   ON review_checkpoints(vendor_id);
        CREATE INDEX IF NOT EXISTS ix_rcp_created_at  ON review_checkpoints(created_at_utc);
        """;

    public SqliteReviewCheckpointStore(string connectionString)
    {
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = Ddl;
        cmd.ExecuteNonQuery();
    }

    public Task<ReviewCheckpoint?> GetLatestAsync(Guid vendorId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT * FROM review_checkpoints WHERE vendor_id = @vid " +
            "ORDER BY created_at_utc DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@vid", vendorId.ToString());
        return Task.FromResult(ReadOne(cmd));
    }

    public Task<IReadOnlyList<ReviewCheckpoint>> GetHistoryAsync(
        Guid vendorId, int maxCount, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT * FROM review_checkpoints WHERE vendor_id = @vid " +
            "ORDER BY created_at_utc DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@vid", vendorId.ToString());
        cmd.Parameters.AddWithValue("@n",   maxCount);
        return Task.FromResult(ReadMany(cmd));
    }

    public Task SaveAsync(ReviewCheckpoint cp, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO review_checkpoints (
                id, vendor_id, vendor_call_run_id, kind, created_at_utc,
                status, movement, confidence,
                q1_answer, q2_answer, q3_answer, q4_answer, q5_answer,
                open_commitment_count, overdue_commitment_count, unresolved_signal_count,
                source_reference_ids
            ) VALUES (
                @id, @vid, @vcrid, @kind, @cat,
                @status, @movement, @confidence,
                @q1, @q2, @q3, @q4, @q5,
                @occ, @odcc, @usc,
                @srids
            )
            """;

        cmd.Parameters.AddWithValue("@id",       cp.Id.ToString());
        cmd.Parameters.AddWithValue("@vid",      cp.VendorId.ToString());
        cmd.Parameters.AddWithValue("@vcrid",    (object?)cp.VendorCallRunId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@kind",     cp.Kind.ToString());
        cmd.Parameters.AddWithValue("@cat",      cp.CreatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@status",   cp.Status.ToString());
        cmd.Parameters.AddWithValue("@movement", cp.Movement.ToString());
        cmd.Parameters.AddWithValue("@confidence", cp.Confidence.ToString());
        cmd.Parameters.AddWithValue("@q1",       cp.Q1Answer);
        cmd.Parameters.AddWithValue("@q2",       cp.Q2Answer);
        cmd.Parameters.AddWithValue("@q3",       cp.Q3Answer);
        cmd.Parameters.AddWithValue("@q4",       cp.Q4Answer);
        cmd.Parameters.AddWithValue("@q5",       cp.Q5Answer);
        cmd.Parameters.AddWithValue("@occ",      cp.OpenCommitmentCount);
        cmd.Parameters.AddWithValue("@odcc",     cp.OverdueCommitmentCount);
        cmd.Parameters.AddWithValue("@usc",      cp.UnresolvedSignalCount);
        cmd.Parameters.AddWithValue("@srids",    JsonSerializer.Serialize(cp.SourceReferenceIds));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public void Dispose() => _conn.Dispose();

    private static ReviewCheckpoint? ReadOne(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapRow(r) : null;
    }

    private static IReadOnlyList<ReviewCheckpoint> ReadMany(SqliteCommand cmd)
    {
        using var r    = cmd.ExecuteReader();
        var       list = new List<ReviewCheckpoint>();
        while (r.Read()) list.Add(MapRow(r));
        return list;
    }

    private static ReviewCheckpoint MapRow(SqliteDataReader r)
    {
        var sridsJson = r.GetString(r.GetOrdinal("source_reference_ids"));
        var srids     = JsonSerializer.Deserialize<List<string>>(sridsJson) ?? [];

        return new ReviewCheckpoint(
            Id:                     Guid.Parse(r.GetString(r.GetOrdinal("id"))),
            VendorId:               Guid.Parse(r.GetString(r.GetOrdinal("vendor_id"))),
            VendorCallRunId:        NullGuid(r, "vendor_call_run_id"),
            Kind:                   Enum.Parse<CheckpointKind>(r.GetString(r.GetOrdinal("kind"))),
            CreatedAtUtc:           DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at_utc"))),
            Status:                 Enum.Parse<ReviewStatus>(r.GetString(r.GetOrdinal("status"))),
            Movement:               Enum.Parse<ReviewMovement>(r.GetString(r.GetOrdinal("movement"))),
            Confidence:             Enum.Parse<ReviewConfidence>(r.GetString(r.GetOrdinal("confidence"))),
            Q1Answer:               r.GetString(r.GetOrdinal("q1_answer")),
            Q2Answer:               r.GetString(r.GetOrdinal("q2_answer")),
            Q3Answer:               r.GetString(r.GetOrdinal("q3_answer")),
            Q4Answer:               r.GetString(r.GetOrdinal("q4_answer")),
            Q5Answer:               r.GetString(r.GetOrdinal("q5_answer")),
            OpenCommitmentCount:    r.GetInt32(r.GetOrdinal("open_commitment_count")),
            OverdueCommitmentCount: r.GetInt32(r.GetOrdinal("overdue_commitment_count")),
            UnresolvedSignalCount:  r.GetInt32(r.GetOrdinal("unresolved_signal_count")),
            SourceReferenceIds:     srids);
    }

    private static Guid? NullGuid(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : Guid.Parse(r.GetString(ord));
    }
}
