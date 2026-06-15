using System.Text.Json;
using Kozmo.Contracts;
using Kozmo.Contracts.Interfaces;
using Microsoft.Data.Sqlite;

namespace Km.Store;

/// <summary>
/// IEntityStore over SQLite. Beliefs and postures are append-only; no edit/delete path.
/// Behind IEntityStore so Azure SQL drops in without caller changes.
/// </summary>
public sealed class SqliteEntityStore : IEntityStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = false
    };

    public SqliteEntityStore(string connectionString)
    {
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        EnsureSchema();
    }

    // ── Beliefs ──────────────────────────────────────────────────────────────

    public Task AppendBeliefAsync(Belief belief, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO beliefs
              (id, entity_id, dimension, criterion, value, source_tier, confidence,
               freshness, derivation, source_signals, version, superseded_by, created_at, trace_id,
               classification_method, classification_confidence, reasoning_summary)
            VALUES
              (@id, @entity_id, @dimension, @criterion, @value, @source_tier, @confidence,
               @freshness, @derivation, @source_signals, @version, @superseded_by, @created_at, @trace_id,
               @classification_method, @classification_confidence, @reasoning_summary)
            """;
        cmd.Parameters.AddWithValue("@id",             belief.Id.ToString());
        cmd.Parameters.AddWithValue("@entity_id",      belief.EntityId.ToString());
        cmd.Parameters.AddWithValue("@dimension",      belief.Dimension.ToString());
        cmd.Parameters.AddWithValue("@criterion",      belief.Criterion);
        cmd.Parameters.AddWithValue("@value",          belief.Value);
        cmd.Parameters.AddWithValue("@source_tier",    belief.SourceTier.ToString());
        cmd.Parameters.AddWithValue("@confidence",     belief.Confidence);
        cmd.Parameters.AddWithValue("@freshness",      belief.Freshness);
        cmd.Parameters.AddWithValue("@derivation",     belief.Derivation);
        cmd.Parameters.AddWithValue("@source_signals", JsonSerializer.Serialize(belief.SourceSignals));
        cmd.Parameters.AddWithValue("@version",        belief.Version);
        cmd.Parameters.AddWithValue("@superseded_by",  belief.SupersededBy.HasValue ? belief.SupersededBy.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at",     belief.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@trace_id",       belief.TraceId.ToString());
        cmd.Parameters.AddWithValue("@classification_method",     belief.ClassificationMethod.ToString());
        cmd.Parameters.AddWithValue("@classification_confidence", belief.ClassificationConfidence.HasValue ? belief.ClassificationConfidence.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@reasoning_summary",        belief.ReasoningSummary is not null ? belief.ReasoningSummary : DBNull.Value);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Belief>> GetCurrentBeliefsAsync(Guid entityId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM beliefs WHERE entity_id = @eid AND superseded_by IS NULL ORDER BY created_at";
        cmd.Parameters.AddWithValue("@eid", entityId.ToString());
        return Task.FromResult(ReadBeliefs(cmd));
    }

    public Task<IReadOnlyList<Belief>> GetBeliefHistoryAsync(Guid entityId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM beliefs WHERE entity_id = @eid ORDER BY created_at";
        cmd.Parameters.AddWithValue("@eid", entityId.ToString());
        return Task.FromResult(ReadBeliefs(cmd));
    }

    // ── Entity Index ─────────────────────────────────────────────────────────

    public Task SaveIndexAsync(EntityIndex index, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entity_indices (entity_id, data, version, computed_at)
            VALUES (@eid, @data, @version, @computed_at)
            """;
        cmd.Parameters.AddWithValue("@eid",         index.EntityId.ToString());
        cmd.Parameters.AddWithValue("@data",        JsonSerializer.Serialize(index, Json));
        cmd.Parameters.AddWithValue("@version",     index.Version);
        cmd.Parameters.AddWithValue("@computed_at", index.ComputedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<EntityIndex?> GetIndexAsync(Guid entityId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM entity_indices WHERE entity_id = @eid ORDER BY version DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@eid", entityId.ToString());
        var json = cmd.ExecuteScalar() as string;
        return Task.FromResult(json != null ? JsonSerializer.Deserialize<EntityIndex>(json, Json) : null);
    }

    public Task<IReadOnlyList<EntityIndex>> GetIndexHistoryAsync(Guid entityId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM entity_indices WHERE entity_id = @eid ORDER BY version";
        cmd.Parameters.AddWithValue("@eid", entityId.ToString());
        var results = new List<EntityIndex>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var idx = JsonSerializer.Deserialize<EntityIndex>(reader.GetString(0), Json);
            if (idx != null) results.Add(idx);
        }
        return Task.FromResult<IReadOnlyList<EntityIndex>>(results);
    }

    // ── Posture ───────────────────────────────────────────────────────────────

    public Task AppendPostureAsync(PostureAssignment posture, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO postures (id, entity_id, data, assigned_at) VALUES (@id, @eid, @data, @at)";
        cmd.Parameters.AddWithValue("@id",   posture.Id.ToString());
        cmd.Parameters.AddWithValue("@eid",  posture.EntityId.ToString());
        cmd.Parameters.AddWithValue("@data", JsonSerializer.Serialize(posture, Json));
        cmd.Parameters.AddWithValue("@at",   posture.AssignedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<PostureAssignment?> GetCurrentPostureAsync(Guid entityId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM postures WHERE entity_id = @eid ORDER BY assigned_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@eid", entityId.ToString());
        var json = cmd.ExecuteScalar() as string;
        return Task.FromResult(json != null ? JsonSerializer.Deserialize<PostureAssignment>(json, Json) : null);
    }

    // ── Signals ───────────────────────────────────────────────────────────────

    public Task AppendSignalAsync(Signal signal, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO signals (id, entity_id, data, received_at) VALUES (@id, @eid, @data, @at)";
        cmd.Parameters.AddWithValue("@id",   signal.Id.ToString());
        cmd.Parameters.AddWithValue("@eid",  signal.EntityId.ToString());
        cmd.Parameters.AddWithValue("@data", JsonSerializer.Serialize(signal, Json));
        cmd.Parameters.AddWithValue("@at",   signal.ReceivedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<Signal?> GetSignalAsync(Guid signalId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM signals WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", signalId.ToString());
        var json = cmd.ExecuteScalar() as string;
        return Task.FromResult(json != null ? JsonSerializer.Deserialize<Signal>(json, Json) : null);
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    public Task ResetAsync(CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM beliefs; DELETE FROM entity_indices; DELETE FROM postures; DELETE FROM signals;";
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static IReadOnlyList<Belief> ReadBeliefs(SqliteCommand cmd)
    {
        var results = new List<Belief>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var supersededRaw = reader["superseded_by"];
            Guid? supersededBy = supersededRaw is DBNull or null ? null
                : Guid.TryParse(supersededRaw.ToString(), out var sg) ? sg : null;

            var sourceSigs = JsonSerializer.Deserialize<List<Guid>>(reader.GetString(reader.GetOrdinal("source_signals"))) ?? [];

            var classificationConfRaw = reader["classification_confidence"];
            double? classConf = classificationConfRaw is DBNull or null ? null : Convert.ToDouble(classificationConfRaw);

            var reasoningRaw = reader["reasoning_summary"];
            string? reasoning = reasoningRaw is DBNull or null ? null : reasoningRaw.ToString();

            var classMethodRaw = reader["classification_method"];
            var classMethod = classMethodRaw is DBNull or null
                ? ClassificationMethod.Rule
                : Enum.Parse<ClassificationMethod>(classMethodRaw.ToString()!);

            results.Add(new Belief(
                Id:            Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                EntityId:      Guid.Parse(reader.GetString(reader.GetOrdinal("entity_id"))),
                Dimension:     Enum.Parse<Dimension>(reader.GetString(reader.GetOrdinal("dimension"))),
                Criterion:     reader.GetString(reader.GetOrdinal("criterion")),
                Value:         reader.GetDouble(reader.GetOrdinal("value")),
                SourceTier:    Enum.Parse<SourceTier>(reader.GetString(reader.GetOrdinal("source_tier"))),
                Confidence:    reader.GetDouble(reader.GetOrdinal("confidence")),
                Freshness:     reader.GetDouble(reader.GetOrdinal("freshness")),
                Derivation:    reader.GetString(reader.GetOrdinal("derivation")),
                SourceSignals: sourceSigs,
                Version:       reader.GetInt32(reader.GetOrdinal("version")),
                SupersededBy:  supersededBy,
                CreatedAt:     DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                TraceId:       Guid.Parse(reader.GetString(reader.GetOrdinal("trace_id")))
            ) {
                ClassificationMethod     = classMethod,
                ClassificationConfidence = classConf,
                ReasoningSummary         = reasoning
            });
        }
        return results;
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = DbSchema.Ddl;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
