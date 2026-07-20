using Ig.Contracts;
using Ig.Resolution;
using Ii.CandidateExtraction;
using Ii.Completeness;
using Ii.Decay;
using Ii.Index;
using Ii.Intake;
using Ii.Observation;
using Ii.Posture;
using Ii.Rubric;
using Ii.Spine;
using Km.Store;
using Km.Store.Metadata;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Contracts.Interfaces;
using Kozmo.Llm;
using Wc.CheckIn;
using Wc.Contracts;

namespace Kyv.ProgramRunner;

/// <summary>
/// Executes the declared KYV program sequence over a local folder:
///   1 ingest           — enumerate PDFs (extract text via PdfTextExtractor) AND .eml files
///                        (parse via EmailParser, E-signal Part 5 Step 2) — email is additive,
///                        not a new pipeline phase (E1 Part 7 Step 5's framing, carried forward)
///   2 classify         — infer source tier per document (DocTypeInferrer); every email is fixed
///                        SourceTier.Correspondence — never filename-inferred the way a
///                        document's tier is
///   3 extract          — LLM party+role extraction (DocumentCandidateExtractor, reused as-is for
///                        email identity text — spec §2.4 Decision 3) + LLM dimension-fact
///                        extraction (DocumentBeliefExtractor for documents,
///                        EmailInterpretationExtractor for email beliefs + signals, E-signal
///                        Part 5 Step 5)
///   4 filter           — deterministic post-filter (embedded in extractor)
///   5 resolve          — Stages A–F: normalize → entity-type classify → cluster →
///                        annotate → disposition → persist to registry (RegistryWriter)
///   6 persist_beliefs  — correlate doc-scoped BeliefCandidates to the resolved vendor via the
///                        same ClusterId &lt;- DocId path RegistryWriter.Build() uses, persist via
///                        BeliefPersistenceStage (Commit 2); also persists doc-scoped
///                        MetadataCandidates via MetadataPersistenceStage when a metadata store is
///                        supplied (E1 Part 7 Step 5) — same correlation, routed to Km.Store.Metadata
///                        instead of IEntityStore, never scored, never read by scoring assemblies
///   7 raise_checkins   — identity + provisional-vendor gap check-ins
///   8 completeness_init — Q&amp;A completeness convergence per resolved vendor (direct
///                        CompletenessOrchestrator call — see stage 9's doc comment for why this
///                        stays separate from RecomputeVendorAsync's own completeness hook)
///   9 recompute_index  — Ii.Spine Index/Posture per resolved vendor (click-path fix #3b); a
///                        vendor with zero scored evidence correctly gets no Index/Posture
///                        (Ii.Index's #4B null-guard), not a fabricated Band/Stance
///
/// The runner is a declared sequence, not a hardcoded monolith.
/// Caller supplies <paramref name="now"/> so this class never reads the clock.
/// </summary>
public sealed class KyvProgramRunner
{
    private readonly IKozmoLlm                     _llm;
    private readonly IKozmoLlm                     _beliefLlm;
    private readonly IKozmoLlm                     _emailInterpretationLlm;
    private readonly EntityTypeClassificationStage _stageB;
    private readonly ClusteringStage               _stageC;
    private readonly CollisionStage                _stageD;
    private readonly IdentityGate                  _stageE;
    private readonly RegistryWriter                _stageF;
    private readonly BeliefPersistenceStage        _stageBeliefs;
    private readonly MetadataPersistenceStage?     _stageMetadata;
    private readonly DocumentPersistenceStage?     _stageDocuments;
    private readonly IEntityStore                  _entityStore;
    private readonly SaasProfile                   _profile;
    private readonly RaiseCheckInsStage            _raiseStage;
    private readonly ICheckInStore                 _checkInStore;
    private readonly string                        _owner;
    private readonly PdfTextExtractor              _pdfReader;
    private readonly PdfPageImageExtractor         _imageExtractor;
    private readonly OcrExtractor                  _ocrExtractor;
    private readonly CompletenessOrchestrator?     _completeness;
    private readonly EntityRegistry                _spineRegistry;
    private readonly bool                          _processEmail;

    // Declared stage sequence — names match the KYV program specification.
    private static readonly string[] DeclaredStages =
        ["ingest", "classify", "extract", "filter", "resolve", "persist_beliefs", "raise_checkins", "completeness_init", "recompute_index"];

    public KyvProgramRunner(
        IKozmoLlm                  llm,
        IEntityTypeClassifier      entityClassifier,
        IIdentityRegistry          registry,
        ICheckInStore              checkInStore,
        IEntityStore               entityStore,
        SaasProfile                profile,
        IKozmoLlm?                 beliefLlm     = null,
        string                     owner         = "kyv@kozmo",
        CompletenessOrchestrator?  completeness  = null,
        EntityRegistry?            spineRegistry = null,
        IMetadataStore?            metadataStore = null,
        IKozmoLlm?                 emailInterpretationLlm = null,
        bool                       processEmail  = false,
        IDocumentStore?            documentStore = null)
    {
        // E-signal Part 5 Step 6 — OFF by default, same opt-in shape as metadataStore/completeness
        // below. Real-corpus proof: turning email on unconditionally surfaced a genuine
        // identity-resolution precision gap (Scenario 07's email-only role hints are inconsistent
        // enough that "Brookfield" — a customer — sometimes aggregates to role=unknown rather than
        // "customer" and isn't filtered by IdentityGate.IsNonVendorRole, which only drops
        // customer/issuer/internal). That broke TWO pre-existing, document-corpus-calibrated tests
        // (ProgramRun_All6Scenarios_VendorSet_NoTimeout's "Brookfield must never appear" assertion;
        // ProgramRun_AbcIdentityAnswerYes_MergesLive_Absorbed_NotDeleted's "first IDENTITY_CONFIRM
        // check-in is the ABC pair" assumption, now competing with email-sourced near-miss noise).
        // Existing callers stay byte-for-byte unaffected until they explicitly opt in — the
        // identity-role-reliability gap is logged (KYV_KNOWN_GAPS.md) and deferred, not papered
        // over by silently degrading pre-existing regression coverage.
        _processEmail   = processEmail;
        _llm            = llm;
        // Identity extraction (DocumentCandidateExtractor) and belief extraction
        // (DocumentBeliefExtractor) use different system prompts, hence different cassette cache
        // keys — a single CachingLlmClient cannot serve both from one cassette file. Defaults to
        // reusing 'llm' (correct for a live, non-cassette-restricted client); pass a distinct
        // cassette-backed client here when 'llm' only has identity-extraction entries.
        _beliefLlm      = beliefLlm ?? llm;
        // E-signal Part 5 Step 6 — email interpretation (EmailInterpretationPrompt's belief AND
        // signal system prompts) uses a THIRD, distinct cassette key space from both identity
        // extraction and document belief extraction. Email IDENTITY extraction, by contrast,
        // reuses '_llm' directly (no separate parameter) — DocumentCandidateExtractor's prompt is
        // identical for documents and email identity text (spec §2.4 Decision 3: the existing
        // path, not a new one), so both share the SAME candidate-extraction cassette.
        _emailInterpretationLlm = emailInterpretationLlm ?? beliefLlm ?? llm;
        _stageB         = new EntityTypeClassificationStage(entityClassifier);
        _stageC         = new ClusteringStage();
        _stageD         = new CollisionStage();
        _stageE         = new IdentityGate();
        _stageF         = new RegistryWriter(registry);
        _stageBeliefs   = new BeliefPersistenceStage(entityStore, profile);
        // E1 Part 7 Step 5 — null when no caller supplies a metadata store (every existing
        // caller today), so metadata extraction still runs but is simply not persisted anywhere.
        _stageMetadata  = metadataStore is null ? null : new MetadataPersistenceStage(metadataStore);
        // Document retention — null when no caller supplies a document store (every existing
        // caller/test today), so beliefs/metadata fall back to their prior Guid.Empty/Guid.NewGuid()
        // placeholder behavior, byte-for-byte unchanged for those callers.
        _stageDocuments = documentStore is null ? null : new DocumentPersistenceStage(documentStore);
        _entityStore    = entityStore;
        _profile        = profile;
        _raiseStage     = new RaiseCheckInsStage();
        _checkInStore   = checkInStore;
        _owner          = owner;
        _completeness   = completeness;
        _pdfReader      = new PdfTextExtractor();
        _imageExtractor = new PdfPageImageExtractor();
        _ocrExtractor   = new OcrExtractor(llm);
        // Ii.Spine.EntityRegistry — distinct from the IIdentityRegistry above (that one resolves
        // KYV document identity; this one backs Index/Posture's renewal-date lookup). Defaults to
        // a fresh, empty registry when the caller has none to share — renewal date then reads as
        // null for KYV vendors (honest gap, not a fabrication) rather than requiring one.
        _spineRegistry  = spineRegistry ?? new EntityRegistry();
    }

    public async Task<ProgramRun> RunAsync(
        string                              workspacePath,
        DateTimeOffset                      now,
        IReadOnlyDictionary<string, string>? driveFileIdsByFilename = null,
        CancellationToken                  ct = default)
    {
        var runId          = Guid.NewGuid();
        var executions     = new List<ProgramStageExecution>();
        var unreadable     = new List<UnreadableDocument>();
        var extractor      = new DocumentCandidateExtractor(_llm);
        var beliefExtractor = new DocumentBeliefExtractor(_beliefLlm, _profile);
        var emailExtractor  = new EmailInterpretationExtractor(_emailInterpretationLlm, _profile);

        // ── Stage 1: ingest — enumerate PDFs, read bytes; AND .eml files ──────
        var pdfPaths = Directory
            .EnumerateFiles(workspacePath, "*.pdf", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // E-signal Part 5 Step 6 — additive, not a new pipeline phase: email is "just another
        // ingested document" for stage-count purposes, same framing MetadataPersistenceStage
        // already uses for signal persistence. Gated behind _processEmail (opt-in, off by
        // default) — existing callers never enumerate .eml files at all, not even to find zero.
        var emlPaths = _processEmail
            ? Directory
                .EnumerateFiles(workspacePath, "*.eml", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();

        // ── Stage 2: classify — infer source tier per document ────────────────
        var classified = pdfPaths
            .Select(p => (Path: p, Tier: DocTypeInferrer.InferTier(Path.GetFileName(p))))
            .ToList();
        executions.Add(new(1, "ingest",    now, pdfPaths.Count + emlPaths.Count));
        executions.Add(new(2, "classify",  now, classified.Count + emlPaths.Count));

        // ── Stage 3+4: extract + filter (both inside DocumentCandidateExtractor)
        var allCandidates           = new List<CandidateIdentityBelief>();
        var factCandidatesByDoc     = new List<(string DocId, IReadOnlyList<BeliefCandidate> Candidates)>();
        var metadataCandidatesByDoc = new List<(string DocId, IReadOnlyList<MetadataCandidate> Candidates)>();
        // Document retention (see KYV_KNOWN_GAPS.md) — captured here because this is the one place
        // in the whole pipeline that ever has the raw bytes AND the extracted text for a PDF in
        // hand together, before Program.cs's /kyv/run handler deletes the temp folder they came
        // from. Captured for EVERY downloaded file, including ones that end up Unreadable below —
        // retention is about the source, not extraction success.
        var pdfDocuments = new List<(string DocId, byte[] Content, string ContentText, string? DriveFileId)>();
        foreach (var (pdfPath, tier) in classified)
        {
            var bytes = File.ReadAllBytes(pdfPath);
            var docId = Path.GetFileName(pdfPath);
            var driveFileId = driveFileIdsByFilename?.GetValueOrDefault(docId);
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
                    pdfDocuments.Add((docId, bytes, "", driveFileId));
                    continue;
                }
            }

            pdfDocuments.Add((docId, bytes, text, driveFileId));

            var beliefs = await extractor.ExtractAsync(
                text, docId, tier, DocTypeInferrer.IsBankingContext(docId), ct);
            allCandidates.AddRange(beliefs);

            // Dimension-fact + metadata extraction (Commit 2 belief bridge; E1 Part 7 Step 5
            // metadata). One LLM pass yields both. A cassette miss means this particular document
            // has no recorded belief-extraction entry — treat as zero facts/metadata rather than
            // failing the whole KYV run, same tolerance the OCR fallback above uses.
            try
            {
                var extraction = await beliefExtractor.ExtractAsync(text, docId, tier, ct);
                factCandidatesByDoc.Add((docId, extraction.Beliefs));
                metadataCandidatesByDoc.Add((docId, extraction.Metadata));
            }
            catch (LlmCacheMissException)
            {
                factCandidatesByDoc.Add((docId, Array.Empty<BeliefCandidate>()));
                metadataCandidatesByDoc.Add((docId, Array.Empty<MetadataCandidate>()));
            }
        }

        // ── Stage 3+4 (email) — identity (reused DocumentCandidateExtractor path, spec §2.4
        // Decision 3) + interpretation (EmailInterpretationExtractor, E-signal Part 5 Step 5) ──
        foreach (var emlPath in emlPaths)
        {
            var docId = Path.GetFileName(emlPath);

            ParsedEmail email;
            try
            {
                email = EmailParser.ParseFile(emlPath);
            }
            catch (Exception ex)
            {
                unreadable.Add(new UnreadableDocument(
                    RelativePath: Path.GetRelativePath(workspacePath, emlPath),
                    Reason:       $"email parse failed: {ex.Message}"));
                continue;
            }

            // Identity extraction — every email is SourceTier.Correspondence, never
            // filename-inferred the way DocTypeInferrer.InferTier infers a document's tier.
            var identityText = EmailParser.BuildIdentityText(email);
            try
            {
                var candidates = await extractor.ExtractAsync(
                    identityText, docId, SourceTier.Correspondence, DocTypeInferrer.IsBankingContext(docId), ct);
                allCandidates.AddRange(candidates);
            }
            catch (LlmCacheMissException)
            {
                // No recorded identity-extraction entry for this email — it contributes no
                // identity candidates and so never enters resolution, but every other email and
                // every document is unaffected (same tolerance as the PDF loop above).
            }

            // Belief + signal interpretation — a cassette miss degrades this email to zero
            // beliefs/signals rather than failing the whole run (same tolerance as documents).
            try
            {
                var extraction = await emailExtractor.ExtractAsync(email, ct);
                factCandidatesByDoc.Add((docId, extraction.Beliefs));
                metadataCandidatesByDoc.Add((docId, extraction.Metadata));
            }
            catch (LlmCacheMissException)
            {
                factCandidatesByDoc.Add((docId, Array.Empty<BeliefCandidate>()));
                metadataCandidatesByDoc.Add((docId, Array.Empty<MetadataCandidate>()));
            }
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

        // Document retention (see KYV_KNOWN_GAPS.md) — persisted BEFORE beliefs/metadata, on the
        // SAME doc-scoped ClusterId <- DocId correlation, so its returned docId -> documentId map
        // is ready for BeliefPersistenceStage/MetadataPersistenceStage to point EvidenceId/
        // DocumentId at real rows instead of Guid.Empty/a fresh Guid.NewGuid(). No-op (empty map)
        // when no document store was supplied to this runner.
        var docIdToDocumentId = _stageDocuments != null
            ? await _stageDocuments.PersistAsync(pdfDocuments, annotated, dispositions, now, ct)
            : new Dictionary<string, Guid>();

        // ── Stage 6: persist_beliefs — correlate doc-scoped facts to the resolved vendor ──
        // Uses the SAME ClusterId <- DocId path RegistryWriter.Build() uses (Stage F, above).
        // Value convention (see KYV_KNOWN_GAPS.md): scored claims (sla_uptime, csat) are banded
        // to 0-1 before persisting; structural claims (payment_terms, renewal_date, annual_value)
        // persist raw with Confidence forced to 0 by VendorFileWriteService.
        var beliefsWritten = await _stageBeliefs.PersistAsync(
            factCandidatesByDoc, annotated, dispositions, now, docIdToDocumentId, ct);
        executions.Add(new(6, "persist_beliefs", now, beliefsWritten));

        // Metadata persistence (E1 Part 7 Step 5) — same doc-scoped correlation, routed to
        // Km.Store.Metadata instead of IEntityStore. Not a declared stage of its own (it is part
        // of "persist" conceptually, not a new pipeline phase) — no-op when no metadata store was
        // supplied to this runner.
        if (_stageMetadata != null)
            await _stageMetadata.PersistAsync(metadataCandidatesByDoc, annotated, dispositions, now, docIdToDocumentId, ct);

        // ── Stage 7: raise_checkins ────────────────────────────────────────────
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
        executions.Add(new(7, "raise_checkins", now, checkIns.Count));

        // ── Stage 8: completeness_init — initial Q&A completeness per resolved vendor ──
        // Runs once per vendor immediately after resolution, using the beliefs Stage 6 just
        // persisted (Commit 3 — previously passed an empty list unconditionally, so completeness
        // always ran empty even when Stage 6 had real beliefs to answer from). A vendor with no
        // beliefs (no belief-extraction cassette coverage for its documents) still degrades
        // correctly: the completeness engine treats empty beliefs as UNKNOWN answers → all L1
        // questions become gaps → initial gap check-ins raised. Null when cassette is absent
        // (legacy/demo).
        //
        // Per-vendor failure containment: production's completeness LLM is always replay-only
        // (no live network in the demo runtime path), so a belief combination that was never
        // pre-recorded — e.g. one vendor's evidence just changed after a new document arrived —
        // hits a cache miss. QuestionAnsweringStage/CompletenessOrchestrator do not catch that
        // internally, so without a guard here it would propagate out of RunAsync and fail this
        // entire multi-vendor run, taking every OTHER vendor's check-ins down with it. Mirrors
        // Stage 3's per-document extraction-miss handling: one bad input degrades gracefully
        // instead of aborting the whole pipeline. Caught broadly (not just LlmCacheMissException)
        // because ANY completeness failure for one vendor must stay scoped to that vendor.
        var resolvedVendorIds = dispositions
            .Where(d => d.Disposition != Disposition.NonVendor)
            .Select(d => d.ClusterId)
            .Distinct()
            .ToList();

        var completenessCheckInCount = 0;
        if (_completeness != null)
        {
            foreach (var vid in resolvedVendorIds)
            {
                try
                {
                    var vendorBeliefs = await _entityStore.GetCurrentBeliefsAsync(vid, ct);
                    var profile = await _completeness.RunAsync(vid, vendorBeliefs, now, ct);
                    completenessCheckInCount += profile.GapQuestionIds.Count;
                }
                catch (Exception)
                {
                    // This vendor's completeness_init step is skipped this run — no check-ins
                    // raised for it this cycle — but every other vendor, and every earlier stage
                    // (persist_beliefs, raise_checkins), is unaffected.
                }
            }
        }
        executions.Add(new(8, "completeness_init", now, completenessCheckInCount));

        // ── Stage 9: recompute_index — Ii.Spine Index/Posture per resolved vendor ──────────
        // Before this, KYV never called RecomputeVendorAsync at all: /vendors/{id} and
        // /vendors/{id}/trail read purely from the store (GetIndexAsync/GetPostureAsync), which
        // stayed permanently null/404 for every KYV-discovered vendor even after real beliefs
        // were persisted. Safe to call unconditionally now (click-path fix #4): a vendor with
        // zero scored evidence in every dimension correctly gets no Index/Posture persisted
        // (Ii.Index.Aggregate's null guard) — a clean "not assessed" 404, never a fabricated
        // Band/Stance built from RubricModule's neutral placeholder.
        //
        // Deliberately built with completeness: null — Stage 8 above already runs completeness
        // convergence directly, unconditionally, for every resolved vendor. Wiring completeness
        // into this facade too would double-run it for any vendor with real scored evidence
        // (GapCheckInStage's open-check-in dedup makes that harmless, not free — still two cache
        // lookups instead of one). More importantly, RecomputeVendorAsync returns before ever
        // reaching completeness when Index is null, which is true for every real vendor in the
        // current corpus (structural-only evidence) — routing completeness through here instead
        // of Stage 8's direct call would silently stop completeness from running at all for
        // exactly the vendors this whole fix chain was built to prove it works for.
        var recomputeFacade = new IiFacade(
            new ObservationModule(), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(),
            _entityStore, _profile, _spineRegistry, completeness: null);

        // RecomputeVendorAsync is documented "read-only; does not persist results" — it exists to
        // give the vendor-file page a fresh, on-demand judgement without writing anything. For
        // /vendors/{id} and /vendors/{id}/trail to see anything (they read GetIndexAsync/
        // GetPostureAsync, which are pure store reads), this stage must persist the result itself
        // — the same two calls the signal-driven path's RecomputeIndexAsync already makes.
        var assessedCount = 0;
        foreach (var vid in resolvedVendorIds)
        {
            try
            {
                var judgement = await recomputeFacade.RecomputeVendorAsync(vid, ct);
                if (judgement is null) continue; // not assessed — nothing to persist, by design

                await _entityStore.SaveIndexAsync(judgement.Index, ct);
                await _entityStore.AppendPostureAsync(judgement.Posture, ct);
                assessedCount++;
            }
            catch (Exception)
            {
                // This vendor's Index/Posture recompute is skipped this run — /vendors/{id}
                // stays 404 for it, but every other vendor and every earlier stage is unaffected.
            }
        }
        executions.Add(new(9, "recompute_index", now, assessedCount));

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
