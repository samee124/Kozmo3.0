# KYV Known Gaps (as of Phase 5 merge)

## The completeness engine works — but KYV vendors have no beliefs to feed it

- Phase 5 built and PROVED the completeness/Q&A engine. Proven on HAND-AUTHORED fixture beliefs
  (IIVS/Regulus), NOT beliefs extracted from real documents.
- KYV discovery produces IDENTITY only (name/role/address via IIdentityRegistry). It does NOT
  produce dimension Beliefs.
- THE GAP: no document→belief extraction exists for KYV. A live KYV vendor has ZERO beliefs, so
  completeness runs empty → every dimension shows as a gap/UNKNOWN regardless of document richness.
- POST /kyv/run constructs KyvProgramRunner WITHOUT the completeness orchestrator, so stage 7 is a
  no-op live. Commit 4's wiring is exercised only by tests.

## For the UI developer

Build against the CompletenessProfile contract + vendor DTOs as-is. EXPECT empty/all-gaps
completeness for KYV vendors until belief-extraction is built. Known BACKEND gap, not a UI bug.

## Next phase

Document → dimension-belief extraction for KYV, plus wiring completeness into POST /kyv/run.

## Belief bridge Commit 1 → Commit 2: Value convention (READ BEFORE building persistence)

`DocumentBeliefExtractor` (`Ii.CandidateExtraction`) intentionally emits RAW magnitudes for all
five target criteria (`sla_uptime`, `csat`, `payment_terms`, `renewal_date`, `annual_value`) — its
own docstring says normalization is a downstream concern, not the extractor's job. Commit 2
(wiring `BeliefCandidate` into `VendorFileWriteService`) must **not** treat all five the same way,
or it will silently corrupt dimension scores. Do not "simplify" this into a single pass-through.

- **Structural — persist RAW, unchanged:** `payment_terms`, `renewal_date`, `annual_value`.
  `claim_key_catalogue.saas.v1.json` marks these `class: structural`, `dimension_weight: 0.0`.
  `VendorFileWriteService` already forces `Confidence = 0` for structural claims, and
  `RubricModule` filters out any belief with `Confidence <= 0` — so the raw magnitude never
  reaches scoring. This exactly matches the existing `VendorFilePdfLane` convention for the same
  claim keys (raw days, raw dollars, raw Unix timestamp).

- **Scored — MUST be banded to 0-1 before persisting:** `sla_uptime`, `csat`.
  `claim_key_catalogue.saas.v1.json` marks these `class: scored`, `dimension_weight: 0.25` — they
  DO feed `RubricModule.ScoreDimension`, which is a pure weighted average of `Value * Confidence`
  (`RubricModule.cs:21-29`) that expects `Value` already on a 0-1 scale — no unit conversion
  happens there. A raw `99.9` or `4.6` fed straight through would blow the dimension score far
  outside `[0,1]` and break the golden fixtures. Commit 2 must band these through the **existing,
  proven** rubric config (`catalogue/profiles/saas/scoring_rubric.saas.v1.json`) — reuse, not
  reinvention:
  - `sla_uptime` (claim key) → `uptime_sla` (rubric criterion) — percentage band, complete over
    0–100, proven against the Cloudwave golden fixture (98.5% → 0.45).
  - `csat` (claim key) → `csat_score` (rubric criterion) — 1.0–5.0 rating band, proven against the
    Helix golden fixture (4.2 → 0.80).

- **CSAT is a rating (1.0–5.0), not a percentage.** `claim_key_catalogue.saas.v1.json` declares
  `csat`'s `value_type` as `"rating"`. `BeliefExtractionPrompt` was narrowed in Commit 1 to extract
  only the 1.0–5.0 scale and omit 0–100 percentage-scale CSAT figures. Don't widen this back
  without first defining new 0–100 bands — that's a business-rule decision, not a mechanical one.

- **`ApplyNumericThresholds` no longer mis-buckets out-of-domain values.** Fixed in Commit 1
  (`Ii.Observation/ObservationModule.cs`): it used to let a value outside a criterion's threshold
  domain (e.g. 92 fed into the 1.0–5.0 `csat_score` band) silently fall into the *lowest* bucket
  via a positional last-index catch-all. It now computes the true `[min, max]` domain across all
  buckets and returns `null` (abstain) for anything outside it, and correctly bands a value
  sitting exactly at the domain ceiling (e.g. 100% uptime) into the *top* bucket instead of the
  bottom. See `Ii.Tests/ObservationModuleThresholdTests.cs` for the regression tests. Golden
  fixtures are unaffected — every real signal value in `fixtures/signals.json` is strictly
  in-domain.

## Commit 3: `Belief.Confidence` means two different things to two different consumers (design tension)

`Belief.Confidence` is asked to serve two consumers with incompatible needs, and Commit 3 is the
first time real data exposed the collision:

- **`RubricModule` (scoring)** needs `Confidence == 0` on structural beliefs (`payment_terms`,
  `annual_value`, `renewal_date`) so its `beliefs.Where(b => b.Confidence > 0)` filter keeps them
  out of the dimension-score average — see the Commit 1→2 section above. This is correct and
  must not change.
- **`QuestionAnsweringStage` / `AnsweringPrompt` (completeness)** treats the SAME `Confidence`
  field as "evidence weight" — its system prompt tells the LLM "confidence reflects evidence
  weight ... no relevant beliefs → ≤ 0.30". A structural belief's `Confidence = 0.0` reads to the
  model as "no evidence exists", so it answered UNKNOWN even when the criterion/value/derivation
  were sitting right there in the prompt (confirmed against real IIVS documents: `saas.fin.l1.1`
  answered UNKNOWN despite a real `payment_terms` belief being present, reasoning "no information
  regarding a signed contract or defined payment terms").

**Fix applied (presentation-only, does not touch scoring):** `AnsweringPrompt.PresentationConfidence`
substitutes the belief's `SourceTier` ceiling (`catalogue/profiles/saas/source_tiers.saas.v1.json`
— e.g. Primary→1.0, Verified→0.8) for the evidence weight shown to the completeness LLM ONLY when
the persisted `Confidence == 0`. `QuestionAnsweringStage` takes an optional `SaasProfile? profile`
to supply the tier ceilings; existing callers that don't pass one keep prior behavior unchanged
(no existing test ever constructs a `Confidence == 0` belief, so this was a zero-risk addition —
confirmed by the full suite staying green with unchanged counts everywhere except the new test).
`RubricModule` and `VendorFileWriteService` are untouched — a structural belief's persisted
`Confidence` is still `0.0` and it still never reaches the scoring average.

**Verified result** (`Kyv.ProgramRunner.Tests/RealDocumentCompletenessProofTests.cs`, real IIVS
documents): `saas.fin.l1.1` ("signed contract with defined payment terms") now answers YES,
grounded in the real `payment_terms` beliefs — Financial coverage 1/2 (50%), up from 0/2.
`saas.fin.l1.2` ("total annual contract value") is still UNKNOWN — that one looks like a real
extraction/derivation-clarity question on that specific document, not the confidence-representation
issue this fix targets, and is out of scope here.

**This is a stopgap, not the fix.** The proper long-term fix is option 2 from the original
discussion: give the belief model a real, separate evidence-weight field distinct from scoring
`Confidence`, so structural facts can carry honest "how much do we trust this as evidence" without
overloading the field `RubricModule` depends on. Deferred — not started.

## Value-representation follow-up: option 1(b) shipped, does NOT fully close the gap

`Belief.Confidence` had two jobs colliding (above); `Belief.Value` has the same problem —
`RubricModule` needs the banded 0-1 score, completeness needs the human-readable fact (a CSAT of
"4.6 out of 5.0" persists as `Value = 1.00`). Investigating this surfaced TWO defects, not one:

1. `AnsweringPrompt.BeliefView` never serialized `Value` at all — the LLM saw no number for any
   belief, banded or raw.
2. `VendorFileWriteService.WriteBeliefAsync` **discarded the real evidence text unconditionally**,
   writing `Derivation: "vendor-file:{claimKey}"` regardless of what the caller had.
   `BeliefCandidate.Derivation` (built by `DocumentBeliefExtractor`) already carried the real
   quoted span (e.g. `doc:QBR....pdf "4.6 out of 5.0"`) — `BeliefPersistenceStage` had it in hand
   and it was thrown away before persistence. `AnsweringPrompt` already serializes `Derivation`,
   so this — not (1) — was the actual fix target for option 1(b).
3. This is bigger than a completeness problem: `VendorFileRenderer.FormatValue` (the user-facing
   vendor-file markdown report) and `DtoMapper.cs` (`BeliefViewDto.Value`, sent to
   `/vendors/{id}/trail`) both display `Belief.Value` directly — a scored belief's banded `1.00`
   shows as "1.00" there too, independent of completeness.

**Option 1(b) shipped:** `VendorFileWriteService.WriteBeliefAsync` gained an optional
`derivation` parameter — uses it when supplied, falls back to the exact prior
`"vendor-file:{claimKey}"` template when null/blank (zero behavior change for the ~15 other call
sites that don't pass one). `BeliefPersistenceStage` now threads `BeliefCandidate.Derivation`
through. No `AnsweringPrompt` change was needed — it already surfaces `Derivation`.
`Derivation` is confirmed excluded from the belief fingerprint (`FingerprintComputer` only hashes
`Dimension, Criterion, Value, Confidence` via `BeliefSnapshot`) — golden (26/26) and the
determinism-spike/fingerprint-annotation tests (7/7) confirm no fingerprint drift.

**Verified mechanically working, but did NOT ground the CSAT question.** `BeliefPersistenceStageTests`
and a new `Kozmo.VendorFile.Tests` pair (`WriteService_UsesCallerDerivation_WhenSupplied` /
`WriteService_FallsBackToTemplate_WhenNoDerivationSupplied`) confirm the real quoted text now
survives into `Belief.Derivation` — for IIVS's real `csat` belief this is now literally
`doc:QBR_Q32022_IIVS_RevMed.pdf "Study quality scores averaged 4.6 out of 5.0 based on sponsor
feedback surveys."`, not the old template. **But re-running the real-vendor completeness proof
against the re-recorded cassette shows Experiential coverage unchanged at 0/2 (0%)** — the model
now answers `saas.exp.l1.2` ("what is the current CSAT or NPS score") as UNKNOWN and does not even
cite the belief (previously, with the generic-template Derivation, it *did* cite the belief but
still said UNKNOWN — arguably a regression on citation, not just a non-improvement). Financial
stayed at 1/2 (50%), unchanged. Best working hypothesis: "study quality scores" in the real
document's own evidence text reads to the model as a different, more specific metric than "CSAT or
NPS score" — a wording-match problem the LLM is (arguably correctly) being conservative about,
not a numeric-representation problem. This was not chased further — out of scope for 1(b), and
resolving it either needs option 1(a) (a structured raw-value field the LLM can read
unambiguously, independent of free-text wording) or a documentation/derivation-quality
improvement upstream in `DocumentBeliefExtractor`'s evidence-quote extraction.

**Net effect of 1(b):** real evidence text now correctly reaches the belief's `Derivation` field
end-to-end (verified) and is available to any future consumer that reads it, but it did not by
itself unlock the Experiential coverage this stocktake was hoping for. `VendorFileRenderer`/
`DtoMapper` still display banded `Value` for scored beliefs — untouched, deferred to 1(a) as
originally planned.
