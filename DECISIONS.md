# DECISIONS.md — Durable Architectural Choices

These decisions were made in prior design sessions and are not visible in the code itself.
Read this before proposing structural changes — many "improvements" re-litigate a settled choice.

---

## 1. Plan A: deterministic 4-dimension scoring, not an LLM Q&A graph

**Decision:** The cognition engine is a deterministic, rule-based pipeline — signal → belief → weighted dimension score → confidence-gated band → posture rule → stance. It is not an LLM that answers free-text questions about vendor health.

**Why:** The townhall demo requires a *glass box*. Buyers and finance need to see the exact evidence and arithmetic that produced a stance, not a model's paraphrase. A reproducible fingerprint is the load-bearing proof: same evidence in → identical posture out, every run. An LLM Q&A graph cannot provide this guarantee without additional determinism infrastructure that would take longer to build and harder to audit.

**What this means in practice:** The five modules (Observation, Rubric, Index, Posture, Decay) are pure functions over typed inputs. No model client, no prompt, no sampling. If a task seems to require an LLM in the core scoring path, the spec has been misread — stop and ask.

---

## 2. LLM at the edge only, cached / record-replay in demo

**Decision:** The LLM assists with *reading* (classifying free-text signals, detecting contradictions) not with *deciding* (scoring, banding, stance). Its output is frozen into a typed belief the moment it runs; the deterministic spine downstream never calls it again. In the demo runtime path, LLM outputs are served from a frozen cache — `CachingLlmClient` throws on a cache miss rather than making a network call.

**Why:** Keeps the core deterministic (fingerprint holds) and the demo reliable (no live API dependency, no rate-limit surprises on stage). The LLM runs once during seed-prep; the demo replays from that frozen state.

**Structural consequence:** The real `LlmClient` (Anthropic SDK) must live in its own assembly — `Kozmo.Llm.Anthropic` — reachable *only* from seed-prep and smoke entrypoints. It is never imported by the demo runtime. CI Lane 5 enforces this at build time.

---

## 3. Soft edge, hard core

**Decision:** Classification (the *reading* step) may be probabilistic — rules first, LLM-assisted for free-text. Everything that *decides* — Rubric, Index, fingerprint, Posture — is deterministic and reproducible. The fingerprint is a hash over the *decision* (typed belief values, scores, weights, config version), not over the reading.

**Why:** This is the precise boundary between "glass box" and "black box." The LLM's uncertainty is captured in the `ClassificationConfidence` annotation field and surfaced in the drill-down; it does not leak into the stance arithmetic. Annotation fields (`ClassificationMethod`, `ClassificationConfidence`, `ReasoningSummary`, `Cautions`, `EvidenceGaps`) are explicitly excluded from the fingerprint input.

---

## 4. Split at contracts, not services — and periphery-first if splitting later

**Decision:** Subsystems are independently-owned modules behind frozen contracts, running in-process. No microservices, no message-bus topologies, no distributed coordination in Phase 0 or Phase 1.

**Why:** The bottleneck for the demo is correctness and explainability, not throughput or scale. In-process calls are synchronous, debuggable, and have no distributed-consistency surface. The seams (`IKozmoBus`, `IEntityStore`, `IKozmoLlm`) are designed as interfaces so each can be promoted to a remote call later without touching callers.

**If scale forces a split:** promote periphery first (intake, UI, reporting); keep the I&I ↔ K&M core in-process until last. Only split after a single-writer / versioned-belief spike proves distributed determinism holds (the fingerprint must still be reproducible across nodes).

---

## 5. Confidence discipline — the gate arithmetic is structural, not configurable

**Decision:** `confidence = tier_weight × freshness`, capped at `tier_weight`. The CRITICAL band requires `confidence_floor ≥ 0.60`. `REPORTED` tier weight is 0.50 — structurally below the gate. This means a single human-reported signal can *never* force CRITICAL, regardless of freshness.

**Why:** Prevents a single junior employee's informal report from triggering an escalation to a VP. The gate is structural (baked into the catalogue validator and the invariant test suite) so it cannot be accidentally bypassed by a config change without failing CI.

**Additional caps:** posture confidence is clamped at 0.95. When meta-cognition detects active contradictions, posture confidence is reduced: `Clamp(index.Confidence - 0.1 × contradictionCount, 0.0, 0.95)`. This lower-confidence caution surfaces in the drill-down without changing the stance taxonomy.

**Anti-proliferation:** meta-cognition contradictions and gaps do *not* mint new stances (`Reconfirm`, `Investigate`, etc.). They attach as `Cautions` and `EvidenceGaps` on the `PostureAssignment`. The stance answers *what to do*; the cautions answer *how sure / what to check first*. Proliferating the taxonomy would duplicate what confidence + cautions already express.

---

## Step 1.0a (erratum) — MetaCognitionResult threaded into IPostureModule.Assign

**What Step 1.0 did:** Added `Cautions`/`EvidenceGaps` init-only properties on `PostureAssignment`, documented the confidence penalty rule (`Clamp(index.ConfidenceFloor - 0.1 × contradictionCount, 0.0, 0.95)`), and generated the `MetaCognitionResult`, `Contradiction`, and `Gap` contract types.

**What it omitted:** Threading `MetaCognitionResult?` as an input parameter into `IPostureModule.Assign`. Without the parameter the module could not receive the meta-cognition output, making Phase A2 (wiring the penalty) impossible to test.

**Fix:** Added `MetaCognitionResult? meta = null` as the final optional parameter to `IPostureModule.Assign` and matched the `PostureModule` implementation signature. `PostureModule` ignores `meta` until A2 implements consumption.

**Scope:** Internal to I&I. `IIiFacade` is unchanged (Spine holds the `MetaCognitionResult` internally). All existing callers are positional and unaffected by the `null` default. Fingerprint inputs, golden bands/stances, and Dev B's surface are all unaffected.

---

## A3 erratum — trajectory methods added to IEntityStore and IIiFacade

**What A3 needs:** The `/vendors/{id}/trajectory` endpoint exposes one data point per processed signal (composite score, band, stance, fingerprint). The store already retains all index and posture rows (append-and-supersede), and signals are kept forever — the data existed; only the read path was missing.

**What was added:**

| Layer | Addition |
|---|---|
| `IEntityStore` | `GetPostureHistoryAsync(entityId)` — all postures ordered by `assigned_at` |
| `IEntityStore` | `GetSignalsForEntityAsync(entityId)` — all signals for entity ordered by `received_at` |
| `IIiFacade` | `TrajectoryPoint` record (Timestamp, SignalId, Composite, Band, Stance, Fingerprint) |
| `IIiFacade` | `GetTrajectoryAsync(entityId)` — joins index history + posture history; correlates signal by position |

**Signal ↔ index version correlation:** Signals are ingested in received_at order; each produces exactly one index version increment. So `signals[V-1].Id` is the signal that triggered index version V. This position-based join is deterministic given the append-only constraint.

**Scope:** Read-only additions. No existing method signatures changed. No fingerprint inputs added. No stance taxonomy changes. The `IIiFacade` contract change requires joint sign-off — surfaced here as the audit trail.

---

## A4 erratum — BandDrivenBy on EntityIndex (fingerprint-excluded metadata)

**What A4 needed:** The drill-down trail exposes `band.drivenBy` to show whether the final band came from the weighted composite alone or was pulled down by a single underperforming dimension.

**What was added:**

| Layer | Addition |
|---|---|
| `EntityIndex` | `string BandDrivenBy { get; init; } = "composite"` — non-positional init-only property |
| `IndexModule.Build` | `MoreSevere(compositeBand, worstBand)` → band; `worstBand > compositeBand` → `"worst-dimension-floor"` |
| `DtoMapper.ToIndexView` | `BandDrivenBy: idx.BandDrivenBy` (was hardcoded `"composite"`) |
| `DtoMapper.ToTrail` | `DrivenBy: idx.BandDrivenBy` (was hardcoded `"composite"`) |

**Algorithm:**

```
compositeBand = AssignBand(composite, confidenceFloor, profile)
worstDimScore = min score across dims with beliefs  (falls back to composite if none)
worstBand     = AssignBand(worstDimScore, confidenceFloor, profile)
band          = MoreSevere(compositeBand, worstBand)
BandDrivenBy  = worstBand > compositeBand ? "worst-dimension-floor" : "composite"
```

**The floor mechanism is real and tested — but no current seed vendor exercises it.** K2 (`Ii.Tests`) demonstrates it: composite Healthy (0.725), one dim at AtRisk (0.50) → final band AtRisk, `BandDrivenBy = "worst-dimension-floor"`. No seed vendor has this profile; all three are composite-driven:

- **Cloudwave** — composite 0.475, worst dim (Exp) 0.40; both AtRisk → `"composite"`.
- **Corvus** — all four dimensions score 0.20–0.35, well inside the Critical range (< 0.40). Composite is 0.275. Both `compositeBand` and `worstBand` resolve to Critical; neither elevates the other → `"composite"`. **Corvus is Critical because its composite is uniformly Critical, not because of any floor override.** The original claim ("Corvus is Critical via the floor gate") was false and is retired.
- **Meridian** — composite 0.70, all scored dims (Op, Fin) ≥ 0.80; both Healthy → `"composite"`.

**Fingerprint discipline:** `BandDrivenBy` is derivation metadata — excluded from `FingerprintInput`. The A1 pins (e5d0e9b9 / 7e7cf005 / 72237da0) are unchanged; `MoreSevere(compositeBand, worstBand)` produces the same final band for the seed data as the old `AssignBand(composite, floor, profile)`.

**Scope:** No method signatures changed. No fingerprint inputs added. No stance taxonomy changes. Old `EntityIndex` rows in SQLite deserialize with `BandDrivenBy = "composite"` (the default).
