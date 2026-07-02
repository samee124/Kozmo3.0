using Ig.Contracts;
using Ig.Resolution;
using Ii.CandidateExtraction;
using Ii.Completeness;
using Ii.Intake;
using Kozmo.Llm;
using Wc.CheckIn;
using Wc.Contracts;

namespace Kyv.ProgramRunner;

/// <summary>
/// Executes the declared KYV program sequence over a local folder:
///   1 ingest           — enumerate PDFs, extract text (PdfTextExtractor)
///   2 classify         — infer source tier per document (DocTypeInferrer)
///   3 extract          — LLM party+role extraction (DocumentCandidateExtractor)
///   4 filter           — deterministic post-filter (embedded in extractor)
///   5 resolve          — Stages A–F: normalize → entity-type classify → cluster →
///                        annotate → disposition → persist to registry (RegistryWriter)
///
/// Stage 6 (raise_checkins) is Commit 2.
/// The runner is a declared sequence, not a hardcoded monolith.
/// Caller supplies <paramref name="now"/> so this class never reads the clock.
/// </summary>
public sealed class KyvProgramRunner
{
    private readonly IKozmoLlm                     _llm;
    private readonly EntityTypeClassificationStage _stageB;
    private readonly ClusteringStage               _stageC;
    private readonly CollisionStage                _stageD;
    private readonly IdentityGate                  _stageE;
    private readonly RegistryWriter                _stageF;
    private readonly RaiseCheckInsStage            _raiseStage;
    private readonly ICheckInStore                 _checkInStore;
    private readonly string                        _owner;
    private readonly PdfTextExtractor              _pdfReader;
    private readonly PdfPageImageExtractor         _imageExtractor;
    private readonly OcrExtractor                  _ocrExtractor;
    private readonly CompletenessOrchestrator?     _completeness;

    // Declared stage sequence — names match the KYV program specification.
    private static readonly string[] DeclaredStages =
        ["ingest", "classify", "extract", "filter", "resolve", "raise_checkins", "completeness_init"];

    public KyvProgramRunner(
        IKozmoLlm                  llm,
        IEntityTypeClassifier      entityClassifier,
        IIdentityRegistry          registry,
        ICheckInStore              checkInStore,
        string                     owner        = "kyv@kozmo",
        CompletenessOrchestrator?  completeness = null)
    {
        _llm            = llm;
        _stageB         = new EntityTypeClassificationStage(entityClassifier);
        _stageC         = new ClusteringStage();
        _stageD         = new CollisionStage();
        _stageE         = new IdentityGate();
        _stageF         = new RegistryWriter(registry);
        _raiseStage     = new RaiseCheckInsStage();
        _checkInStore   = checkInStore;
        _owner          = owner;
        _completeness   = completeness;
        _pdfReader      = new PdfTextExtractor();
        _imageExtractor = new PdfPageImageExtractor();
        _ocrExtractor   = new OcrExtractor(llm);
    }

    public async Task<ProgramRun> RunAsync(
        string            workspacePath,
        DateTimeOffset    now,
        CancellationToken ct = default)
    {
        var runId      = Guid.NewGuid();
        var executions = new List<ProgramStageExecution>();
        var unreadable = new List<UnreadableDocument>();
        var extractor  = new DocumentCandidateExtractor(_llm);

        // ── Stage 1: ingest — enumerate PDFs, read bytes ──────────────────────
        var pdfPaths = Directory
            .EnumerateFiles(workspacePath, "*.pdf", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ── Stage 2: classify — infer source tier per document ────────────────
        var classified = pdfPaths
            .Select(p => (Path: p, Tier: DocTypeInferrer.InferTier(Path.GetFileName(p))))
            .ToList();
        executions.Add(new(1, "ingest",    now, pdfPaths.Count));
        executions.Add(new(2, "classify",  now, classified.Count));

        // ── Stage 3+4: extract + filter (both inside DocumentCandidateExtractor)
        var allCandidates = new List<CandidateIdentityBelief>();
        foreach (var (pdfPath, tier) in classified)
        {
            var bytes = File.ReadAllBytes(pdfPath);
            var pages = _pdfReader.ExtractPageTexts(bytes);
            var text  = string.Join("\n", pages.OrderBy(kv => kv.Key).Select(kv => kv.Value));
            if (string.IsNullOrWhiteSpace(text))
            {
                // OCR fallback for image-only PDFs. Failures surface as distinct Unreadable reasons
                // rather than crashing the run — "empty" and "errored" are never conflated.
                string? ocrFailReason = null;
                try
                {
                    var pageImages = _imageExtractor.ExtractPageImages(bytes);
                    if (pageImages.Count == 0)
                    {
                        ocrFailReason = "no extractable raster images — PDF may use a compression format unsupported by the image extractor";
                    }
                    else
                    {
                        text = await _ocrExtractor.ExtractTextAsync(pageImages, ct) ?? "";
                        if (string.IsNullOrWhiteSpace(text))
                            ocrFailReason = "OCR returned no text — vision model could not read the embedded image";
                    }
                }
                catch (LlmCacheMissException)
                {
                    ocrFailReason = "OCR cassette miss — re-run recorder with OPENAI_API_KEY to populate vision entries";
                }
                catch (NotSupportedException)
                {
                    ocrFailReason = "LLM client does not support vision — use a CachingLlmClient backed by OpenAiLlmClient";
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    unreadable.Add(new UnreadableDocument(
                        RelativePath: Path.GetRelativePath(workspacePath, pdfPath),
                        Reason:       ocrFailReason ?? "unknown OCR failure"));
                    continue;
                }
            }

            var beliefs = await extractor.ExtractAsync(text, Path.GetFileName(pdfPath), tier, ct);
            allCandidates.AddRange(beliefs);
        }
        executions.Add(new(3, "extract", now, allCandidates.Count));
        executions.Add(new(4, "filter",  now, allCandidates.Count));

        // ── Stage 5: resolve — Stages A→F ─────────────────────────────────────
        // Stage A: Normalize
        var normalized = allCandidates.Select(Normalizer.Normalize).ToList();

        // Stage B: Classify entity type
        var stageB = new List<ClassifiedCandidate>(normalized.Count);
        foreach (var n in normalized)
            stageB.Add(await _stageB.ClassifyAsync(n, ct));

        // Stage C: Cluster
        var clusters = _stageC.Cluster(stageB);

        // Stage D: Annotate (collision detection)
        var annotated = _stageD.Annotate(clusters);

        // Stage E: Disposition
        var dispositions = _stageE.Assign(annotated);

        // Stage F: Persist to registry (stamp with programRunId for isolation)
        await _stageF.WriteAsync(annotated, dispositions, now, runId, ct);

        var vendorCount = dispositions.Count(d => d.Disposition != Disposition.NonVendor);
        executions.Add(new(5, "resolve", now, vendorCount));

        // ── Stage 6: raise_checkins ───────────────────────────────────────────
        // Identity check-ins: raised automatically from Triage+PossibleSameEntity dispositions.
        // Gap check-ins: one per Provisional vendor — asks the owner to confirm/supply details.
        var gapRequests = dispositions
            .Where(d => d.Disposition == Disposition.Provisional)
            .Select(d => new VendorGapRequest(
                VendorId:      d.ClusterId,
                Question:      $"Please confirm the status of '{d.ProposedCanonicalName}': " +
                               "provide contract reference or confirm operational status.",
                ResponseShape: ResponseShape.STATUS_SELECT,
                TargetField:   null))
            .ToList();

        var checkIns = await _raiseStage.RaiseAsync(
            dispositions, gapRequests, _checkInStore, _owner, runId, now, ct);
        executions.Add(new(6, "raise_checkins", now, checkIns.Count));

        // ── Stage 7: completeness_init — initial Q&A completeness per resolved vendor ──
        // Runs once per vendor immediately after resolution. Beliefs are empty at this point
        // (KYV writes identity to the registry, not dimension beliefs to the belief store).
        // The completeness engine treats empty beliefs as UNKNOWN answers → all L1 questions
        // become gaps → initial gap check-ins raised. Null when cassette is absent (legacy/demo).
        var completenessCheckInCount = 0;
        if (_completeness != null)
        {
            var resolvedVendorIds = dispositions
                .Where(d => d.Disposition != Disposition.NonVendor)
                .Select(d => d.ClusterId)
                .Distinct()
                .ToList();

            foreach (var vid in resolvedVendorIds)
            {
                var profile = await _completeness.RunAsync(vid, [], now, ct);
                completenessCheckInCount += profile.GapQuestionIds.Count;
            }
        }
        executions.Add(new(7, "completeness_init", now, completenessCheckInCount));

        return new ProgramRun(
            RunId:               runId,
            ProgramName:         "Know Your Vendor",
            SourceFolder:        workspacePath,
            StartedAt:           now,
            FinishedAt:          now,
            Status:              ProgramRunStatus.Completed,
            Stages:              executions,
            UnreadableDocuments: unreadable);
    }
}
