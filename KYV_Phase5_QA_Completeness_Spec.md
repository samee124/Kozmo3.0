# KYV — Phase 5 Frozen Spec: The Q&A / Completeness Engine

**Why this phase exists.** Phases 1–4 built the mechanical pipeline: it discovers, resolves,
and persists vendors, and detects *some* gaps ad hoc. It does not yet KNOW what a complete
vendor file requires, or measure a vendor against that standard. That knowing is the core
intelligence of KYV — the difference between "resolved vendors with a few gaps flagged" and
"a system that knows what it needs to know about a vendor and systematically pursues it."
This phase builds that: a dimension-organized question bank that DEFINES completeness, an LLM
that ANSWERS those questions from the vendor's beliefs (the intelligence), a deterministic
rubric that judges completeness, and the gap→check-in→refill→re-answer loop that converges.

This is engine work. It belongs BEFORE UI, connector, and enrichment — those present or feed
the engine; this IS the engine's brain.

Derived from the platform's prior design (Commercial_Reality question bank + depth ladder;
Q&A-graph + MetaCognition.identify_gaps + dirty propagation), REBUILT under the current
soft-edge/hard-core discipline. The original design's "LLM per graph node" is preserved as
intelligence but relocated to a defined soft edge, cassette-backed like extraction.

---

## 0. The determinism boundary (the load-bearing decision)

Not everything is deterministic — and it must not be. There are now TWO soft edges:
1. **Extraction** (documents → beliefs) — Phase 2, built.
2. **Question-answering** (beliefs → answers) — THIS phase. The LLM reasons across all relevant
   beliefs to answer a completeness question, with a confidence and cited beliefs. This is the
   intelligence. Non-deterministic, correctly. Cassette-backed for test determinism.

Determinism lives at three fixed points (the "hard core"):
- **The question bank** — WHICH questions define completeness (doctrine, not LLM-decided).
- **The completeness rubric** — WHETHER the answers add up to "complete" (arithmetic over
  answers+confidences). This is the decision seam.
- **The loop mechanics** — which gaps become check-ins, how answers re-enter, how completeness
  recomputes.

So: **the LLM answers questions (intelligence, non-deterministic); the question set, the
completeness judgment, and the loop are deterministic.** Same shape as extraction — LLM at a
defined edge, deterministic core downstream.

## 1. The question bank (doctrine — deterministic)

- A **fixed, versioned question bank**, authored (not LLM-generated), organized **per
  dimension**. The dimensions are the FOUR that exist in the codebase (Dimension enum,
  catalogue, EntityIndex.DimensionScores): **Operational, Experiential, Financial, Strategic**.
  NOT six — the old design's "relationship" and "compliance" do not exist in code and are NOT
  added here (that would be a contract/enum change off the critical path). Compliance or
  relationship concerns, if needed, live as CRITERIA within these four dimensions, not as new
  top-level dimensions.
- Each Question: `id`, `dimension`, `text`, `answer_type`, `depth_level` (L1/L2/L3),
  `required_confidence` (the bar this question's answer must clear to count as "answered").
  `answer_type` maps to the EXISTING ResponseShape enum: YES_NO, TYPED_VALUE, STATUS_SELECT.
  FREE_TEXT is deferred (it reopens the free-text-response problem — a later soft-edge addition);
  keep completeness questions to the three structured shapes for now.
- **Selection is deterministic**: the question set for a vendor = (its category's bank) filtered
  by (its depth level). NOT generated per vendor. This is what makes "complete" a stable,
  auditable, comparable standard.
- Questions do NOT change when signals arrive. The bank evolves ONLY offline, between runs, via
  the Wisdom Loop (out of scope this phase — noted §7).
- **First version**: author a modest bank for the demo vendor category (SaaS/services), a
  handful of questions per dimension. The intelligence is in ANSWERING them well, not in having
  hundreds.

## 2. The answering stage (soft edge — LLM, cassette-backed)

- For each selected question, the LLM is given the question + ALL relevant beliefs for the
  vendor as evidence, and reasons to an **Answer**: `question_id`, `vendor_id`, `answer`,
  `confidence`, `cited_belief_ids`, `answered_at`, `is_dirty`.
- Goes through `CachingLlmClient` (same cassette mechanism as extraction) — recorded per
  (question, belief-set) so tests replay deterministically, no live calls. Cap the serialized
  belief-set size (analogous to extraction's `MaxDocChars = 15_000`) so the cassette key stays
  stable and the token budget is bounded.
- The answer is grounded: it cites which beliefs it used. Confidence reflects the grounding
  (a Primary-tier contract belief → higher-confidence answer than a single Reported belief).
- Questions with NO supporting belief → answered as UNKNOWN/low-confidence → these are the
  gaps (§4). The LLM does not invent; "I can't answer this from the evidence" is a valid,
  first-class answer.

## 3. The completeness rubric (the seam — deterministic)

- Given the set of Answers + confidences, deterministically compute completeness **per
  dimension** and overall:
  - A question is "answered" if its answer confidence ≥ its `required_confidence`.
  - A dimension's completeness = (answered questions / required questions) for that dimension
    at the vendor's depth level.
  - Overall "grounded enough" = coverage per dimension meets the bar for the decision at hand —
    NOT global 100%. (Design: "grounded enough to act is coverage across the vectors a specific
    decision needs.")
- Output: a **CompletenessProfile** per vendor — per-dimension coverage, the answered set, and
  the GAP set (unanswered/weak questions). Deterministic: same answers → same profile.

## 4. Gaps → check-ins (reuse the built loop)

- The GAP set (unanswered/weak decision-relevant questions) → each becomes a check-in via the
  EXISTING `raise_checkins` mechanism (Phase 3). The question's `text` is the check-in question;
  the `answer_type` shapes the response.
- This REUSES the check-in loop wholesale — no new coordination machinery. A completeness gap
  is just another source of check-ins alongside identity triage and the ad-hoc gaps.
- **check-in ≠ action** holds: these are information-gathering, no ledger entry.

## 5. The convergence loop (synchronous recompute + termination)

There is NO dirty-flag/queue in the codebase — the design is synchronous-imperative: a belief
change fires `RecomputeVendorAsync` immediately. Do NOT build a dirty-set/queue. Instead, HOOK
the Q&A re-answering into the existing synchronous recompute: when a vendor's beliefs change and
`RecomputeVendorAsync` fires, re-answer that vendor's affected questions and recompute its
completeness in the same synchronous pass. "Answers change, questions don't" still holds — it's
triggered synchronously, not via a dirty queue.

- gap → check-in → (transport) → human answers → `ProcessCheckInService` writes a Reported-tier
  belief and calls `RecomputeVendorAsync` (it already does) → that recompute now ALSO re-answers
  the affected questions (LLM, cassette) → completeness recomputes → converge.
- "Affected questions" = questions whose answer cited a changed belief, or whose dimension the
  new belief touches. Re-answer those; leave the rest.

**Termination (must hold, or the loop nags forever):**
- (a) An answered question does not re-fire as a check-in. New evidence may open a DIFFERENT
  gap, but never re-litigates an answered one.
- (b) Unanswerable/unanswered gaps are ACCEPTED as honest permanent gaps (human didn't reply,
  info doesn't exist) — flagged in the profile, NOT re-asked.
- (c) The **depth ladder** caps effort by stakes: L1 baseline questions for all vendors; L2/L3
  only for the vendors that matter. This is the LLM-cost governor AND the loop-termination
  governor.

## 6. Altitude, reuse/new

- **Subsystem:** Interpretation & Inference (this is inference over beliefs).
- **Module:** a NEW Completeness/Q&A module — owns a distinct responsibility nothing else does
  (defining and measuring completeness). Distinct from resolution (identity) and from raw
  extraction.
- **Stages:** `answer_questions` (soft edge, LLM), `compute_completeness` (deterministic seam),
  `raise_completeness_checkins` (reuses Phase 3 raise).
- **REUSE:** beliefs + tiers via `GetCurrentBeliefsAsync` (Km.Store), the four dimensions
  (EntityIndex), the check-in loop (Wc.*, `RaiseCheckInsStage` + `ProcessCheckInService`),
  CachingLlmClient + cassette (Kozmo.Llm), and the existing synchronous `RecomputeVendorAsync`
  trigger. **NEW:** the question bank (doctrine), the answering stage, the completeness rubric,
  the CompletenessProfile record, and the hook that re-answers affected questions inside the
  existing recompute pass (NOT a dirty-set/queue — none exists, none is built).
  **ADAPTER:** answer_type → the three existing ResponseShapes (FREE_TEXT deferred). Note:
  completeness gaps share the DIMENSION_GAP CheckInKind with ad-hoc gaps — fine mechanically;
  a distinct kind is a later observability nicety, not built now.

## 7. Scope OUT (this phase)
- The Wisdom Loop (offline refinement of the bank) — later, separate, never live.
- Per-category banks beyond the demo category — start with one.
- The query-a-vendor "Analysis & Q&A" UI tab — that's a different, later capability.
- Real email transport for the completeness check-ins — reuses in-app for now (Brevo later).

## 8. Test contract
- **Bank selection deterministic**: a vendor of category X at depth L1 selects exactly the L1
  X-questions — same input, same set.
- **Answering is cassette-backed**: `answer_questions` replays deterministically in tests; no
  live LLM call. A recorded vendor's answers are stable.
- **Answering is grounded**: an answer cites belief ids; a question with no supporting belief
  answers UNKNOWN/low-confidence (does not hallucinate).
- **Completeness rubric deterministic**: given a fixed answer set, the CompletenessProfile
  (per-dimension coverage + gap set) is reproducible.
- **Gaps become check-ins**: the gap set raises check-ins via the existing loop; answering one
  (human) → belief → dirties the answer → re-answer → completeness recomputes → the gap closes.
- **Convergence/termination**: (a) an answered question does not re-fire; (b) an unanswerable
  gap is flagged permanent, not re-asked; (c) depth ladder caps which questions fire.
- **check-in ≠ action** holds: no ledger entry from completeness check-ins.

## 9. The hard checkpoint
Run a resolved vendor (e.g. IIVS, which has rich evidence, or Regulus, which is deliberately
sparse) through the engine end-to-end:
- The LLM answers the dimension questions from the vendor's beliefs, grounded (cited), with
  sensible confidences.
- The completeness rubric produces a per-dimension profile: IIVS should score well (rich
  evidence), Regulus should show real gaps (sparse — the "scattered evidence" scenario).
- The gaps raise check-ins; answering one closes the gap on recompute (dirty → re-answer →
  profile updates).
- An unanswerable gap stays a flagged permanent gap, not a re-fired check-in.
Two-sided: a rich vendor shows high completeness; a sparse vendor shows honest, specific gaps —
and the gaps are the RIGHT ones (the dimensions the sparse vendor genuinely lacks).

## 10. Build order (commits)
1. **Commit 1 — the question bank (doctrine) + deterministic selection + the CompletenessProfile
   shape + the completeness rubric (deterministic).** No LLM yet — test selection and rubric
   against hand-supplied answer sets. Proves the deterministic core first.
2. **Commit 2 — the answering stage (soft edge, LLM, cassette-backed).** LLM answers questions
   from beliefs, grounded/cited, UNKNOWN when unsupported. Record a cassette for a couple of
   resolved vendors. Proves the intelligence, deterministically replayable.
3. **Commit 3 — wire gaps → check-ins (reuse Phase 3) + dirty-propagation + the convergence
   loop.** Answer a gap → belief → dirty → re-answer → recompute. Prove termination (answered
   doesn't re-fire; unanswerable stays flagged; depth caps).
