using Microsoft.Data.Sqlite;

namespace Po.VendorCall;

/// <summary>
/// SQLite-backed implementation of IVendorCallRunStore.
/// Creates the vendor_call_runs table on first use (CREATE TABLE IF NOT EXISTS).
/// Uses INSERT OR REPLACE so SaveAsync is always an upsert.
/// Dispose() closes the underlying SQLite connection.
/// </summary>
public sealed class SqliteVendorCallRunStore : IVendorCallRunStore, IDisposable
{
    private readonly SqliteConnection _conn;

    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS vendor_call_runs (
            id                          TEXT NOT NULL PRIMARY KEY,
            event_id                    TEXT NOT NULL,
            ical_uid                    TEXT,
            join_web_url                TEXT,
            vendor_id                   TEXT NOT NULL,
            vendor_name                 TEXT NOT NULL,
            meeting_subject             TEXT NOT NULL,
            start_utc                   TEXT NOT NULL,
            end_utc                     TEXT NOT NULL,
            signed_in_user_principal_id TEXT NOT NULL,
            status                      TEXT NOT NULL,
            pre_check_in_sent_at        TEXT,
            briefing_sent_at            TEXT,
            pre_checkpoint_id           TEXT,
            online_meeting_id           TEXT,
            transcript_id               TEXT,
            transcript_fetched_at       TEXT,
            transcript_analyzed_at      TEXT,
            post_summary_sent_at        TEXT,
            post_checkpoint_id          TEXT,
            review_token                TEXT,
            review_token_expires_at     TEXT,
            summary_json                TEXT,
            created_at                  TEXT NOT NULL,
            updated_at                  TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_vcr_event_id  ON vendor_call_runs(event_id);
        CREATE INDEX        IF NOT EXISTS ix_vcr_status    ON vendor_call_runs(status);
        CREATE INDEX        IF NOT EXISTS ix_vcr_vendor_id ON vendor_call_runs(vendor_id);
        """;

    public SqliteVendorCallRunStore(string connectionString)
    {
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = Ddl;
        cmd.ExecuteNonQuery();
        MigrateIfNeeded();
    }

    /// <summary>
    /// Adds columns introduced after the initial schema so existing DBs are upgraded
    /// automatically on first open.
    /// </summary>
    private void MigrateIfNeeded()
    {
        // Read existing columns
        using var pragma = _conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(vendor_call_runs)";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var r = pragma.ExecuteReader())
            while (r.Read()) columns.Add(r.GetString(1));

        if (!columns.Contains("review_token"))
        {
            using var alter = _conn.CreateCommand();
            alter.CommandText = "ALTER TABLE vendor_call_runs ADD COLUMN review_token TEXT";
            alter.ExecuteNonQuery();
        }

        if (!columns.Contains("review_token_expires_at"))
        {
            using var alter = _conn.CreateCommand();
            alter.CommandText = "ALTER TABLE vendor_call_runs ADD COLUMN review_token_expires_at TEXT";
            alter.ExecuteNonQuery();
        }

        if (!columns.Contains("summary_json"))
        {
            using var alter = _conn.CreateCommand();
            alter.CommandText = "ALTER TABLE vendor_call_runs ADD COLUMN summary_json TEXT";
            alter.ExecuteNonQuery();
        }
    }

    // ── IVendorCallRunStore ───────────────────────────────────────────────────

    public Task<VendorCallRun?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM vendor_call_runs WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        return Task.FromResult(ReadOne(cmd));
    }

    public Task<VendorCallRun?> GetByEventIdAsync(string eventId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM vendor_call_runs WHERE event_id = @eid";
        cmd.Parameters.AddWithValue("@eid", eventId);
        return Task.FromResult(ReadOne(cmd));
    }

    public Task<IReadOnlyList<VendorCallRun>> GetByStatusAsync(VendorCallStatus status, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM vendor_call_runs WHERE status = @s ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("@s", status.ToString());
        return Task.FromResult(ReadMany(cmd));
    }

    public Task<IReadOnlyList<VendorCallRun>> GetByVendorIdAsync(Guid vendorId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM vendor_call_runs WHERE vendor_id = @vid ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("@vid", vendorId.ToString());
        return Task.FromResult(ReadMany(cmd));
    }

    public Task SaveAsync(VendorCallRun run, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO vendor_call_runs (
                id, event_id, ical_uid, join_web_url, vendor_id, vendor_name,
                meeting_subject, start_utc, end_utc, signed_in_user_principal_id,
                status, pre_check_in_sent_at, briefing_sent_at, pre_checkpoint_id,
                online_meeting_id, transcript_id, transcript_fetched_at,
                transcript_analyzed_at, post_summary_sent_at, post_checkpoint_id,
                review_token, review_token_expires_at, summary_json, created_at, updated_at
            ) VALUES (
                @id, @eid, @ical, @jwu, @vid, @vn,
                @subj, @start, @end, @sipid,
                @status, @pci, @bi, @pchk,
                @omid, @tid, @tfa, @taa, @psa, @pchk2,
                @rt, @rtea, @sj, @cat, @uat
            )
            """;

        cmd.Parameters.AddWithValue("@id",    run.Id.ToString());
        cmd.Parameters.AddWithValue("@eid",   run.EventId);
        cmd.Parameters.AddWithValue("@ical",  (object?)run.ICalUid                            ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@jwu",   (object?)run.JoinWebUrl                         ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@vid",   run.VendorId.ToString());
        cmd.Parameters.AddWithValue("@vn",    run.VendorName);
        cmd.Parameters.AddWithValue("@subj",  run.MeetingSubject);
        cmd.Parameters.AddWithValue("@start", run.StartUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@end",   run.EndUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@sipid", run.SignedInUserPrincipalId);
        cmd.Parameters.AddWithValue("@status",run.Status.ToString());
        cmd.Parameters.AddWithValue("@pci",   (object?)run.PreCheckInSentAt?.ToString("O")    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bi",    (object?)run.BriefingSentAt?.ToString("O")      ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pchk",  (object?)run.PreCheckpointId                    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@omid",  (object?)run.OnlineMeetingId                    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tid",   (object?)run.TranscriptId                       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tfa",   (object?)run.TranscriptFetchedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@taa",   (object?)run.TranscriptAnalyzedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@psa",   (object?)run.PostSummarySentAt?.ToString("O")   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pchk2", (object?)run.PostCheckpointId                   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rt",   (object?)run.ReviewToken                            ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rtea", (object?)run.ReviewTokenExpiresAt?.ToString("O")   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sj",   (object?)run.SummaryJson                           ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cat",  run.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@uat",   run.UpdatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public void Dispose() => _conn.Dispose();

    // ── Reading helpers ───────────────────────────────────────────────────────

    private static VendorCallRun? ReadOne(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapRow(r) : null;
    }

    private static IReadOnlyList<VendorCallRun> ReadMany(SqliteCommand cmd)
    {
        using var r   = cmd.ExecuteReader();
        var       list = new List<VendorCallRun>();
        while (r.Read()) list.Add(MapRow(r));
        return list;
    }

    private static VendorCallRun MapRow(SqliteDataReader r) => new()
    {
        Id                      = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
        EventId                 = r.GetString(r.GetOrdinal("event_id")),
        ICalUid                 = NullStr(r, "ical_uid"),
        JoinWebUrl              = NullStr(r, "join_web_url"),
        VendorId                = Guid.Parse(r.GetString(r.GetOrdinal("vendor_id"))),
        VendorName              = r.GetString(r.GetOrdinal("vendor_name")),
        MeetingSubject          = r.GetString(r.GetOrdinal("meeting_subject")),
        StartUtc                = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("start_utc"))),
        EndUtc                  = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("end_utc"))),
        SignedInUserPrincipalId = r.GetString(r.GetOrdinal("signed_in_user_principal_id")),
        Status                  = Enum.Parse<VendorCallStatus>(r.GetString(r.GetOrdinal("status"))),
        PreCheckInSentAt        = NullDto(r, "pre_check_in_sent_at"),
        BriefingSentAt          = NullDto(r, "briefing_sent_at"),
        PreCheckpointId         = NullStr(r, "pre_checkpoint_id"),
        OnlineMeetingId         = NullStr(r, "online_meeting_id"),
        TranscriptId            = NullStr(r, "transcript_id"),
        TranscriptFetchedAt     = NullDto(r, "transcript_fetched_at"),
        TranscriptAnalyzedAt    = NullDto(r, "transcript_analyzed_at"),
        PostSummarySentAt       = NullDto(r, "post_summary_sent_at"),
        PostCheckpointId        = NullStr(r, "post_checkpoint_id"),
        ReviewToken             = NullStr(r, "review_token"),
        ReviewTokenExpiresAt    = NullDto(r, "review_token_expires_at"),
        SummaryJson             = NullStr(r, "summary_json"),
        CreatedAt               = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
        UpdatedAt               = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("updated_at")))
    };

    private static string? NullStr(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
    }

    private static DateTimeOffset? NullDto(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : DateTimeOffset.Parse(r.GetString(ord));
    }
}
