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

## Salesforce demo scenario completed (Path C) — follow-up: malformed-GUID citation drop

Scenario 05's real documents (SOW, Amendment 2) both defer fees and payment terms to an Order Form
referenced by name but never present in the corpus. A faithful, completing-not-inventing Order
Form was authored (annual_value $214,500, payment_terms Net 30, term "coterminous with the
Agreement" — deliberately no calendar date, so `renewal_date` correctly abstains and Amendment 2's
2028-06-30 stays the sole source) and dropped into the workspace. Result: Salesforce Financial
coverage went from 0% (0/2) to 100% (2/2); Operational/Experiential stayed honest gaps (no
invented SLA/CSAT). Pinned by `SalesforceOrderFormCompletenessProofTests`.

**Follow-up, not fixed:** `saas.fin.l1.1` ("signed contract with defined payment terms") answers
`YES` at confidence 1.0 — genuinely grounded, the model's reasoning cites both the annual_value and
payment_terms facts by name — but `Answer.CitedBeliefIds` comes back **empty**. The recorded raw
model response cited belief ids as `"de110000-0000-000000000001"` / `...002` — missing a `-0000-`
segment versus the real id shape `de110000-0000-0000-0000-000000000001`. `QuestionAnsweringStage`
parses each cited id with `Guid.TryParse` (see `subsystems/interpretation-inference/dotnet/
Ii.Completeness/QuestionAnsweringStage.cs`); a malformed id silently fails to parse and is dropped
from the list rather than surfacing a parse warning. `saas.fin.l1.2` in the same run cited its
belief correctly, so this is a per-call model formatting slip, not a systemic prompt problem.

**Why it matters for the demo, specifically:** Kozmo's whole pitch is "glass box — same evidence
in, byte-identical fingerprint out, every claim traceable." An answer that is right but shows no
citation is indistinguishable, to a viewer, from an answer that is right by luck. It's a
credibility soft-spot right where the demo is trying to prove the opposite. Noting it here rather
than fixing it now — the fix is either (a) validate cited ids more leniently (e.g. strip stray
characters before `Guid.TryParse`), or (b) have `QuestionAnsweringStage` log/flag a parse failure
instead of silently dropping the id, so at least the gap is visible rather than silent. Neither is
started.

## Supersession fix (E1 prerequisite) — `annual_value`'s same-tier tiebreak picks an arbitrary winner

Investigating the reported "IIVS has 5 simultaneously-current `annual_value` beliefs" symptom
found it doesn't reproduce as stated — `SqliteEntityStore.AppendBeliefAsync`'s same-tier
deterministic tiebreak already collapses IIVS's real 6 milestone invoices down to exactly ONE
current `annual_value` belief ($34,700, Version 5) — but that "single truth" is itself wrong. IIVS
has no annual figure anywhere in its real document corpus: all 6 invoices are one-off CRO
milestone bills ($12,600–$39,200, e.g. "Milestone M2 -- SOW-01"), yet `BeliefExtractionPrompt`
has no periodicity check for `annual_value` (unlike `payment_terms`/`csat`, which already have
deterministic guards — `ContainsTerminationLanguage`/`ContainsNonCustomerQualityLanguage` — added
after exactly this kind of real-document failure), so every invoice's "TOTAL DUE" line gets
extracted as a competing `annual_value` candidate. All 6 land on the same `SourceTier.Verified`
(via `DocTypeInferrer`'s filename-default, not `doc_type_tier_map.saas.v1.json`), so they
genuinely collide as one same-tier slot, and the tiebreak (content-ordinal comparison, since
`ObservedAt` ties within a KYV run) picks whichever invoice's filename sorts last —
`Invoice_IIVS-INV-2023-0002.pdf` — not the invoice that means anything. The other 5 real amounts
are now silently superseded and invisible in the vendor-file view.

**Real fix is E1-side, not supersession:** either a deterministic guard rejecting
milestone/periodic invoice language for `annual_value` (mirroring the existing
`ContainsTerminationLanguage` pattern), and/or a distinct per-invoice claim key with its own
sum/period aggregation semantics instead of sharing `annual_value`'s single-slot supersession.
Until one of those lands, **`annual_value` for any invoice-billed vendor is a deterministic pick,
not a verified annual figure** — don't read it as ground truth in the demo without checking the
source document.

## Live-signal path "weak supersedes strong" is NOT a bug — it's the live-signal-report feature

An earlier pass through this file described `IiFacade.SubmitSignalAsync`'s unconditional
supersession (any new signal for a `(EntityId, Dimension, Criterion)` slot always supersedes
whatever was current, regardless of `SourceTier`) as "the mirror of the KYV-path bug" — a
data-loss defect worth fixing. That framing was wrong and has been corrected here.

`IiFacade.AnchorConfidences` exists specifically so a weaker-tier signal **can** become current
over a stronger one: it floors the new belief's confidence at the strongest still-valid
predecessor's decayed confidence, so "corroborating bad news must not demote the band." That
*is* the live-signal demo feature — a CSM's free-text report about a known issue is supposed to
become the current, authoritative belief for that criterion, with confidence protected against
unfair collapse, not blocked from taking effect. This is exercised end-to-end by
`Kozmo.Api.Tests/OTests.O1_LiveSignal_CreatesBeliefAndRecomputesEngine` (the actual
`/demo/live-signal` endpoint test) and pinned by two golden fingerprints
(`GoldenStreamB3Tests.P1_Corvus_WithLlmBelief_FingerprintPin`,
`LlmStreamTests.L5_Cloudwave_WithLlmBelief_FingerprintPin`) plus
`MetaCognitionTests.T10_AnchorProvenance_ExposedInReasoningTrail`, which asserts the anchor's
provenance fields are populated after exactly this kind of reversal.

A tier-then-recency winner-take-all fix was built and tested against this path (mirroring the KYV
fix's mechanism) and confirmed to close the literal "weak overwrites strong" scenario — but doing
so removes the reversal `AnchorConfidences` depends on, which broke both golden pins, the T10
provenance test, and the O1 demo-feature test. Those failures are correct: they were protecting
intended behavior, and updating them to match the fix would have been encoding a regression, not
fixing one. The fix was reverted in full (`IiFacade.SubmitSignalAsync`, plus its new tests) rather
than kept and the tests "corrected." No fix is needed here — this is confirmed working as
designed.

## E1 Part 7 Step 3: invoice schema fixed the annual_value confusion at extraction — two new gaps surfaced, both deferred, both documented here rather than papered over

Step 3 wired a document-type → extraction-schema mapping (invoice / msa / order_form) and gave
invoices their own `invoice_amount` claim key instead of `annual_value`, fixing the type-confusion
described in the "Supersession fix" section above at its root. Real-corpus proof: IIVS's 6
invoices now extract `invoice_amount` (same values as before, just correctly typed), one of them
(`Invoice_IIVS-INV-2023-0003.txt`) newly and correctly extracts `invoice_amount = 18100` where it
previously abstained on `annual_value` entirely (verified against the source: "TOTAL DUE
$18,100.00" is genuinely present), and Salesforce's Order Form is untouched (`annual_value =
214500`, same cache key, zero live calls). Re-record was scoped exactly to the 12 invoice-type
documents (454→466 belief-extraction cassette entries, 0 removed) plus 8 downstream answering-cassette
entries for IIVS's changed belief set — confirmed via a full before/after diff of the entire 152-doc
corpus, not just the invoice folder.

**The discipline this proved out:** `BeliefExtractionPromptGenerationTests`'s hash-identity check
(Step 2) proves PROMPT TEXT stability for a given key set — it says nothing about extraction
stability once a document's key set actually changes. Swapping `annual_value` for `invoice_amount`
in the invoice schema changes the prompt's overall composition, and that composition change can
shift what the model does with a key that itself DID NOT CHANGE (`renewal_date` — see below). The
acceptance gate for any future schema change is therefore a full before/after extraction diff
across every document of the affected type(s) — not just the hash test, and not just the intended
key swap. That diff was run exhaustively here (all 152 documents, all criteria) and found exactly
three deltas: the 12 intended relabels, the 1 newly-grounded `invoice_amount`, and the one
unintended shift documented next. Nothing else moved.

### New gap 1 — invoice schema composition change caused a real mis-extraction on an untouched key (renewal_date)

`Invoice_RGL-2023-002.txt` (Scenario 03) gained a `renewal_date` belief
(`1691971200` = 2023-08-14) that it did not have before the invoice schema change. The source text
contains `Due Date: 14 August 2023` — the model read this invoice's PAYMENT due date as a CONTRACT
renewal date. This is a real mis-extraction, the same class of error as the `annual_value`
milestone confusion (`ContainsMilestoneLanguage`) and the `payment_terms` termination-notice
confusion (`ContainsTerminationLanguage`) — a raw date/amount that is unambiguous in isolation gets
swept into the wrong criterion once it sits in a document type asking about it under different
surrounding context. Unlike those two, this was not present before Step 3 on the identical
document under the default (pre-invoice-schema) prompt — it is a genuinely new failure mode
introduced by giving invoices their own schema, not a pre-existing bug this step happened to
surface.

**Deferred, not fixed:** a `renewal_date` guard analogous to `ContainsMilestoneLanguage` —
candidate rule: reject `renewal_date` evidence whose surrounding text reads as an invoice
due-date/invoice-date line (e.g. contains "due date" or sits inside an invoice-schema document at
all, since invoices structurally have no contract renewal date). The narrower fix — since
document type is now known at extraction time — may simply be to exclude `renewal_date` from the
invoice schema entirely once that's confirmed safe against the corpus; that wasn't done here to
keep Step 3 to exactly the one intended key swap. Does not affect any current pinned test — RGL/
Regulus is not part of any golden or completeness-proof assertion — but is a real quality
regression on that document's belief set and should not be forgotten.

### New gap 2 — extraction confusion fixed, but the SAME confusion reappears one layer up, at answering

On the real IIVS document set, `saas.fin.l1.2` ("what is the total annual contract value") now
answers `18000` at confidence 0.80 (previously `UNKNOWN`) — grounded in whichever single
`invoice_amount` belief currently survives the same-tier supersession collision described in the
"Supersession fix" section above (the identical collision mechanism, now recurring under the new
key name instead of `annual_value`). The extraction layer is correct — that belief is honestly
labeled `invoice_amount`, a one-time transaction figure, not a contract's annual value — but
`QuestionAnsweringStage`/`AnsweringPrompt` still hands the model every Financial-dimension belief
without telling it which claim keys are valid grounding for an "annual value" question, so the
model does its best with what it's given and answers a specific-sounding number that doesn't
actually answer what was asked.

**This is an E2 item, not an E1 fix:** the Kozmo_Phase_E1_Spec.md's deferred "deterministic
retrieval" work (Part 2.5, E1 Step 6) filters the belief pool a question sees by declared claim
keys — extending that filter (or the question bank itself) to explicitly exclude `invoice_amount`
from annual-value-style questions is the natural fix, and belongs with E2's question-bank
work, not E1's extraction work. Capturing here so it isn't lost: **the question bank needs
invoice-awareness** — it should not offer a one-time invoice figure to answer a question about a
recurring annual figure, the same way `RubricModule` must never average a structural belief into a
dimension score.

## E1 Part 7 Step 5→6: metadata extraction is multi-pass, not one-pass — the attention wall

Step 5 tried the obvious design: one LLM call per document, asking about the document type's
belief facts AND all of its metadata fields together. For MSA (5 belief keys + 18 metadata
fields), this gave **near-zero metadata recall** — the real IIVS MSA (the richest document in the
corpus; a keyword grep against its source text confirmed most of the 18 clause types are
genuinely stated) yielded **0 metadata fields**, twice, even after fixing an unrelated
document-truncation bug (`MaxDocChars` was silently cutting the 41,000-character document off
before reaching most of its clauses) and adding a worked example to the prompt disambiguating
"facts" from "metadata". A real, separate regression also surfaced from the same combined prompt:
an insurance policy limit got extracted as `annual_value` on the same document (fixed with a new
`ContainsInsuranceOrLiabilityLanguage` guard, same pattern as `ContainsMilestoneLanguage`).

**Diagnosis, not guesswork:** a targeted single-document experiment split the same 18 fields into
3 ad hoc groups on the same document. A 5-field group hit **100% recall**; an 8-field group hit
**38%** and missed fields present in the source text. This isolated the real cause — the model
(GPT-4o-mini) cannot reliably hold ~18 simultaneous extraction categories in attention across one
response, regardless of prompt wording, document truncation, or worked examples. It is not a
recall/capability floor (the 100%-recall group proves the model finds this content just fine) —
it is a **category-count-per-call** ceiling.

**Step 6 fix — multi-pass, general, config-driven:** `DocumentBeliefExtractor` now makes one
isolated belief call (unchanged, never combined with metadata — see `Kozmo_Phase_E1_Spec.md` §5.4)
plus one call per metadata field **group** the document type declares in
`extraction_schemas.saas.v1.json` (`metadata_field_groups`, ~4-5 fields each), unioning the
results. MSA's 18 fields became 4 groups. Real-corpus result: IIVS went from 0 to **14/18** —
verified complete recall, the 4 "misses" (`liability_cap`, `sla_definition`,
`data_processing_terms`, `non_solicitation`) confirmed genuinely absent from the source text, not
model failures. This is now the documented architecture and design parameter (`Kozmo_Phase_E1_Spec.md`
§5.4) for every future document type's metadata depth work — invoice/PO/Order Form metadata is a
catalogue change (declare their groups at ~4-5 fields each), not a new architecture.

**Bonus, not the goal:** separating the belief call from metadata entirely also structurally
closed the composition-ripple risk that surfaced independently twice — Step 3's invoice-schema
change misreading an unrelated `renewal_date` on one document, and Step 5's combined prompt
picking up two extra belief facts on IIVS/Regulus that Step 3's prompt never produced. With
beliefs and metadata in permanently separate LLM calls, a metadata schema change cannot perturb
belief extraction even in principle — proven, not just argued, by a full 152-document corpus diff
showing zero belief-line differences after the Step 6 re-record (235 facts, byte-identical to the
pre-Step-5 baseline).

### Two real, separable findings surfaced while investigating (not the phantom bug — queued for later)

- **Golden gate has a matching gap.** `LlmStreamTests.L5_Cloudwave_WithLlmBelief_FingerprintPin`
  carries `[Trait("Golden", "true")]`, but the documented gate command
  (`dotnet test Kozmo.sln --filter "Golden"`) is a name-substring filter — it only catches
  `GoldenStreamB3Tests.P1` because "Golden" happens to appear in that class's name.
  `LlmStreamTests` doesn't contain "Golden" anywhere in its class or method names, so L5 silently
  escapes the documented gate despite being tagged as a golden pin. Worth fixing so a golden
  fingerprint pin can't drift undetected by the command everyone actually runs — likely means
  switching the gate to filter on the `Golden=true` trait instead of a name substring.
- **`EvidenceFusionTests.Q2`/`Q3` pass vacuously today, regardless of this investigation or its
  (reverted) fix.** Flagged for a closer look someday — worth confirming they're still testing
  what their names/comments claim (the confidence-anchor chain-walk actually firing and mattering)
  rather than asserting an invariant that happens to hold true anyway. Not chased further now.
- **The "generated" contracts' codegen script doesn't exist.** Every file under
  `libs/Kozmo.Contracts/Generated/` (`Enums.cs`, `Belief.cs`, `EntityIndex.cs`, etc.) is headed
  `// GENERATED — do not hand-edit; regenerate via tools/codegen/generate.ps1`, and
  `STEP_1_0_contract_amendment.md` describes a schema-change → codegen → compile workflow. No such
  script exists anywhere in the repo (`tools/codegen/` is absent) — these files are, in practice,
  hand-maintained, and `schema/*.json` has already silently drifted from them independent of any
  recent work: `schema/belief.schema.json`'s `SourceTier` enum list was missing `Primary` (present
  in `Enums.cs` since some earlier phase) before the E-signal `Correspondence` addition touched
  either file. Discovered while adding `Correspondence` to `SourceTier`
  (`5c3b27f`) — both files were updated for the new member, consistent with each other, but the
  pre-existing `Primary` gap was left alone (out of scope for that change). Same class of finding
  as the golden-gate substring gap above: a documented mechanism that silently isn't what the docs
  say it is. Worth eventually either writing the codegen script for real or updating the header
  comments / `STEP_1_0_contract_amendment.md` to describe the actual (manual, dual-file) discipline
  — not chased further now.
- **Email threading is implicit, not header-based — the E-signal spec's thread-awareness section
  assumes something the real corpus doesn't have.** Surveyed all 338 real `.eml` files while
  building `EmailParser` (E-signal Part 5 Step 2, `fb28037`): zero files carry an `In-Reply-To` or
  `References` header. `Scenario 07`'s 300 emails (the corpus this matters most for) are ordered by
  filename (`0001_...`, `0002_...`) and by `Date`, not by MIME reference chains.
  `Kozmo_Phase_E_Signal_Spec.md` §3.4/Part 5 Step 7 ("Thread awareness") is written assuming
  reference-header-driven threading — that assumption doesn't hold here. **Spec-affecting, not just
  a code gap:** before Step 7, decide one of (a) derive threading from filename sequence +
  subject-line similarity + participant pair (corpus-specific, works today, brittle to a corpus that
  numbers files differently), (b) support both real reference headers and the heuristic fallback
  (more robust, more work), or (c) defer thread-relative signals (responsiveness-across-a-thread,
  reply-relative sentiment) until real threading data exists, scoring only per-message signals for
  now. `ParsedEmail.InReplyTo`/`References` are already on the struct and will be empty/null for
  every message in this corpus — not a parsing bug, an honest reflection of the source data. Update
  §3.4 before Step 7 starts.
- ~~**The two hardcoded tier-ceiling fallback switches still need a `Correspondence` case.**~~
  **RESOLVED at Step 4.** `SqliteEntityStore.FallbackVendorFileTierRank` and
  `AnsweringPrompt.FallbackTierCeiling` both gained a `SourceTier.Correspondence => 0.25` arm,
  consistent with `source_tiers.saas.v1.json`. Flagged at Step 1 (`5c3b27f`), carried forward at
  Step 3, closed here now that correspondence-tier beliefs are imminent (Step 5).
- **`annual_value` has now attracted a wrong figure at FOUR separate build stages — the deny-list
  pattern itself may be the problem, not any one keyword.** Chronology: (1) milestone/per-engagement
  invoice amounts (IIVS's invoices, E1 Part 7 Step 3), (2) insurance/liability/indemnification policy
  limits (the real IIVS MSA, E1 Part 7 Step 5), (3) both consolidated into ONE
  `ContainsAnnualValueExclusionLanguage` guard (E1 Part 7 Step 7), (4) hedged/negotiation-in-progress
  proposal figures (0006_pricing.eml, "roughly... approximately... starting point... as we finalize"
  — E-signal Part 5 Step 5, `ContainsHedgedProposalLanguage`). Four independent real-world confusions,
  four keyword additions to what is structurally the same fix each time: another way for a dollar
  figure to look like an annual contract value without being one. **This is a signal the base
  `annual_value` definition (catalogue: "an explicit contract price or subscription fee paid by the
  customer") is too permissive — a growing DENY-list (reject known-bad phrasings) is chasing an
  unbounded space of wrong-figure shapes one proven confusion at a time.** An ALLOW-list framing —
  only extract when the evidence contains contract-value language itself ("annual contract value",
  "ACV", "total contract value", "annual fee", "subscription price") rather than merely "a dollar
  figure that isn't one of the four excluded kinds" — may be structurally more precise and stop
  requiring a new guard per newly-discovered confusion. Not fixed now — a base-definition change is
  bigger than a reactive guard and needs its own design pass (re-record every affected cassette,
  re-verify recall on the full corpus). Deferred to E-docdepth or E2, captured here so it isn't lost
  and so a fifth confusion doesn't just add a fifth keyword without anyone asking whether the pattern
  itself has run its course.
- **Email belief extraction has a THIRD failure class distinct from the two above: semantic-field
  confusion — the right number, extracted under the wrong claim key's role.** Unlike hedging
  (Fix 1/2, a number that's real but not yet settled) or a missing/wrong value (Fix 2, a number
  that isn't stated at all), this is a number that IS settled and real, but answers a different
  question than the claim key asks. Two proven instances from the full-338 audit, both fixed
  reactively (Fixes 3/4a): (a) `01_Contract_Kickoff_Mar2021.eml` — "our standard invoice cycle is
  monthly, submitted within the first 5 business days" gave `payment_terms=5`, but this describes
  when the VENDOR issues invoices, not how long the CUSTOMER has to pay one; (b)
  `05_Year_End_Review_Dec2022.eml` — "Total invoiced: $153,950 (per submitted invoices
  RGL-2022-001 through RGL-2022-004)" gave `invoice_amount=153950`, but this is a year-total
  across four invoices, not one invoice's amount. A third, incidental instance surfaced during
  Fix 3/4 verification (not yet guarded): a fresh live call on `01_MSA_Execution_Confirmation_
  Apr2022.eml` — an email whose three PRIOR independent extractions (both Step 5 sample rounds
  and the full-338 run) never produced a `renewal_date` — this time extracted
  `renewal_date=1745452800` from "has been fully executed as of 24 April 2025", which states the
  MSA's EXECUTION/signing date, not a renewal date. Whether this specific case needs its own
  guard (extending `ContainsInvoiceDateLanguage`'s pattern to "executed as of"/"fully executed"
  language) is undecided — flagged here, not fixed, pending a decision on whether to keep patching
  per-instance or address the pattern directly (see below). Not caught by the day-count or
  hedged-language guards because the SHAPE is correct — a real digit, a real dollar figure, a real
  date — only the semantic ROLE is wrong. **Keyword guards patch this reactively, and the deny-list
  will keep growing one confusion at a time** (the same dynamic as the `annual_value` entry above,
  now proven for a different reason — wrong role, not wrong settledness). The more durable fix is
  a semantically-explicit prompt per claim key (e.g. spelling out payment_terms as "the CUSTOMER's
  obligation, never the VENDOR's billing cadence," invoice_amount as "ONE transaction, never a
  period total," renewal_date as "when the agreement renews or is next due for renewal, never when
  it was first signed") rather than another negative-example keyword after each new instance is
  found. Deferred alongside the `annual_value` base-definition rethink — same root cause
  (permissive claim-key definitions relying on guards to narrow them after the fact), same
  recommended fix shape (make the definition precise up front), same deferral target (E-docdepth
  or E2).

## E-signal Part 5 Step 6: email-only identity resolution has a real precision gap — Brookfield/OfficeSpace

Wiring `.eml` ingestion unconditionally into `KyvProgramRunner` (before the `processEmail` opt-in
flag was added) broke two pre-existing, document-corpus-calibrated regression tests:
`ProgramRun_All6Scenarios_VendorSet_NoTimeout` (asserted "Brookfield" — a customer, not a vendor —
never appears in the resolved vendor set) and
`ProgramRun_AbcIdentityAnswerYes_MergesLive_Absorbed_NotDeleted` (assumed the first
`IDENTITY_CONFIRM` check-in is the ABC pair).

**Root cause, confirmed on real data (Scenario 07, 300 emails, email-only):**
- `ClusteringStage.AggregateEntityRole`'s highest-tier-members-vote mechanism depends on the LLM's
  per-email role hints being consistent. Email-only correspondents get their role inferred purely
  from free-text party/role extraction (`DocumentCandidateExtractor`, reused as-is for email
  identity per spec §2.4 Decision 3) — there is no filename/doc-type signal the way documents have.
  Across 300 real emails between OfficeSpace Software (vendor) and Brookfield (customer), enough of
  Brookfield's role hints come back `unknown` rather than `customer` that the aggregate vote lands
  on `unknown` for the cluster as a whole.
- `IdentityGate.IsNonVendorRole` only filters `customer`/`issuer`/`internal` — **not** `unknown` —
  so a customer that role-aggregates to `unknown` is never excluded and surfaces as a resolved
  vendor. This is arguably correct conservative behavior for a genuinely ambiguous entity, but it
  is wrong here because the true role (`customer`) is knowable from the corpus; the aggregation
  step is losing the signal, not the underlying data lacking it.
- Separately, name variance ("Office Space Software" vs "OfficeSpace") across different emails
  fragments what should be one vendor cluster into two (`Office Space Software` role=unknown,
  `OfficeSpace` role=vendor in the Step 6 full-wire dump) — a fuzzy-match miss at
  `ClusteringStage`'s `MergeThreshold=0.90` for this specific name pair.

**Not fixed — deferred by design.** `Ig.Resolution` (Clustering/IdentityGate) is Dev A's
heavily-tested core identity logic, calibrated against the document-only corpus; patching its
vote/threshold behavior reactively for one email scenario risks silently degrading the document
path it was proven against. Instead, email processing was gated behind a `processEmail` opt-in
constructor parameter (`KyvProgramRunner`, default `false`) so every existing document-only caller
is provably unaffected (verified: `Kyv.ProgramRunner.Tests` 17/17 green, including both previously
broken by the unconditional wiring; a before/after diff of the full document-only real corpus dump
is byte-identical modulo one cosmetic blank line — see the Step 6 report). The real fix — either
teaching `AggregateEntityRole`/`IdentityGate` to treat `unknown` more conservatively for
correspondence-tier-only clusters, or tightening fuzzy-match for common SaaS-vendor name
abbreviations — is out of scope for Step 6 and needs its own design pass against BOTH corpora
(document and email), not just Scenario 07.

## E-signal Part 5 Step 6: multi-tier corroboration on the SAME criterion revives the Commit-3 UNKNOWN answer

The "Commit 3" section above documents `AnsweringPrompt.PresentationConfidence` fixing
`saas.fin.l1.1` ("is there a signed contract with defined payment terms?") to answer `YES` for IIVS
once a single `Verified`-tier `payment_terms` belief was present. Re-running the same question on
IIVS's Step 6 combined belief set (the SAME `Verified` doc belief **plus** a corroborating
`Correspondence`-tier `payment_terms=30` email belief, both citing "Net 30") produced `UNKNOWN`
again, on a real live GPT-4o-mini call — not the previously-fixed `YES`. Two candidate beliefs for
the same criterion, at different tiers, appears to have reintroduced the ambiguity the presentation-
confidence fix was meant to close, rather than reinforcing the answer as corroboration should.
Not investigated further — flagged here so it isn't mistaken for a completeness regression specific
to email (the underlying `AnsweringPrompt`/`QuestionAnsweringStage` code is untouched by Step 6);
it looks like a pre-existing multi-belief-per-criterion presentation gap that simply had no real
corroborating-tier data to expose it until now. Worth a closer look alongside the Commit-3 stopgap's
already-acknowledged "not the proper fix" status.

## Check-in loop verification (real HTTP, IIVS): answering a DIMENSION_GAP check-in processes but does NOT close the gap

Verified the full check-in loop end-to-end against the actual running `Kozmo.Api` process (real HTTP,
zero DI overrides, no new production code) on IIVS's real "does the vendor have a documented uptime
SLA?" gap (`saas.op.l1.1`). The mechanical pipe works: raise → `GET /checkins` lists it → `POST
/checkins/{id}/answer` returns `200 Ok` → the check-in transitions to `PROCESSED` → a new belief is
written. But the belief that gets written is malformed, and completeness correctly refuses to close
the gap on it.

**Root cause:** `ProcessDimensionGapAsync` (`subsystems/workflow-coordination/dotnet/Wc.CheckIn/
ProcessCheckInService.cs:143-173`) does:
```csharp
var claimKey = checkIn.TargetField ?? "human_answer";
if (!string.IsNullOrEmpty(checkIn.TargetField)
    && profile.ClaimKeyCatalogue.TryGetValue(checkIn.TargetField, out var ckDef)
    && Enum.TryParse<Dimension>(ckDef.Dimension, ignoreCase: true, out var catalogueDim))
    dimension = catalogueDim;
```
`checkIn.TargetField` is set by `GapCheckInStage.RaiseAsync` (`Ii.Completeness/GapCheckInStage.cs:66`)
to `q.Id` — the **question ID** (`"saas.op.l1.1"`), not a claim key. `saas.op.l1.1` is never an entry
in `claim_key_catalogue.saas.v1.json` (real entries look like `sla_uptime`, `csat`, `payment_terms`),
so the catalogue lookup always misses. Result: `dimension` silently defaults to `Financial`
(regardless of the question's real dimension — Operational, in this case), and `WriteBeliefAsync` is
called with `claimKey = criterion = "saas.op.l1.1"`. Since that's not a catalogued key, it isn't
flagged `structural`, so confidence is NOT zeroed (stays at the passed `0.5`), and no explicit
`derivation:` argument is passed, so it falls back to `WriteBeliefAsync`'s generic template:
`"vendor-file:saas.op.l1.1"`. Net belief written: `[Financial] saas.op.l1.1 = 1, tier=Reported,
conf=0.5, derivation="vendor-file:saas.op.l1.1"` — nothing in any of these fields says "SLA," "uptime,"
or "yes." When completeness re-runs (`AnsweringPrompt.SerializeBeliefs` shows the LLM exactly
Dimension/Criterion/SourceTier/Confidence/Derivation, verified via a real live GPT-4o-mini call),
there is zero semantic bridge from this belief back to "Does the vendor have a documented uptime
SLA?" — the model correctly stays `UNKNOWN`, cites nothing, and `saas.op.l1.1` remains in
`GapQuestionIds`, never moving to `AnsweredQuestionIds`.

**Contrast, confirming the break is localized:** `saas.fin.l1.1` ("is there a signed contract with
defined payment terms?"), grounded in a REAL catalogued `payment_terms` belief written by the normal
KYV extraction path (not by `ProcessDimensionGapAsync`), answered `YES` at `conf=0.80` with a real
citation throughout this same test run. The completeness/answering machinery itself is fine — the
defect is specifically `ProcessDimensionGapAsync`'s claim-key resolution for DIMENSION_GAP answers,
which has effectively never worked for any question whose ID isn't coincidentally also a valid
catalogue key (none are).

**Not fixed — logged for a fix-size assessment first**, since this touches the check-in answer path
that the demo's check-in loop relies on. See the fix assessment below.

### Follow-up: the derivation-only fix was tried and reverted — it fixes the semantic grounding, but a SEPARATE confidence-ceiling gate still keeps the gap open

Tried the smallest safe fix: pass a real `derivation:` argument into `ProcessDimensionGapAsync`'s
existing `WriteBeliefAsync` call — `$"Check-in answer to \"{checkIn.Question}\": {checkIn.ResponseValue}"`
— instead of relying on the generic `"vendor-file:{claimKey}"` template. One call site, one new
argument, no claim-key/dimension/value/schema change. Verified live against the real running API
(same harness as the original verification):

- **The semantic-grounding half of the fix worked exactly as predicted.** With the real derivation
  text present, the completeness LLM correctly answered `saas.op.l1.1` (`"Does the vendor have a
  documented uptime SLA?"`) as **`YES`, citing the check-in-derived belief by name** — a real
  improvement over the prior `UNKNOWN`/uncited result. `Derivation` genuinely is the only field the
  LLM needs; the mislabeled `Dimension=Financial` and opaque `Criterion="saas.op.l1.1"` did not stop
  it from grounding once given real semantic content.
- **The gap still did not close.** `saas.op.l1.1` stayed in `GapQuestionIds`, never moved to
  `AnsweredQuestionIds`. Root cause is `CompletenessRubric.Compute`
  (`Ii.Completeness/CompletenessRubric.cs:24`): a question counts as answered only when
  `answer.Confidence >= question.RequiredConfidence`. Every L1 question requires `0.60`
  (`SaasQuestionBank.cs`). `ProcessDimensionGapAsync` always writes DIMENSION_GAP-derived beliefs at
  `SourceTier.Reported`, whose ceiling is `0.50` (`source_tiers.saas.v1.json`) — structurally below
  the L1 bar. This is the SAME `REPORTED weight (0.50) < CRITICAL gate (0.60)` invariant CLAUDE.md
  documents as Invariant #4 (confidence discipline) — working exactly as designed, just not in the
  direction this feature needs: **a DIMENSION_GAP check-in answer can never, by architecture, clear
  an L1 completeness question's confidence bar, no matter how well-grounded its content is.**
  Contrast: `saas.fin.l1.1`'s `payment_terms` belief is `Verified`-tier (ceiling `0.80`), which clears
  `0.60` easily — that's why it closes and this doesn't. No regression there — it answered `YES,
  conf=0.80, cited=2` both before and after this fix, unchanged.

**Reverted in full** (`ProcessCheckInService.cs` back to the original generic-template derivation,
the additively-recorded cassette entries checked out) — the fix didn't clear the stated bar
("`saas.op.l1.1` moves to `AnsweredQuestionIds`"), so per the decision rule it was pulled rather than
partially kept. Full suite green, golden 26/26, CI 9/9 confirmed after revert.

**Why this isn't a quick follow-up fix:** closing this properly means either (a) raising
`SourceTier.Reported`'s ceiling above `0.60` — which isn't a check-in-path-local change, it's a
catalogue-wide tier-ceiling change (`source_tiers.saas.v1.json`) that would also loosen every OTHER
Reported-tier belief's ability to clear confidence gates across the whole system, a real scoring-
discipline decision Invariant #4 was specifically written to prevent, not a bug; or (b) inventing a
NEW, higher-trust source tier specifically for "human operator confirmed via check-in" (distinct from
"someone reported this to us" Reported-tier), which is a genuine new business-rule/catalogue decision
("ask, don't invent" territory) — not a mechanical fix. Both are out of scope for a demo-day change.
**Net for the demo: DIMENSION_GAP check-ins process correctly (raise → list → answer → belief written,
now with real semantic content) but will never visibly close a completeness gap in the current
architecture — do not demo "answer a gap → watch it disappear" with a DIMENSION_GAP question. The
payment-terms-style "real, already-catalogued evidence closes a gap" path is unaffected and remains
the correct thing to demo.**
