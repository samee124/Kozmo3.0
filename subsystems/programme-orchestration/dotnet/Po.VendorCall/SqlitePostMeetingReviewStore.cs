using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Po.VendorCall;

/// <summary>
/// SQLite-backed implementation of IPostMeetingReviewStore.
/// One row per VendorCallRun (keyed by run_id).
/// Corrections are stored as a JSON array in the corrections_json column.
/// </summary>
public sealed class SqlitePostMeetingReviewStore : IPostMeetingReviewStore, IDisposable
{
    private readonly SqliteConnection _conn;

    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS post_meeting_reviews (
            run_id               TEXT NOT NULL PRIMARY KEY,
            submitted_at         TEXT NOT NULL,
            summary_accurate     INTEGER NOT NULL,
            corrections_json     TEXT NOT NULL,
            additions            TEXT,
            promote_to_evidence  INTEGER NOT NULL
        );
        """;

    public SqlitePostMeetingReviewStore(string connectionString)
    {
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = Ddl;
        cmd.ExecuteNonQuery();
    }

    public Task SaveAsync(PostMeetingReviewSubmission submission, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO post_meeting_reviews (
                run_id, submitted_at, summary_accurate, corrections_json,
                additions, promote_to_evidence
            ) VALUES (
                @rid, @sat, @acc, @cj, @add, @pte
            )
            """;

        cmd.Parameters.AddWithValue("@rid", submission.RunId.ToString());
        cmd.Parameters.AddWithValue("@sat", submission.SubmittedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@acc", submission.SummaryAccurate ? 1 : 0);
        cmd.Parameters.AddWithValue("@cj",  JsonSerializer.Serialize(submission.Corrections));
        cmd.Parameters.AddWithValue("@add", (object?)submission.Additions ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pte", submission.PromoteToEvidence ? 1 : 0);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<PostMeetingReviewSubmission?> GetByRunIdAsync(Guid runId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM post_meeting_reviews WHERE run_id = @rid";
        cmd.Parameters.AddWithValue("@rid", runId.ToString());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Task.FromResult<PostMeetingReviewSubmission?>(null);

        var corrections = JsonSerializer.Deserialize<List<ItemCorrection>>(
            r.GetString(r.GetOrdinal("corrections_json"))) ?? [];

        var sub = new PostMeetingReviewSubmission(
            RunId:             Guid.Parse(r.GetString(r.GetOrdinal("run_id"))),
            SubmittedAt:       DateTimeOffset.Parse(r.GetString(r.GetOrdinal("submitted_at"))),
            SummaryAccurate:   r.GetInt32(r.GetOrdinal("summary_accurate")) == 1,
            Corrections:       corrections,
            Additions:         NullStr(r, "additions"),
            PromoteToEvidence: r.GetInt32(r.GetOrdinal("promote_to_evidence")) == 1);

        return Task.FromResult<PostMeetingReviewSubmission?>(sub);
    }

    public void Dispose() => _conn.Dispose();

    private static string? NullStr(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
    }
}
