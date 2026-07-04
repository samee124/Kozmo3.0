using System.Text.Json;
using Ii.Contracts;
using Ii.Intake;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Contracts.Interfaces;

namespace Kozmo.Api;

/// <summary>
/// Vendor file control plane (Phase 4).
/// Fixed pipeline: intake lanes → RecomputeVendorAsync → render → write .md to disk.
/// Single-pass, no loops, no autonomous orchestrator.
/// </summary>
public sealed class VendorFileStageRunner
{
    private readonly IEntityStore           _store;
    private readonly SaasProfile            _profile;
    private readonly IIiFacade              _facade;
    private readonly VendorFileWriteService _writeService;
    private readonly CompletenessService    _completeness;
    private readonly RulesExtractor         _rules;
    private readonly PdfIntakeLane          _pdf;

    public VendorFileStageRunner(
        IEntityStore store,
        SaasProfile  profile,
        IIiFacade    facade)
    {
        _store        = store;
        _profile      = profile;
        _facade       = facade;
        _writeService = new VendorFileWriteService(store, profile);
        _completeness = new CompletenessService(profile);
        _rules        = new RulesExtractor(profile);
        _pdf          = new PdfIntakeLane(profile);
    }

    /// <summary>
    /// Run the vendor file pipeline: intake → recompute → render → write .md.
    /// Returns a VendorFileResult with the judgement, rendered markdown, and output path.
    /// </summary>
    public async Task<VendorFileResult> RunAsync(
        Guid            vendorId,
        string          vendorName,
        DateTimeOffset  asOf,
        string          fixtureFilePath,
        string          outputPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(fixtureFilePath))
            throw new FileNotFoundException($"Vendor file fixture not found: {fixtureFilePath}");

        // ── Stage 1: Intake ───────────────────────────────────────────────────
        var fixtureJson  = await File.ReadAllTextAsync(fixtureFilePath, ct);
        var evidenceDocs = JsonDocument.Parse(fixtureJson).RootElement;

        var writtenBeliefs = new List<Belief>();
        var evidenceRows   = new List<Evidence>();

        foreach (var docEl in evidenceDocs.EnumerateArray())
        {
            var ev         = ParseEvidence(docEl, vendorId);
            var claimsJson = JsonSerializer.Serialize(new { claims = docEl.GetProperty("claims") });

            var observedAt = docEl.TryGetProperty("observed_at", out var oaProp)
                ? DateTimeOffset.Parse(oaProp.GetString()!)
                : asOf;

            await _store.AppendEvidenceAsync(ev, ct);
            evidenceRows.Add(ev);

            IReadOnlyList<ExtractedClaim> extracted = ev.DocType switch
            {
                DocType.SignedContract or DocType.ExecutedAgreement or DocType.Amendment
                    or DocType.Addendum or DocType.Quote or DocType.Proposal
                    => _pdf.Replay(ev, claimsJson, observedAt),

                _ => _rules.Extract(ev, claimsJson, observedAt)
            };

            foreach (var claim in extracted)
            {
                var belief = await _writeService.WriteBeliefAsync(
                    vendorId:            vendorId,
                    claimKey:            claim.ClaimKey,
                    dimension:           claim.Dimension,
                    criterion:           claim.Criterion,
                    rawValue:            claim.NormalisedValue,
                    tier:                claim.Tier,
                    extractorConfidence: claim.ExtractorConfidence,
                    observedAt:          claim.ObservedAt,
                    provenance:          new BeliefProvenance(claim.EvidenceId, claim.Locator),
                    ingestedAt:          asOf,
                    ct:                  ct);
                writtenBeliefs.Add(belief);
            }
        }

        // ── Stage 2: RecomputeVendorAsync (includes management block) ─────────
        var judgement = await _facade.RecomputeVendorAsync(vendorId, ct);

        // ── Stage 3: Render ───────────────────────────────────────────────────
        var activeBeliefs = await _store.GetCurrentBeliefsAsync(vendorId, ct);
        var allBeliefs    = await _store.GetBeliefHistoryAsync(vendorId, ct);
        var evidence      = await _store.GetEvidenceForVendorAsync(vendorId, ct);

        // No dimension has any scored evidence yet — render identity + real belief evidence
        // without a fabricated Band/Stance, rather than a verdict manufactured from zero evidence.
        var markdown = judgement is null
            ? VendorFileRenderer.RenderNotAssessed(
                vendorId:      vendorId,
                vendorName:    vendorName,
                asOf:          asOf,
                activeBeliefs: activeBeliefs,
                evidence:      evidence)
            : VendorFileRenderer.Render(
                vendorId:      vendorId,
                vendorName:    vendorName,
                asOf:          asOf,
                judgement:     judgement,
                activeBeliefs: activeBeliefs,
                allBeliefs:    allBeliefs,
                evidence:      evidence);

        // ── Stage 4: Write .md to disk ────────────────────────────────────────
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(outputPath, markdown, ct);

        var completeness = _completeness.Compute(vendorId, activeBeliefs);

        return new VendorFileResult(
            VendorId:     vendorId,
            Evidence:     evidenceRows,
            Beliefs:      writtenBeliefs,
            Completeness: completeness,
            Index:        judgement?.Index,
            Posture:      judgement?.Posture)
        {
            Judgement        = judgement,
            RenderedMarkdown = markdown,
            RenderedPath     = outputPath,
        };
    }

    private static Evidence ParseEvidence(JsonElement docEl, Guid vendorId)
    {
        var ev = docEl.GetProperty("evidence");
        return new Evidence(
            EvidenceId:  Guid.Parse(ev.GetProperty("evidence_id").GetString()!),
            VendorId:    vendorId,
            DocType:     Enum.Parse<DocType>(ev.GetProperty("doc_type").GetString()!, ignoreCase: true),
            SourceTier:  Enum.Parse<SourceTier>(ev.GetProperty("source_tier").GetString()!, ignoreCase: true),
            Ref:         ev.GetProperty("ref").GetString()!,
            DocVersion:  ev.TryGetProperty("doc_version", out var dv) ? dv.GetInt32() : 1,
            IngestedAt:  DateTimeOffset.Parse(ev.GetProperty("ingested_at").GetString()!));
    }
}

public sealed record VendorFileResult(
    Guid                      VendorId,
    IReadOnlyList<Evidence>   Evidence,
    IReadOnlyList<Belief>     Beliefs,
    CompletenessResult        Completeness,
    EntityIndex?              Index,
    PostureAssignment?        Posture
)
{
    public VendorJudgement? Judgement        { get; init; }
    public string?          RenderedMarkdown { get; init; }
    public string?          RenderedPath     { get; init; }
}
