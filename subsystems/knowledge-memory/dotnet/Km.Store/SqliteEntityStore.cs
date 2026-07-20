using System.Linq;
using System.Text.Json;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Contracts.Interfaces;
using Microsoft.Data.Sqlite;

namespace Km.Store;

/// <summary>
/// IEntityStore over SQLite. Beliefs and postures are append-only; no edit/delete path.
/// Behind IEntityStore so Azure SQL drops in without caller changes.
/// </summary>
public sealed class SqliteEntityStore : IEntityStore, IRegistryStore, ICheckInRowStore, IOwnerChannelPrefStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SaasProfile?     _profile;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = false
    };

    public SqliteEntityStore(string connectionString, SaasProfile? profile = null)
    {
        _conn    = new SqliteConnection(connectionString);
        _profile = profile;
        _conn.Open();
        EnsureSchema();
    }

    // ── Beliefs ──────────────────────────────────────────────────────────────

    public Task AppendBeliefAsync(Belief belief, CancellationToken ct = default)
    {
        // Vendor file write-path enforcement (§2 + §7).
        // Keyed on non-empty ClaimKey so the signal pipeline (ClaimKey="") is unaffected.
        var priorsToSupersede = new List<Guid>();
        if (!string.IsNullOrEmpty(belief.ClaimKey))
        {
            // §2: clamp confidence to tier ceiling — ceiling values come from source_tiers config
            var ceiling = _profile is not null
                       && _profile.SourceTiers.TryGetValue(belief.SourceTier.ToString(), out var tc)
                        ? tc.Ceiling
                        : double.MaxValue;
            var clamped = Math.Min(belief.Confidence, ceiling);
            if (clamped != belief.Confidence)
                belief = belief with { Confidence = clamped };

            // §7: append-and-supersede keyed on (entity_id, claim_key). Tier is compared per prior,
            // NOT winner-take-all across the whole slot — the three outcomes are deliberately
            // asymmetric, not a gap to close:
            //   • new STRONGER than a prior  → that prior is corrected/upgraded: supersede it
            //     (a Quote superseded by the signed Contract — T13/WriteService_Supersession_
            //     PrimarySuperseedsVerified).
            //   • new WEAKER than a prior    → leave BOTH current, untouched. This is NOT the
            //     "new" case superseding, and it is NOT the reverse either: a stronger belief
            //     already on record and a weaker one arriving later is a genuine cross-source
            //     disagreement (or, for scored claim keys, an independent corroborating
            //     measurement) — ContradictionDetector.DetectCrossSource and RubricModule's
            //     weighted fusion both depend on seeing every current belief in the slot to do
            //     their job (MetaCognitionTests T11/T12, QTests.VendorFixtures_SpreadHolds).
            //     Silently picking a tier winner here would make disagreements invisible.
            //   • new SAME tier as a prior   → a true collision over the same slot (e.g. two
            //     Primary documents), which the two rules above have no opinion on. Resolved via
            //     the interim deterministic tiebreak below — no test currently covers this case
            //     the way T11/T13 cover cross-tier, so there's no existing contract to preserve.
            if (belief.SupersededBy == null)
            {
                var priors = FindActivePriorsInSlot(belief.EntityId, belief.ClaimKey);
                if (priors.Count > 0)
                {
                    var newRank    = VendorFileTierRank(belief.SourceTier);
                    var tiedPriors = new List<(Guid id, SourceTier tier, DateTimeOffset? observedAt, string derivation, double value)>();
                    foreach (var p in priors)
                    {
                        var priorRank = VendorFileTierRank(p.tier);
                        if (newRank > priorRank) priorsToSupersede.Add(p.id);   // new corrects/upgrades prior
                        else if (newRank == priorRank) tiedPriors.Add(p);       // same-tier collision
                        // newRank < priorRank: leave both untouched — see comment above
                    }

                    if (tiedPriors.Count > 0)
                    {
                        var winner = new SupersessionCandidate(
                            null, belief.ObservedAt, belief.Derivation, belief.Value);
                        foreach (var p in tiedPriors)
                        {
                            var candidate = new SupersessionCandidate(p.id, p.observedAt, p.derivation, p.value);
                            if (IsStrongerForSlot(candidate, winner)) winner = candidate;
                        }

                        if (winner.PriorId is Guid winnerId)
                        {
                            belief = belief with { SupersededBy = winnerId };
                            foreach (var p in tiedPriors)
                                if (p.id != winnerId) priorsToSupersede.Add(p.id);
                        }
                        else
                        {
                            foreach (var p in tiedPriors) priorsToSupersede.Add(p.id);
                        }
                    }
                }
            }
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO beliefs
              (id, entity_id, dimension, criterion, value, source_tier, confidence,
               freshness, derivation, source_signals, version, superseded_by, created_at, trace_id,
               classification_method, classification_confidence, reasoning_summary,
               claim_key, observed_at, half_life_days, valid_until,
               provenance_evidence_id, provenance_locator)
            VALUES
              (@id, @entity_id, @dimension, @criterion, @value, @source_tier, @confidence,
               @freshness, @derivation, @source_signals, @version, @superseded_by, @created_at, @trace_id,
               @classification_method, @classification_confidence, @reasoning_summary,
               @claim_key, @observed_at, @half_life_days, @valid_until,
               @provenance_evidence_id, @provenance_locator)
            """;
        cmd.Parameters.AddWithValue("@id",             belief.Id.ToString());
        cmd.Parameters.AddWithValue("@entity_id",      belief.EntityId.ToString());
        cmd.Parameters.AddWithValue("@dimension",      belief.Dimension?.ToString() ?? "");
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
        cmd.Parameters.AddWithValue("@claim_key",                belief.ClaimKey);
        cmd.Parameters.AddWithValue("@observed_at",              belief.ObservedAt.HasValue ? belief.ObservedAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("@half_life_days",           belief.HalfLifeDays.HasValue ? belief.HalfLifeDays.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@valid_until",              belief.ValidUntil.HasValue ? belief.ValidUntil.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("@provenance_evidence_id",   belief.Provenance?.EvidenceId.ToString() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@provenance_locator",       belief.Provenance?.Locator ?? (object)DBNull.Value);
        if (priorsToSupersede.Count > 0)
        {
            // Atomic: INSERT new belief + UPDATE all prior superseded_by in a single transaction.
            // Using a single UPDATE ... WHERE id IN (...) for efficiency.
            using var txn = _conn.BeginTransaction();
            cmd.Transaction = txn;
            cmd.ExecuteNonQuery();

            var placeholders = string.Join(",", priorsToSupersede.Select((_, i) => $"@p{i}"));
            using var upd = _conn.CreateCommand();
            upd.Transaction = txn;
            upd.CommandText = $"UPDATE beliefs SET superseded_by = @new_id WHERE id IN ({placeholders})";
            upd.Parameters.AddWithValue("@new_id", belief.Id.ToString());
            for (var i = 0; i < priorsToSupersede.Count; i++)
                upd.Parameters.AddWithValue($"@p{i}", priorsToSupersede[i].ToString());
            upd.ExecuteNonQuery();

            txn.Commit();
        }
        else
        {
            cmd.ExecuteNonQuery();
        }

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
        cmd.CommandText = "SELECT data FROM postures WHERE entity_id = @eid ORDER BY assigned_at DESC, rowid DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@eid", entityId.ToString());
        var json = cmd.ExecuteScalar() as string;
        return Task.FromResult(json != null ? JsonSerializer.Deserialize<PostureAssignment>(json, Json) : null);
    }

    // ── Signals ───────────────────────────────────────────────────────────────

    public Task AppendSignalAsync(Signal signal, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO signals (id, entity_id, data, received_at) VALUES (@id, @eid, @data, @at)";
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

    // ── Posture history ───────────────────────────────────────────────────────

    public Task<IReadOnlyList<PostureAssignment>> GetPostureHistoryAsync(Guid entityId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM postures WHERE entity_id = @eid ORDER BY assigned_at";
        cmd.Parameters.AddWithValue("@eid", entityId.ToString());
        var results = new List<PostureAssignment>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var p = JsonSerializer.Deserialize<PostureAssignment>(reader.GetString(0), Json);
            if (p != null) results.Add(p);
        }
        return Task.FromResult<IReadOnlyList<PostureAssignment>>(results);
    }

    // ── Signal history for entity ─────────────────────────────────────────────

    public Task<IReadOnlyList<Signal>> GetSignalsForEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM signals WHERE entity_id = @eid ORDER BY received_at";
        cmd.Parameters.AddWithValue("@eid", entityId.ToString());
        var results = new List<Signal>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var s = JsonSerializer.Deserialize<Signal>(reader.GetString(0), Json);
            if (s != null) results.Add(s);
        }
        return Task.FromResult<IReadOnlyList<Signal>>(results);
    }

    // ── Evidence ──────────────────────────────────────────────────────────────

    public Task AppendEvidenceAsync(Evidence evidence, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO evidence
              (evidence_id, vendor_id, doc_type, source_tier, ref, doc_version, ingested_at)
            VALUES
              (@eid, @vid, @dtype, @tier, @ref, @ver, @at)
            """;
        cmd.Parameters.AddWithValue("@eid",   evidence.EvidenceId.ToString());
        cmd.Parameters.AddWithValue("@vid",   evidence.VendorId.ToString());
        cmd.Parameters.AddWithValue("@dtype", evidence.DocType.ToString());
        cmd.Parameters.AddWithValue("@tier",  evidence.SourceTier.ToString());
        cmd.Parameters.AddWithValue("@ref",   evidence.Ref);
        cmd.Parameters.AddWithValue("@ver",   evidence.DocVersion);
        cmd.Parameters.AddWithValue("@at",    evidence.IngestedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<Evidence?> GetEvidenceAsync(Guid evidenceId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM evidence WHERE evidence_id = @eid";
        cmd.Parameters.AddWithValue("@eid", evidenceId.ToString());
        var results = ReadEvidence(cmd);
        return Task.FromResult(results.Count > 0 ? results[0] : (Evidence?)null);
    }

    public Task<IReadOnlyList<Evidence>> GetEvidenceForVendorAsync(Guid vendorId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM evidence WHERE vendor_id = @vid ORDER BY ingested_at";
        cmd.Parameters.AddWithValue("@vid", vendorId.ToString());
        return Task.FromResult(ReadEvidence(cmd));
    }

    // ── Vendor registry persistence ───────────────────────────────────────────

    /// <summary>Persist a user-created vendor so it survives process restart.</summary>
    public Task SaveVendorAsync(Guid id, string canonicalName, DateTimeOffset? renewalDate, DateTimeOffset createdAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO vendors (id, canonical_name, renewal_date, created_at)
            VALUES (@id, @name, @renewal, @created_at)
            """;
        cmd.Parameters.AddWithValue("@id",         id.ToString());
        cmd.Parameters.AddWithValue("@name",        canonicalName);
        cmd.Parameters.AddWithValue("@renewal",     renewalDate.HasValue ? (object)renewalDate.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at",  createdAt.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Load all persisted vendors with no program_run_id (user-created via the legacy vendor-file
    /// upload path). Seeded demo vendors are not stored here. Deliberately excludes every
    /// KYV-discovered vendor — KYV's Stage F (RegistryWriter) always stamps a non-null
    /// program_run_id for run isolation (see ProgramRun_LegacyDemo_Untouched, which pins this
    /// exact separation). Use <see cref="LoadVendorsByRunAsync"/> to read a specific KYV run's
    /// vendors instead — do not relax this filter to include them.
    /// </summary>
    public Task<IReadOnlyList<(Guid Id, string Name, DateTimeOffset? RenewalDate)>> LoadVendorsAsync()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, canonical_name, renewal_date FROM vendors WHERE program_run_id IS NULL";
        var results = new List<(Guid, string, DateTimeOffset?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id         = Guid.Parse(reader.GetString(0));
            var name       = reader.GetString(1);
            var renewalRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
            DateTimeOffset? renewal = renewalRaw is null ? null : DateTimeOffset.Parse(renewalRaw);
            results.Add((id, name, renewal));
        }
        return Task.FromResult<IReadOnlyList<(Guid, string, DateTimeOffset?)>>(results);
    }

    /// <summary>
    /// Load all vendors stamped with a specific KYV program_run_id — the counterpart to
    /// <see cref="LoadVendorsAsync"/>'s NULL-only filter. POST /kyv/run uses this with the run id
    /// it just produced to sync exactly the vendors that run discovered into EntityRegistry,
    /// instead of the global NULL-filtered query (which would never see them).
    /// </summary>
    public Task<IReadOnlyList<(Guid Id, string Name, DateTimeOffset? RenewalDate)>> LoadVendorsByRunAsync(
        Guid runId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, canonical_name, renewal_date FROM vendors WHERE program_run_id = @run_id";
        cmd.Parameters.AddWithValue("@run_id", runId.ToString());
        var results = new List<(Guid, string, DateTimeOffset?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id         = Guid.Parse(reader.GetString(0));
            var name       = reader.GetString(1);
            var renewalRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
            DateTimeOffset? renewal = renewalRaw is null ? null : DateTimeOffset.Parse(renewalRaw);
            results.Add((id, name, renewal));
        }
        return Task.FromResult<IReadOnlyList<(Guid, string, DateTimeOffset?)>>(results);
    }

    /// <summary>
    /// Load every KYV-discovered vendor (program_run_id IS NOT NULL) across all runs, for
    /// boot-time re-registration into EntityRegistry — without this, a vendor discovered by a
    /// prior process instance vanishes from /vendors on restart even though its beliefs/checkins
    /// remain in SQLite (LoadVendorsAsync deliberately excludes them; see its doc comment).
    /// Rows are deduped per comparison_key (falling back to canonical_name), preferring whichever
    /// duplicate has beliefs recorded — a re-run can leave behind an empty duplicate row, and that
    /// one should never win over the real one just for being newer.
    /// </summary>
    public Task<IReadOnlyList<(Guid Id, string Name, DateTimeOffset? RenewalDate)>> LoadAllKyvVendorsAsync()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT v.id, v.canonical_name, v.renewal_date, v.comparison_key, v.created_at,
                   (SELECT COUNT(*) FROM beliefs b WHERE b.entity_id = v.id) AS belief_count
            FROM vendors v
            WHERE v.program_run_id IS NOT NULL";

        var rows = new List<(Guid Id, string Name, DateTimeOffset? Renewal, string GroupKey, DateTimeOffset CreatedAt, long BeliefCount)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id           = Guid.Parse(reader.GetString(0));
                var name         = reader.GetString(1);
                var renewalRaw   = reader.IsDBNull(2) ? null : reader.GetString(2);
                DateTimeOffset? renewal = renewalRaw is null ? null : DateTimeOffset.Parse(renewalRaw);
                var comparisonKey = reader.IsDBNull(3) ? name : reader.GetString(3);
                var createdAt    = DateTimeOffset.Parse(reader.GetString(4));
                var beliefCount  = reader.GetInt64(5);
                rows.Add((id, name, renewal, comparisonKey, createdAt, beliefCount));
            }
        }

        var deduped = rows
            .GroupBy(r => r.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.BeliefCount).ThenByDescending(r => r.CreatedAt).First())
            .Select(r => (r.Id, r.Name, r.Renewal))
            .ToList();

        return Task.FromResult<IReadOnlyList<(Guid, string, DateTimeOffset?)>>(deduped);
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    public Task ResetAsync(CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        // vendors table is intentionally excluded — user-created vendors survive demo resets.
        cmd.CommandText = "DELETE FROM beliefs; DELETE FROM entity_indices; DELETE FROM postures; DELETE FROM signals; DELETE FROM evidence;";
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

            // Vendor file fields
            var observedAtRaw = reader["observed_at"];
            DateTimeOffset? observedAt = observedAtRaw is DBNull or null ? null
                : DateTimeOffset.Parse(observedAtRaw.ToString()!);

            var hldRaw     = reader["half_life_days"];
            int? halfLifeDays = hldRaw is DBNull or null ? null : Convert.ToInt32(hldRaw);

            var validUntilRaw = reader["valid_until"];
            DateTimeOffset? validUntil = validUntilRaw is DBNull or null ? null
                : DateTimeOffset.Parse(validUntilRaw.ToString()!);

            var provEidRaw = reader["provenance_evidence_id"];
            var provLocRaw = reader["provenance_locator"];
            BeliefProvenance? provenance = null;
            if (provEidRaw is not DBNull and not null && provLocRaw is not DBNull and not null)
                provenance = new BeliefProvenance(Guid.Parse(provEidRaw.ToString()!), provLocRaw.ToString()!);

            var claimKeyRaw = reader["claim_key"];
            var claimKey = claimKeyRaw is DBNull or null ? "" : claimKeyRaw.ToString()!;

            var dimensionRaw = reader.GetString(reader.GetOrdinal("dimension"));

            results.Add(new Belief(
                Id:            Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                EntityId:      Guid.Parse(reader.GetString(reader.GetOrdinal("entity_id"))),
                Dimension:     string.IsNullOrEmpty(dimensionRaw) ? null : Enum.Parse<Dimension>(dimensionRaw),
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
                ReasoningSummary         = reasoning,
                ClaimKey                 = claimKey,
                ObservedAt               = observedAt,
                HalfLifeDays             = halfLifeDays,
                ValidUntil               = validUntil,
                Provenance               = provenance
            });
        }
        return results;
    }

    private static IReadOnlyList<Evidence> ReadEvidence(SqliteCommand cmd)
    {
        var results = new List<Evidence>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new Evidence(
                EvidenceId:  Guid.Parse(reader.GetString(reader.GetOrdinal("evidence_id"))),
                VendorId:    Guid.Parse(reader.GetString(reader.GetOrdinal("vendor_id"))),
                DocType:     Enum.Parse<DocType>(reader.GetString(reader.GetOrdinal("doc_type"))),
                SourceTier:  Enum.Parse<SourceTier>(reader.GetString(reader.GetOrdinal("source_tier"))),
                Ref:         reader.GetString(reader.GetOrdinal("ref")),
                DocVersion:  reader.GetInt32(reader.GetOrdinal("doc_version")),
                IngestedAt:  DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("ingested_at")))));
        }
        return results;
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = DbSchema.Ddl;
        cmd.ExecuteNonQuery();
        MigrateBeliefColumns();
        MigrateRegistryColumns();
        MigrateCheckInColumns();
        MigrateOAuthTokenTable();
    }

    /// <summary>Adds identity-resolution columns to the vendors table and creates vendor_aliases if absent.</summary>
    private void MigrateRegistryColumns()
    {
        var vendorCols = new[]
        {
            ("comparison_key",         "TEXT"),
            ("entity_type",            "TEXT"),
            ("confidence",             "REAL"),
            ("flags",                  "TEXT"),
            ("status",                 "TEXT"),
            ("rebrand_map_ref",        "TEXT"),
            ("acquisition_map_ref",    "TEXT"),
            ("absorbed_into_vendor_id","TEXT"),
            ("program_run_id",         "TEXT"),
            ("entity_role",            "TEXT"),
            ("domains_json",           "TEXT"),
        };
        foreach (var (col, def) in vendorCols)
            TryAddColumn("vendors", col, def);

        // vendor_aliases is in the DDL with CREATE TABLE IF NOT EXISTS; the migration is a no-op
        // on fresh databases but safe to call after MigrateBeliefColumns on existing ones.
    }

    /// <summary>Adds vendor file columns to the beliefs table if they are absent (safe on fresh DBs too).</summary>
    private void MigrateBeliefColumns()
    {
        var migrations = new[]
        {
            ("claim_key",              "TEXT NOT NULL DEFAULT ''"),
            ("observed_at",            "TEXT"),
            ("half_life_days",         "INTEGER"),
            ("valid_until",            "TEXT"),
            ("provenance_evidence_id", "TEXT"),
            ("provenance_locator",     "TEXT"),
        };
        foreach (var (col, def) in migrations)
            TryAddColumn("beliefs", col, def);
    }

    private void TryAddColumn(string table, string column, string definition)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            cmd.ExecuteNonQuery();
        }
        catch { /* column already exists — SQLite has no IF NOT EXISTS for ALTER TABLE */ }
    }

    // ── Vendor file write-path helpers (§2 ceilings + §7 supersession) ───────

    /// <summary>
    /// One candidate in a same-tier collision over a (entity_id, claim_key) slot — either an
    /// existing active prior (PriorId set) or the newly arriving belief (PriorId null). Carries
    /// only the fields IsStrongerForSlot needs to compare candidates; never the belief's random Id
    /// (a fresh Guid per run, not stable across runs), CreatedAt (identical for every belief in a
    /// single KYV run — see ObservedAt below), or SourceTier (the caller guarantees every
    /// candidate passed here already shares the same tier).
    /// </summary>
    private readonly record struct SupersessionCandidate(
        Guid? PriorId, DateTimeOffset? ObservedAt, string Derivation, double Value);

    private IReadOnlyList<(Guid id, SourceTier tier, DateTimeOffset? observedAt, string derivation, double value)>
        FindActivePriorsInSlot(Guid entityId, string claimKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, source_tier, observed_at, derivation, value FROM beliefs " +
            "WHERE entity_id = @eid AND claim_key = @ck AND superseded_by IS NULL " +
            "ORDER BY id";
        cmd.Parameters.AddWithValue("@eid", entityId.ToString());
        cmd.Parameters.AddWithValue("@ck",  claimKey);
        using var reader = cmd.ExecuteReader();
        var results = new List<(Guid, SourceTier, DateTimeOffset?, string, double)>();
        while (reader.Read())
        {
            var id     = Guid.Parse(reader.GetString(0));
            var tier   = Enum.Parse<SourceTier>(reader.GetString(1));
            var obsRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
            DateTimeOffset? obs = obsRaw is null ? null : DateTimeOffset.Parse(obsRaw);
            var derivation = reader.GetString(3);
            var value      = reader.GetDouble(4);
            results.Add((id, tier, obs, derivation, value));
        }
        return results;
    }

    /// <summary>
    /// Same-tier collision tiebreak: true if `a` outranks `b` for "current" status in a
    /// (entity_id, claim_key) slot, GIVEN both are already the same tier (the caller only invokes
    /// this among candidates it has pre-filtered to equal SourceTier — see AppendBeliefAsync's §7.
    /// Cross-tier disagreement is handled entirely by the caller and never reaches here: a
    /// stronger prior stays current alongside a weaker new arrival untouched, feeding
    /// ContradictionDetector.DetectCrossSource / RubricModule fusion).
    ///
    /// observedAt is the recency tiebreak. KYV currently stamps every belief in a run with the
    /// same run-wide timestamp (no real per-document dates yet — that's E1 work), so observedAt is
    /// typically equal and this branch is a no-op today; the deterministic content ordering below
    /// is what actually resolves same-tier collisions in the meantime. It compares only stable,
    /// content-derived fields — never a belief's Id (a fresh random Guid every run) and never
    /// insertion/arrival order — so the same two belief contents pick the same winner regardless of
    /// which run or which order they were appended in. Once E1 supplies real per-document dates,
    /// observedAt starts doing real work and this fallback simply stops firing; no logic change
    /// needed here.
    /// </summary>
    private static bool IsStrongerForSlot(SupersessionCandidate a, SupersessionCandidate b)
    {
        if (a.ObservedAt.HasValue && b.ObservedAt.HasValue && a.ObservedAt.Value != b.ObservedAt.Value)
            return a.ObservedAt.Value > b.ObservedAt.Value;

        var cmp = string.CompareOrdinal(a.Derivation ?? "", b.Derivation ?? "");
        if (cmp != 0) return cmp > 0;
        return a.Value.CompareTo(b.Value) > 0;
    }

    /// <summary>
    /// Tier strength, read from the catalogue's source_tiers weight (source_tiers.saas.v1.json) so
    /// this ranking can never drift from the config-driven values used elsewhere (§2's confidence
    /// clamp above, AnsweringPrompt.PresentationConfidence). Falls back to a hardcoded ladder —
    /// same ordering as the current catalogue — only when no profile is available (e.g. a store
    /// constructed without one) or the profile omits a tier.
    /// </summary>
    private double VendorFileTierRank(SourceTier tier)
    {
        if (_profile is not null && _profile.SourceTiers.TryGetValue(tier.ToString(), out var tc))
            return tc.Weight;
        return FallbackVendorFileTierRank(tier);
    }

    private static double FallbackVendorFileTierRank(SourceTier tier) => tier switch
    {
        SourceTier.Primary        => 1.0,
        SourceTier.Verified       => 0.8,
        SourceTier.Reported       => 0.5,
        SourceTier.Inferred       => 0.3,
        SourceTier.Unverified     => 0.2,
        SourceTier.Correspondence => 0.25,
        SourceTier.Confirmed      => 0.65,
        _                         => 0.0
    };

    // ── IRegistryStore — canonical vendor + alias persistence ─────────────────

    public Task SaveRegistryVendorAsync(RegistryVendorRow vendor, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO vendors
              (id, canonical_name, created_at,
               comparison_key, entity_type, confidence, flags, status,
               rebrand_map_ref, acquisition_map_ref, absorbed_into_vendor_id,
               program_run_id, entity_role, domains_json)
            VALUES
              (@id, @name, @created_at,
               @comparison_key, @entity_type, @confidence, @flags, @status,
               @rebrand_map_ref, @acquisition_map_ref, @absorbed_into_vendor_id,
               @program_run_id, @entity_role, @domains_json)
            """;
        cmd.Parameters.AddWithValue("@id",                      vendor.VendorId.ToString());
        cmd.Parameters.AddWithValue("@name",                    vendor.CanonicalName);
        cmd.Parameters.AddWithValue("@created_at",              vendor.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@comparison_key",          vendor.ComparisonKey          ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@entity_type",             vendor.EntityType             ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@confidence",              vendor.Confidence.HasValue ? vendor.Confidence.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@flags",                   vendor.FlagsJson              ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@status",                  vendor.Status                 ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@rebrand_map_ref",         vendor.RebrandMapRef          ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@acquisition_map_ref",     vendor.AcquisitionMapRef      ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@absorbed_into_vendor_id", vendor.AbsorbedIntoVendorId.HasValue
                                                                    ? vendor.AbsorbedIntoVendorId.Value.ToString()
                                                                    : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@program_run_id",          vendor.ProgramRunId.HasValue
                                                                    ? vendor.ProgramRunId.Value.ToString()
                                                                    : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@entity_role",             vendor.EntityRole   ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@domains_json",            vendor.DomainsJson  ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task SaveVendorAliasAsync(VendorAliasRow alias, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO vendor_aliases
              (id, vendor_id, raw_name, provenance_doc_id, provenance_span)
            VALUES
              (@id, @vendor_id, @raw_name, @provenance_doc_id, @provenance_span)
            """;
        cmd.Parameters.AddWithValue("@id",                alias.AliasId.ToString());
        cmd.Parameters.AddWithValue("@vendor_id",         alias.VendorId.ToString());
        cmd.Parameters.AddWithValue("@raw_name",          alias.RawName);
        cmd.Parameters.AddWithValue("@provenance_doc_id", alias.ProvenanceDocId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@provenance_span",   alias.ProvenanceSpan  ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<RegistryVendorRow?> GetRegistryVendorAsync(Guid vendorId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, canonical_name, created_at, comparison_key, entity_type, " +
            "       confidence, flags, status, rebrand_map_ref, acquisition_map_ref, " +
            "       absorbed_into_vendor_id, program_run_id, entity_role, domains_json " +
            "FROM vendors WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", vendorId.ToString());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return Task.FromResult<RegistryVendorRow?>(null);
        return Task.FromResult<RegistryVendorRow?>(ReadRegistryVendorRow(reader));
    }

    public Task<IReadOnlyList<VendorAliasRow>> GetVendorAliasesAsync(Guid vendorId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, vendor_id, raw_name, provenance_doc_id, provenance_span " +
            "FROM vendor_aliases WHERE vendor_id = @vid";
        cmd.Parameters.AddWithValue("@vid", vendorId.ToString());
        var results = new List<VendorAliasRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new VendorAliasRow(
                AliasId:         Guid.Parse(reader.GetString(0)),
                VendorId:        Guid.Parse(reader.GetString(1)),
                RawName:         reader.GetString(2),
                ProvenanceDocId: reader.IsDBNull(3) ? null : reader.GetString(3),
                ProvenanceSpan:  reader.IsDBNull(4) ? null : reader.GetString(4)));
        return Task.FromResult<IReadOnlyList<VendorAliasRow>>(results);
    }

    public Task<IReadOnlyList<RegistryVendorRow>> GetAllRegistryVendorsAsync(CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, canonical_name, created_at, comparison_key, entity_type, " +
            "       confidence, flags, status, rebrand_map_ref, acquisition_map_ref, " +
            "       absorbed_into_vendor_id, program_run_id, entity_role, domains_json " +
            "FROM vendors";
        var results = new List<RegistryVendorRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadRegistryVendorRow(reader));
        return Task.FromResult<IReadOnlyList<RegistryVendorRow>>(results);
    }

    private static RegistryVendorRow ReadRegistryVendorRow(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new RegistryVendorRow(
            VendorId:             Guid.Parse(reader.GetString(0)),
            CanonicalName:        reader.GetString(1),
            CreatedAt:            DateTimeOffset.Parse(reader.GetString(2)),
            ComparisonKey:        reader.IsDBNull(3)  ? null : reader.GetString(3),
            EntityType:           reader.IsDBNull(4)  ? null : reader.GetString(4),
            Confidence:           reader.IsDBNull(5)  ? null : reader.GetDouble(5),
            FlagsJson:            reader.IsDBNull(6)  ? null : reader.GetString(6),
            Status:               reader.IsDBNull(7)  ? null : reader.GetString(7),
            RebrandMapRef:        reader.IsDBNull(8)  ? null : reader.GetString(8),
            AcquisitionMapRef:    reader.IsDBNull(9)  ? null : reader.GetString(9),
            AbsorbedIntoVendorId: reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
            ProgramRunId:         reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)),
            EntityRole:           reader.IsDBNull(12) ? null : reader.GetString(12),
            DomainsJson:          reader.IsDBNull(13) ? null : reader.GetString(13));

    // ── ICheckInRowStore ──────────────────────────────────────────────────────

    public Task SaveCheckInAsync(CheckInRow row, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO checkins
              (checkin_id, vendor_id, program_run_id, kind, question, response_shape,
               target_field, owner, status, raised_at, answered_at, expires_at,
               response_value, paired_vendor_id)
            VALUES
              (@checkin_id, @vendor_id, @program_run_id, @kind, @question, @response_shape,
               @target_field, @owner, @status, @raised_at, @answered_at, @expires_at,
               @response_value, @paired_vendor_id)
            """;
        cmd.Parameters.AddWithValue("@checkin_id",       row.CheckInId.ToString());
        cmd.Parameters.AddWithValue("@vendor_id",        row.VendorId.ToString());
        cmd.Parameters.AddWithValue("@program_run_id",   row.ProgramRunId.ToString());
        cmd.Parameters.AddWithValue("@kind",             row.Kind);
        cmd.Parameters.AddWithValue("@question",         row.Question);
        cmd.Parameters.AddWithValue("@response_shape",   row.ResponseShape);
        cmd.Parameters.AddWithValue("@target_field",     row.TargetField      ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@owner",            row.Owner);
        cmd.Parameters.AddWithValue("@status",           row.Status);
        cmd.Parameters.AddWithValue("@raised_at",        row.RaisedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@answered_at",      row.AnsweredAt.HasValue    ? row.AnsweredAt.Value.ToString("O")    : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@expires_at",       row.ExpiresAt.HasValue     ? row.ExpiresAt.Value.ToString("O")     : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@response_value",   row.ResponseValue          ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@paired_vendor_id", row.PairedVendorId.HasValue ? row.PairedVendorId.Value.ToString() : (object)DBNull.Value);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CheckInRow>> GetOpenCheckInsAsync(CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM checkins WHERE status = 'OPEN' ORDER BY raised_at";
        var results = new List<CheckInRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadCheckInRow(reader));
        return Task.FromResult<IReadOnlyList<CheckInRow>>(results);
    }

    public Task<CheckInRow?> GetCheckInAsync(Guid checkInId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM checkins WHERE checkin_id = @id";
        cmd.Parameters.AddWithValue("@id", checkInId.ToString());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return Task.FromResult<CheckInRow?>(null);
        return Task.FromResult<CheckInRow?>(ReadCheckInRow(reader));
    }

    public Task<IReadOnlyList<CheckInRow>> GetResolvedCheckInsForVendorAsync(Guid vendorId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM checkins WHERE vendor_id = @vendorId AND status IN ('PROCESSED', 'EXPIRED') ORDER BY raised_at";
        cmd.Parameters.AddWithValue("@vendorId", vendorId.ToString());
        var results = new List<CheckInRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadCheckInRow(reader));
        return Task.FromResult<IReadOnlyList<CheckInRow>>(results);
    }

    /// <summary>
    /// One-time migration: for every (vendor_id, target_field) pair that has multiple OPEN
    /// check-in rows, keep the one with the earliest raised_at and mark the rest EXPIRED.
    /// Safe to call repeatedly — subsequent calls are no-ops when no duplicates remain.
    /// Run once at startup before deploying the cross-run dedup fix so stale rows from
    /// previous pipeline reruns don't remain live in the pending queue.
    /// </summary>
    public Task ExpireDuplicatePendingCheckInsAsync(CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE checkins
            SET status = 'EXPIRED'
            WHERE status = 'OPEN'
              AND target_field IS NOT NULL
              AND checkin_id IN (
                SELECT c1.checkin_id
                FROM checkins c1
                WHERE c1.status = 'OPEN'
                  AND c1.target_field IS NOT NULL
                  AND EXISTS (
                    SELECT 1 FROM checkins c2
                    WHERE c2.status      = 'OPEN'
                      AND c2.vendor_id   = c1.vendor_id
                      AND c2.target_field = c1.target_field
                      AND c2.raised_at   < c1.raised_at
                  )
              )
            """;
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private static CheckInRow ReadCheckInRow(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var pairedCol = reader.GetOrdinal("paired_vendor_id");
        return new CheckInRow(
            CheckInId:      Guid.Parse(reader.GetString(reader.GetOrdinal("checkin_id"))),
            VendorId:       Guid.Parse(reader.GetString(reader.GetOrdinal("vendor_id"))),
            ProgramRunId:   Guid.Parse(reader.GetString(reader.GetOrdinal("program_run_id"))),
            Kind:           reader.GetString(reader.GetOrdinal("kind")),
            Question:       reader.GetString(reader.GetOrdinal("question")),
            ResponseShape:  reader.GetString(reader.GetOrdinal("response_shape")),
            TargetField:    reader.IsDBNull(reader.GetOrdinal("target_field"))    ? null : reader.GetString(reader.GetOrdinal("target_field")),
            Owner:          reader.GetString(reader.GetOrdinal("owner")),
            Status:         reader.GetString(reader.GetOrdinal("status")),
            RaisedAt:       DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("raised_at"))),
            AnsweredAt:     reader.IsDBNull(reader.GetOrdinal("answered_at"))   ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("answered_at"))),
            ExpiresAt:      reader.IsDBNull(reader.GetOrdinal("expires_at"))    ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("expires_at"))),
            ResponseValue:  reader.IsDBNull(reader.GetOrdinal("response_value")) ? null : reader.GetString(reader.GetOrdinal("response_value")),
            PairedVendorId: reader.IsDBNull(pairedCol) ? null : Guid.Parse(reader.GetString(pairedCol)));
    }

    private void MigrateCheckInColumns()
    {
        TryAddColumn("checkins", "paired_vendor_id", "TEXT");
    }

    private void MigrateOAuthTokenTable()
    {
        // CREATE TABLE IF NOT EXISTS is in the DDL, but older DBs that existed before
        // this table was added need it created via migration.
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS oauth_tokens (
                provider       TEXT NOT NULL PRIMARY KEY,
                access_token   TEXT NOT NULL,
                refresh_token  TEXT NOT NULL,
                expires_at     TEXT NOT NULL,
                user_email     TEXT NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    // ── OAuth token persistence ───────────────────────────────────────────────

    public Task SaveOAuthTokenAsync(
        string provider, string accessToken, string refreshToken,
        DateTimeOffset expiresAt, string userEmail,
        CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO oauth_tokens
              (provider, access_token, refresh_token, expires_at, user_email)
            VALUES
              (@provider, @access_token, @refresh_token, @expires_at, @user_email)
            """;
        cmd.Parameters.AddWithValue("@provider",      provider);
        cmd.Parameters.AddWithValue("@access_token",  accessToken);
        cmd.Parameters.AddWithValue("@refresh_token", refreshToken);
        cmd.Parameters.AddWithValue("@expires_at",    expiresAt.ToString("O"));
        cmd.Parameters.AddWithValue("@user_email",    userEmail);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns (accessToken, refreshToken, expiresAt, userEmail) or null if not found.
    /// </summary>
    public Task<(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string UserEmail)?>
        GetOAuthTokenAsync(string provider, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT access_token, refresh_token, expires_at, user_email " +
            "FROM oauth_tokens WHERE provider = @provider";
        cmd.Parameters.AddWithValue("@provider", provider);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<(string, string, DateTimeOffset, string)?>(null);

        return Task.FromResult<(string, string, DateTimeOffset, string)?>(
            (reader.GetString(0),
             reader.GetString(1),
             DateTimeOffset.Parse(reader.GetString(2)),
             reader.GetString(3)));
    }

    // ── IOwnerChannelPrefStore ─────────────────────────────────────────────────

    public Task SaveOwnerChannelPrefAsync(OwnerChannelPrefRow row, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO owner_channel_prefs
              (owner_id, channel, slack_destination)
            VALUES
              (@owner_id, @channel, @slack_destination)
            """;
        cmd.Parameters.AddWithValue("@owner_id",          row.OwnerId);
        cmd.Parameters.AddWithValue("@channel",           row.Channel);
        cmd.Parameters.AddWithValue("@slack_destination", row.SlackDestination ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<OwnerChannelPrefRow?> GetOwnerChannelPrefAsync(string ownerId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT owner_id, channel, slack_destination FROM owner_channel_prefs WHERE owner_id = @owner_id";
        cmd.Parameters.AddWithValue("@owner_id", ownerId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return Task.FromResult<OwnerChannelPrefRow?>(null);
        return Task.FromResult<OwnerChannelPrefRow?>(new OwnerChannelPrefRow(
            OwnerId:          reader.GetString(0),
            Channel:          reader.GetString(1),
            SlackDestination: reader.IsDBNull(2) ? null : reader.GetString(2)));
    }

    public Task<IReadOnlyList<OwnerChannelPrefRow>> GetAllOwnerChannelPrefsAsync(CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT owner_id, channel, slack_destination FROM owner_channel_prefs ORDER BY owner_id";
        var results = new List<OwnerChannelPrefRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new OwnerChannelPrefRow(
                OwnerId:          reader.GetString(0),
                Channel:          reader.GetString(1),
                SlackDestination: reader.IsDBNull(2) ? null : reader.GetString(2)));
        return Task.FromResult<IReadOnlyList<OwnerChannelPrefRow>>(results);
    }

    public void Dispose() => _conn.Dispose();
}
