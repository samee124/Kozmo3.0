# STEP 1.0 — Contract Amendment (opens Phase 1)

**When:** immediately after Phase 0, before any Dev A / Dev B fan-out.
**Who:** joint (both devs + lead). Half a day.
**Why:** the Phase 0 contracts (Signal, Belief, DimensionScore, EntityIndex, PostureAssignment)
predate the decision to add LLM-assisted understanding and meta-cognition. This step adds the
few types that scope needs, re-freezes, and **must leave the Phase 0 fingerprints byte-identical.**

> Scope of 1.0 is **contracts + schema + codegen + determinism re-verification only.** No module
> logic. No LLM call. No detection logic. No UI. Those are the fan-out (Dev A / Dev B).

---

## The one decision to confirm in the session

**Meta-cognition does not add new stances.** The stance set stays the five from the catalogue
(`Maintain / Monitor / Renegotiate / Escalate / Remediate`). Contradictions and gaps surface in
two ways instead: (1) they lower confidence, which already flows through the confidence-gated
banding, and (2) they attach to the posture as explicit `cautions` and `evidence_gaps`, shown in
the drill-down ("conflicting evidence — verify before acting").

This keeps the taxonomy frozen (anti-proliferation) and keeps the deterministic stance logic
intact — the stance answers *what to do*, the cautions answer *how sure / what to check first*.
Confirm this before freezing. The alternative — minting `Reconfirm` / `Investigate` stances — is
not recommended; it proliferates the taxonomy and duplicates what confidence + cautions already
express.

---

## New types (add to `schema/`, codegen → `Kozmo.Contracts`)

```csharp
public enum ContradictionSeverity { Low, Medium, High }
public enum DetectionSource       { Deterministic, Llm }   // which path found it
public enum ClassificationMethod  { Rule, Lexicon, Llm }   // how a belief was classified

public readonly record struct Contradiction(
    string EntityId,
    string Dimension,                       // dimension/criterion the conflict is about
    string Description,
    ContradictionSeverity Severity,
    IReadOnlyList<Guid> ConflictingBeliefIds,
    DetectionSource DetectedBy);

public readonly record struct Gap(
    string EntityId,
    string Dimension,
    string Description,
    DetectionSource DetectedBy);

public readonly record struct MetaCognitionResult(
    string EntityId,
    IReadOnlyList<Contradiction> Contradictions,
    IReadOnlyList<Gap> Gaps,
    string EpistemicSummary);
```

---

## Amend `Belief` — annotation fields only

Add three fields so the drill-down can show "the model read this and concluded X at 0.7":

```csharp
ClassificationMethod ClassificationMethod,   // default Rule
double? ClassificationConfidence,            // raw method confidence, pre tier×freshness; null for pure rule
string? ReasoningSummary                     // LLM rationale; null when rule-classified
```

The composite `Confidence` field is unchanged and remains the decision-relevant value (the
classifier's confidence is folded into it, still capped at the tier weight).

### ⚠ Fingerprint exclusion — the load-bearing constraint

The three new `Belief` fields and **all** the new meta-cognition / caution fields are
**annotation, not decision input.** The fingerprint function must continue to hash only the
decision-relevant fields (dimension, criterion, value, composite confidence, tier, version) —
**never** `ClassificationMethod`, `ClassificationConfidence`, `ReasoningSummary`, `Cautions`,
`EvidenceGaps`, or anything on `MetaCognitionResult`.

**Verification gate:** after codegen, re-run the Phase 0 determinism spike and the three golden
vendors. The fingerprints must be **byte-identical to Phase 0**. If any fingerprint changed, a new
field leaked into the fingerprint input — fix it before proceeding.

---

## Amend `PostureAssignment` — surface meta-cognition

```csharp
IReadOnlyList<string> Cautions,        // from MetaCognitionResult.Contradictions
IReadOnlyList<string> EvidenceGaps     // from MetaCognitionResult.Gaps
```

Confidence rule (deterministic, applied in Posture — not in 1.0, but freeze the rule now):

```
posture.Confidence = Clamp(index.Confidence - 0.1 * activeContradictionCount, 0.0, 0.95)
```

Stance enum unchanged. Rationale builder will include cautions and gaps when present.

---

## Define `IKozmoLlm` (reserved in Phase 0 — now the real contract)

The interface is frozen here; the two implementations are Dev B's Phase 1 work, not 1.0.

```csharp
public interface IKozmoLlm
{
    Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default);
}

public readonly record struct LlmResult(object? Answer, double Confidence, string ReasoningSummary);
```

Implementations (Dev B, Phase 1):
- `CachingLlmClient` — cache key = stable hash of `model + system + user`. **Demo/test runtime: a
  cache miss is a hard error, never a network call.**
- `LlmClient` — real Anthropic. Reachable **only** from the seed-prep and smoke entrypoints.

---

## Reframe invariant #5 (CI)

Old: "no LLM, no network anywhere." New:

> **No live network call in the demo runtime path. LLM outputs are served from the frozen cache.**
> The demo/test path resolves `IKozmoLlm` to `CachingLlmClient` only; a cache miss there fails the
> run. The real `LlmClient` is reachable solely from the seed-prep / smoke entrypoint, which is not
> part of the demo runtime.

Write the new invariant text now; the CI lane that enforces it is Dev A's carryover (Phase 0 step
0.8), wired during fan-out.

---

## Definition of done for Step 1.0

- [ ] New types (`Contradiction`, `Gap`, `MetaCognitionResult`, three enums) added to `schema/`; codegen produces compiling C# records.
- [ ] `Belief` amended with the three annotation fields; existing seed + golden data updated with safe defaults (`Rule`, `null`, `null`).
- [ ] `PostureAssignment` amended with `Cautions` + `EvidenceGaps`; confidence rule recorded.
- [ ] `IKozmoLlm` + `LlmResult` defined (implementations deferred to Dev B).
- [ ] Invariant #5 text updated.
- [ ] **Determinism re-verified: Phase 0 spike + 3 golden vendors produce byte-identical fingerprints; 16/16 tests still green.**
- [ ] Contracts re-frozen and merged to main.

When every box is checked, fan out to Dev A (deterministic core + spine + CI) and Dev B
(LLM edge + data + UI).
