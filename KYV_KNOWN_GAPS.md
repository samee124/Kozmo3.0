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
