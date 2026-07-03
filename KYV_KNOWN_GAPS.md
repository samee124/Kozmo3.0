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
