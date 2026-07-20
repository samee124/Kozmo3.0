using Kozmo.Contracts;
using Kozmo.Llm;

namespace Po.VendorCall;

/// <summary>
/// Full output of one review composition run.
/// Q1Packet and Q2Packet are the raw fact packets; callers use them to derive
/// ProposedSummary / CurrentPositionSummary for email rendering.
/// </summary>
public sealed record ReviewCompositionResult(
    ReviewCheckpoint Checkpoint,
    ComposedAnswer   Q1,
    ComposedAnswer   Q2,
    ComposedAnswer   Q3,
    ComposedAnswer   Q4,
    ComposedAnswer   Q5,
    ComposedAnswer   Overview,
    Q1FactPacket     Q1Packet,
    Q2FactPacket     Q2Packet);

public interface IReviewComposer
{
    /// <summary>
    /// Assembles all five fact packets, classifies status/movement/confidence,
    /// composes narratives for all six sections, saves a ReviewCheckpoint,
    /// and returns the full composition result.
    /// </summary>
    Task<ReviewCompositionResult> ComposeAsync(
        Guid                     vendorId,
        VendorCallEvidenceBundle bundle,
        IReadOnlyList<Belief>    beliefs,
        ReviewCheckpoint?        previousCheckpoint,
        string                   vendorName,
        string                   ownerUpn,
        string                   eventTypeCode,
        DateTimeOffset           now,
        Guid?                    vendorCallRunId,
        CheckpointKind           kind,
        CancellationToken        ct = default);
}

/// <summary>
/// Orchestrator: runs all five assemblers, the classifier, all six composers,
/// saves the resulting checkpoint, and returns the full composition result.
/// All assemblers are stateless value objects — created once per instance.
/// </summary>
public sealed class ReviewComposer : IReviewComposer
{
    // Stale threshold — intentionally in sync with VendorCallEvidenceCollector and FactAssemblers
    private const int StaleDays = 4;

    private static readonly IQ1FactAssembler Q1Assembler = new Q1FactAssembler();
    private static readonly IQ2FactAssembler Q2Assembler = new Q2FactAssembler();
    private static readonly IQ3FactAssembler Q3Assembler = new Q3FactAssembler();
    private static readonly IQ4FactAssembler Q4Assembler = new Q4FactAssembler();
    private static readonly IQ5FactAssembler Q5Assembler = new Q5FactAssembler();

    private readonly IReviewCheckpointStore     _store;
    private readonly IReviewStatusClassifier    _classifier;
    private readonly IQ1NarrativeComposer       _q1;
    private readonly IQ2NarrativeComposer       _q2;
    private readonly IQ3NarrativeComposer       _q3;
    private readonly IQ4NarrativeComposer       _q4;
    private readonly IQ5NarrativeComposer       _q5;
    private readonly IOverviewNarrativeComposer _overview;

    /// <param name="store">Checkpoint persistence.</param>
    /// <param name="llm">Optional LLM for Mode B narrative enhancement. Null = deterministic only.</param>
    public ReviewComposer(IReviewCheckpointStore store, IKozmoLlm? llm = null)
    {
        _store      = store;
        _classifier = new ReviewStatusClassifier();
        _q1         = new Q1NarrativeComposer(llm);
        _q2         = new Q2NarrativeComposer(llm);
        _q3         = new Q3NarrativeComposer(llm);
        _q4         = new Q4NarrativeComposer(llm);
        _q5         = new Q5NarrativeComposer(llm);
        _overview   = new OverviewNarrativeComposer(llm);
    }

    public async Task<ReviewCompositionResult> ComposeAsync(
        Guid                     vendorId,
        VendorCallEvidenceBundle bundle,
        IReadOnlyList<Belief>    beliefs,
        ReviewCheckpoint?        previousCheckpoint,
        string                   vendorName,
        string                   ownerUpn,
        string                   eventTypeCode,
        DateTimeOffset           now,
        Guid?                    vendorCallRunId,
        CheckpointKind           kind,
        CancellationToken        ct = default)
    {
        // ── 1-5. Assemble fact packets ────────────────────────────────────────
        var q1Packet = Q1Assembler.Assemble(
            bundle, beliefs, previousCheckpoint, vendorName, eventTypeCode, now);

        var q2Packet = Q2Assembler.Assemble(
            bundle, beliefs, previousCheckpoint, now);

        var q3Packet = Q3Assembler.Assemble(
            bundle, beliefs, previousCheckpoint, now);

        var q4Packet = Q4Assembler.Assemble(bundle, beliefs, now, maxPriorities: 3);

        var q5Packet = Q5Assembler.Assemble(bundle, q4Packet, ownerUpn, beliefs, now);

        // ── 6. Classify ───────────────────────────────────────────────────────
        var currentOpenCount = bundle.OpenCommitments.Count;
        var currentOverdueCount = bundle.OpenCommitments
            .Count(c => (now - c.IngestedAt).TotalDays > StaleDays);

        var status     = _classifier.ClassifyStatus(bundle, q1Packet);
        var movement   = _classifier.ClassifyMovement(
            currentOpenCount, currentOverdueCount, previousCheckpoint);
        var confidence = _classifier.ClassifyConfidence(bundle);

        // ── 7. Compose narratives (in parallel — no data dependencies) ────────
        var q1Task       = _q1.ComposeAsync(q1Packet, ct);
        var q2Task       = _q2.ComposeAsync(q2Packet, ct);
        var q3Task       = _q3.ComposeAsync(q3Packet, ct);
        var q4Task       = _q4.ComposeAsync(q4Packet, ct);
        var q5Task       = _q5.ComposeAsync(q5Packet, ct);
        var overviewTask = _overview.ComposeAsync(
            status, movement, confidence, q1Packet, q2Packet, ct);

        await Task.WhenAll(q1Task, q2Task, q3Task, q4Task, q5Task, overviewTask);

        var q1Answer       = q1Task.Result;
        var q2Answer       = q2Task.Result;
        var q3Answer       = q3Task.Result;
        var q4Answer       = q4Task.Result;
        var q5Answer       = q5Task.Result;
        var overviewAnswer = overviewTask.Result;

        // ── 8. Aggregate source IDs (union across all packets) ────────────────
        var allSourceIds = new HashSet<string>();
        foreach (var id in q1Packet.SourceReferenceIds)  allSourceIds.Add(id);
        foreach (var id in q2Packet.SourceReferenceIds)  allSourceIds.Add(id);
        foreach (var id in q3Packet.SourceReferenceIds)  allSourceIds.Add(id);
        foreach (var id in q4Packet.SourceReferenceIds)  allSourceIds.Add(id);
        foreach (var id in q5Packet.SourceReferenceIds)  allSourceIds.Add(id);

        // ── 9. Create and persist checkpoint ──────────────────────────────────
        var checkpoint = new ReviewCheckpoint(
            Id:                     Guid.NewGuid(),
            VendorId:               vendorId,
            VendorCallRunId:        vendorCallRunId,
            Kind:                   kind,
            CreatedAtUtc:           now,
            Status:                 status,
            Movement:               movement,
            Confidence:             confidence,
            Q1Answer:               q1Answer.Text,
            Q2Answer:               q2Answer.Text,
            Q3Answer:               q3Answer.Text,
            Q4Answer:               q4Answer.Text,
            Q5Answer:               q5Answer.Text,
            OpenCommitmentCount:    currentOpenCount,
            OverdueCommitmentCount: currentOverdueCount,
            UnresolvedSignalCount:  bundle.CommercialSignals.Count,
            SourceReferenceIds:     [.. allSourceIds]);

        await _store.SaveAsync(checkpoint, ct);

        return new ReviewCompositionResult(
            checkpoint, q1Answer, q2Answer, q3Answer, q4Answer, q5Answer, overviewAnswer,
            q1Packet, q2Packet);
    }
}
