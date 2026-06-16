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

**Structural consequence:** The real `OpenAiLlmClient` (OpenAI SDK, GPT-4o-mini) must live in its own assembly — `Kozmo.Llm.OpenAi` — reachable *only* from seed-prep and smoke entrypoints. It is never imported by the demo runtime. CI Lane 5 enforces this at build time.

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

---

## B2 — LLM integration-gate slice (free-text signal end-to-end)

**What B2 added:**

| Layer | Change |
|---|---|
| `Kozmo.Llm` | `LlmDefaults` static class — single source for `Model = "gpt-4o-mini"` and `Temperature = 0f`; both record and replay read from here so cache keys always match |
| `CachingLlmClient` | Default params changed from literals to `LlmDefaults.Model / LlmDefaults.Temperature` |
| `OpenAiLlmClient` | `DefaultModel / DefaultTemperature` constants now resolve from `LlmDefaults` |
| `Ii.Contracts` | `ClassificationResult` extended with non-positional init-only annotation fields: `Method`, `MethodConfidence`, `ReasoningSummary` (same pattern as `BandDrivenBy` on `EntityIndex`) |
| `Ii.Observation` | `ObservationModule(IKozmoLlm? llm = null)` — optional LLM constructor. Rule path runs first; if no rule matches and payload has `"body"` key and LLM is configured, calls LLM via `.GetAwaiter().GetResult()`. Tier is always `Reported` for `HumanReport` source |
| `Ii.Spine` | `IiFacade.SubmitSignalAsync` threads `ClassificationResult.Method/MethodConfidence/ReasoningSummary` into `Belief` annotation fields |
| `Km.Store` | `AppendBeliefAsync` changed `INSERT` → `INSERT OR REPLACE` to support the supersession path (second append of same belief Id with `SupersededBy` set) — pre-existing latent bug, never triggered before B2 |
| `fixtures/signals.json` | Signal 11 added: Cloudwave HumanReport/CSM free-text, `observed_at 2026-06-03T11:00:00Z`, payload `{"body": "..."}` |
| `fixtures/llm-cache.json` | Created empty `{}`; populated by seed-prep with real OpenAI responses |
| `tools/Kozmo.SeedPrep` | Console tool — records LLM responses for all free-text signals into `fixtures/llm-cache.json` using `OpenAiLlmClient + CachingLlmClient(record)` |
| `Kozmo.Api/Program.cs` | `BuildFacade` wires `CachingLlmClient(replay)` when `fixtures/llm-cache.json` exists and has content; before seed-prep LLM is null and free-text signals are silently skipped |
| `Ii.Tests` | Class L tests (L1–L5): free-text belief, cache-miss throw, determinism, band/stance unchanged, fingerprint pin |

**Cloudwave fingerprint re-pin:**
- F3 pin (`e5d0e9b9...`) is **unchanged** — `TestHarness.FreshEngineWithSeed()` has no LLM by default and Signal 11 is not in `BuildCloudwaveSignals()`.
- **L5 new pin**: `84111e62215a04b982da3d955b0a6daa5bf4160e46c52df283754c86ec0e0691` — Cloudwave with LLM belief (Signal 11 supersedes Signal 3's Exp/adoption_rate with value 0.30 / Reported tier).

**Why different pins:** Signal 11's LLM-classified belief (`adoption_rate = 0.30, Reported confidence 0.50`) supersedes Signal 3's rule-classified belief (`adoption_rate = 0.40, Verified confidence 0.95`). Both `Value` and `Confidence` change → fingerprint hash changes. Band/stance remain AtRisk/Renegotiate (composite 0.45 ≥ AtRiskMin; ConfidenceFloor = 0.50 prevents Critical regardless).

**Scope:** F2/F3/F4 golden pins unchanged. Lane 5a verified (Ii.Observation uses `Kozmo.Llm`, not `Kozmo.Llm.OpenAi`). Demo runtime cannot reference `Kozmo.Llm.OpenAi` (BannedSymbols + NetArchTest). Cache miss throws `LlmCacheMissException` — never falls through to network.

---

## B2-verify — Frozen read committed, supersession audited, annotation fields confirmed excluded

### Check 1 — Committed cache; tests replay from cache (not fake)

**Defect retired:** Class L tests (B2) used `FakeLlmClient` with hardcoded `{"dimension":"Experiential","criterion":"adoption_rate","value":0.30,...}`. The fake pinned phantom data — the demo and tests could diverge silently.

**Fix:** Ran `OPENAI_API_KEY=... dotnet run --project tools/Kozmo.SeedPrep`. Real GPT-4o-mini classified Signal 11 (Cloudwave CSM free-text) as **`Operational/uptime_sla = 0.50`** — not `Experiential/adoption_rate = 0.30` as the fake assumed. The SLA breach mention drove the model to an Operational signal.

Cache committed to `fixtures/llm-cache.json`. L tests rewritten to use `CachingLlmClient(replay)`.

**L5 fingerprint re-pin (old → new):**

| | Value |
|---|---|
| Old (fake, `adoption_rate=0.30`) | `84111e62215a04b982da3d955b0a6daa5bf4160e46c52df283754c86ec0e0691` |
| **New (real, `uptime_sla=0.50`)** | **`c0dd1bf6470c3139284850cb37e871800b100a478aa1c90fcb96a4d4059695d1`** |

The pin changes because Signal 11 now supersedes Cloudwave's `Operational/uptime_sla` belief (value and confidence both change). Band/stance remain **AtRisk / Renegotiate**. F2/F3/F4 golden pins unchanged (no LLM in default harness).

### Check 2 — Supersession preserves history; signal re-delivery is idempotent

New Class S tests (`StoreSupersessionTests.cs`):

| Test | Before fix | After fix |
|---|---|---|
| S1 — two signals, same slot; predecessor in history (count=2) | **GREEN** | GREEN |
| S2 — same signal re-delivered; no duplicate | **RED** (`UNIQUE constraint failed: signals.id`) | **GREEN** |
| S3 — end-to-end facade supersession preserves history | GREEN | GREEN |

**S2 fix:** `AppendSignalAsync` changed `INSERT INTO signals` → `INSERT OR IGNORE INTO signals`. Store-layer only — duplicate signal Id is silently ignored, no cascade to beliefs. The facade still creates a new belief version on re-delivery (pipeline-level idempotency is a phase 2 concern).

### Check 3 — Annotation fields excluded from fingerprint

New Class M tests (`FingerprintAnnotationTests.cs`):

| Test | Result |
|---|---|
| M1 — identical `(Dimension, Criterion, Value, Confidence)`, different `ClassificationMethod/ClassificationConfidence/ReasoningSummary` → identical fingerprint | **GREEN** |
| M2 — different `Value` → different fingerprint (control) | **GREEN** |

`BeliefSnapshot(Dimension, Criterion, Value, Confidence)` is the fingerprint input — annotation fields are excluded by construction. No code change needed.

---

## B3 — Cascade generalization + light entity resolution

**What B3 added:**

| Layer | Change |
|---|---|
| `ObservationModule.Classify` | Removed `"body"` key guard on LLM path. Any signal that fails all rules now falls to LLM when configured. Text extraction: `"body"` key → use body text; else → `JsonSerializer.Serialize(payload)`. |
| `EntityRegistry.Resolve` | Added body-text substring scan (step 2) after existing exact alias match (step 1). Scans `payload["body"]` text for any alias keyword (case-insensitive). First match wins; falls back to `signalEntityId` if no alias found. |
| `entity_resolution.saas.v1.json` | Added `"Corvus": "Corvus Infrastructure Ltd."` and `"Meridian": "Meridian IT Services Ltd."` to alias_map. |
| `fixtures/signals.json` | Signal 12 added: Corvus HumanReport/CSM free-text, `observed_at 2026-06-05T09:00:00Z`. Signal 13 added: Meridian HumanReport/CSM free-text, `observed_at 2026-06-07T10:00:00Z`, body mentions "Meridian" alias. |
| `fixtures/llm-cache.json` | Two new cache entries (signals 12 and 13) recorded via real OpenAI API. |
| `tools/Kozmo.SeedPrep/Program.cs` | Updated to run ALL signals through classify (not just body-filtered). LLM is invoked for any that miss rules; recorded count = LLM hits only. |
| `Ii.Tests` | New test classes: R (cascade routing), E (entity resolution body scan), G (golden stream B3), P (fingerprint pins). |

**LLM reads — audit summary:**

| Signal | Body intent | LLM result | Reasoning |
|---|---|---|---|
| S11 Cloudwave | SLA breach, low adoption | `Operational/uptime_sla = 0.50`, confidence=0.7 | "SLA breach indicates potential operational issues" |
| S12 Corvus | SLA failures, reliability concerns | `Operational/uptime_sla = 0.20`, confidence=0.9 | "Consistently high response times, unresolved critical incidents" |
| S13 Meridian | 99.9% uptime, positive QBR | `Operational/uptime_sla = 1.00`, confidence=0.9 | "99.9% uptime with zero incidents — excellent operational performance" |

All three reads are sensible and serve the demo narrative.

**Golden outcomes WITH LLM signals:**

| Vendor | Band | Stance | Why |
|---|---|---|---|
| Cloudwave + S11 | AtRisk | Renegotiate | S11 Reported supersedes S1 Verified uptime_sla. Composite still AtRisk. |
| Corvus + S12 | **AtRisk** | **Renegotiate** | S12 (Reported, uptime_sla=0.20) supersedes S5 (Verified). Confidence_floor drops to ≤0.50 — below Critical gate (0.60). Reported evidence is structurally incapable of locking in Critical (DECISIONS §5). |
| Meridian + S13 | Healthy | Maintain | S13 Reported uptime_sla=1.00 supersedes S2 Verified. Composite stays Healthy; no Critical gate to fail. |

**Corvus Critical/Escalate is preserved for the structured-only path** (F2 pin `7e7cf005...`). The B3 demo story is: a CSM note confirming Corvus problems is valuable context, but Reported tier evidence correctly prevents locking in Critical — that requires Verified data.

**New fingerprint pins:**

| Vendor + signal set | Pin |
|---|---|
| Corvus + S12 (LLM, Reported) | `e948620fea8621cf33a712f31d1db5368fae3559d9b5306b2970d6f07cfd5e60` |
| Meridian + S13 (LLM, Reported) | `cdb887733b510d5f1c38373793031d553c72098af2281d41c7a086724d8dd680` |

L5 (Cloudwave, `c0dd1bf6...`), F2/F3/F4 (structured-only) pins all unchanged.

**Determinism of body-text entity resolution:** Alias scan order follows `AliasMap.Keys` iteration. JSON deserialization of the alias map preserves insertion order (System.Text.Json). Multiple aliases in a single body → first match in insertion order wins. This is deterministic given the fixed config file.

---

## B3-fix — Evidence-fusion fault: corroborating Reported evidence demoted Corvus from Critical

### The fault

Signal 12 (Reported tier, `uptime_sla=0.20`) superseded Signal 5 (Verified tier, `uptime_sla=0.25`) in the same `Operational/uptime_sla` slot. `GetCurrentBeliefsAsync` then returned only S12 for that slot. `RubricModule.ScoreDimension` computes `confidence = Max(belief.Confidence)` — with only S12 in the slot, that is S12's Reported confidence (≈0.40, tier_weight 0.50 × freshness 0.79). `IndexModule.ComputeConfidenceFloor` = `Min(all dimension confidences)` ≈ 0.40. The Critical gate requires `confidence_floor ≥ 0.60`; 0.40 < 0.60 → band demoted to AtRisk.

**Effect:** Adding MORE negative evidence (a Reported confirmation of bad performance) made the system LESS concerned about Corvus. This is the flip-guard violation.

### Principled fix

**Confidence anchor in `IiFacade.AnchorConfidences`:** After applying decay to current beliefs, for each current belief that superseded a predecessor in the same `(Dimension, Criterion)` slot, compute the predecessor's current decayed confidence. If it exceeds the current belief's confidence, floor the current belief's effective confidence at the predecessor's level. The stored `Belief` record is unchanged; this affects in-memory scoring and fingerprinting only.

**Principle restored:** "Corroborating evidence must never lower a dimension's effective confidence below the strongest still-valid predecessor in the same slot."

**Scope:** The anchor fires only when a belief was produced by supersession. In the structured-only path no cross-dimension supersession occurs → F-class pins unaffected. The anchor also handles predecessors that have decayed to near zero naturally (stale predecessors contribute nothing). No change to supersession semantics, decay, history, or trajectory.

**Why not fix the gate or re-tier S12:** The 0.60 gate is structural (Decision §5) and the right signal to suppress weak single-reporter Critical escalations. S12's Reported tier is correct. The fault was in discarding the predecessor's confidence contribution upon supersession, not in the gate or the tier assignment.

### S13 re-audit (signal quality)

Original S13 body contained "99.9% uptime" which caused the LLM to classify the QBR note as `Operational/uptime_sla = 1.00`. A QBR is a relationship/renewal signal; uptime was a supporting detail, not the primary signal. Rewording confirmed: the LLM now classifies S13 as `Strategic/renewal_intent = 1.00` ("executive sponsor confirmed strong intent to renew and expand the license footprint"). This is the correct dimension. The reword removed the specific uptime percentage and foregrounded the renewal and roadmap statements.

S13 now adds a `Strategic` belief to Meridian (no supersession — Meridian had no prior Strategic belief). This is additive, not a slot replacement.

### Fingerprint re-pins

All three LLM-augmented stream pins legitimately changed because the confidence anchor changes the `Confidence` field in the `BeliefSnapshot` fingerprint input for superseded slots.

| Stream | Old pin | New pin | Reason |
|---|---|---|---|
| Cloudwave + S11 (L5) | `c0dd1bf6...` | `e0440aa93b02986144ba143d23373263550f345152b5854ece9fcb5bd5573efd` | S11 Reported supersedes S1 Verified; anchor raises S11 effective confidence to S1's level |
| Corvus + S12 (P1) | `e948620f...` | `9c33aaee76ce8a3df3798797d5d6194cf51765a28064f18560a38f4792b20385` | S12 Reported supersedes S5 Verified; anchor raises S12 effective confidence; Critical/Escalate restored |
| Meridian + S13 (P2) | `cdb887733...` | `5b67c2ae7691c88cb88f7c641af974a95385d753790e8ba7b76d936314976c4a` | S13 body reworded → new LLM result → different belief (Strategic/renewal_intent instead of Operational/uptime_sla); no anchor (new slot, no predecessor) |

**F2/F3/F4 (structured-only) pins unchanged** — confirmed by CI.

### Restored golden outcomes (WITH LLM)

| Vendor | Band | Stance |
|---|---|---|
| Cloudwave + S11 | AtRisk | Renegotiate |
| Corvus + S12 | **Critical** | **Escalate** |
| Meridian + S13 | Healthy | Maintain |

---

## B4 — MetaCognition (deterministic path)

**Branch:** `devb/llm-metacog`

### What was built

Deterministic meta-cognition pass in `IiFacade`. Three concerns:

1. **Contradiction detection** — for each current belief, if a direct predecessor exists in the same `(Dimension, Criterion)` slot and `|newValue − priorValue| ≥ 0.30`, a `Contradiction` is emitted with severity (Low/Medium/High). Contradictions flow to `PostureModule.Assign(meta)` → populated in `PostureAssignment.Cautions`, lowers `Confidence` by `0.10 × contradictionCount` (capped at 0.95).

2. **Gap detection** — any of the four dimensions with no current belief emits a `Gap`. Gaps flow to `PostureAssignment.EvidenceGaps`. They do not affect confidence.

3. **Anchor trail surface** — when `AnchorConfidences` raises a belief's effective confidence, the `MetaCognitionResult.EpistemicSummary` records it in plain language ("confidence anchored 0.400→0.620 from prior Verified belief"). No silent confidence boosts in the drill-down.

The contradiction threshold (0.30) is a constant in `IiFacade.ComputeMeta` — below this delta the change is considered a corroborating update, not a conflict.

### Architecture

`ComputeMeta` is a private method on `IiFacade` (in `Ii.Spine`). It takes the already-computed `decayed` and `anchored` belief lists plus `allHistory` for predecessor lookup. It is called in `RecomputeIndexAsync` (before `_posture.Assign`) and in `GetReasoningTrailAsync` (to populate `ReasoningTrail.Meta`).

No separate module was created — the logic earns no assembly boundary since it reads beliefs already in scope during index recomputation.

`ReasoningTrail` gained a nullable `MetaCognitionResult? Meta` init-only property (non-breaking — existing positional constructors still compile). Populated by `GetReasoningTrailAsync`.

### Non-goals

- No LLM meta path (Path B) — reserved.
- No change to band, stance, or fingerprint from meta. Meta is annotation-only; `PostureModule` only adjusts `Confidence` and surfaces `Cautions`/`EvidenceGaps`.
- No new config file — contradiction threshold as a named constant pending future catalogue extension.

### Current seed meta-state

| Vendor | Contradictions | Gaps | Anchor fires? |
|---|---|---|---|
| Cloudwave (structured only) | 0 | 0 | no |
| Cloudwave + S11 | 0 (Δ uptime=0.05 < 0.30) | 0 | yes — Operational/uptime_sla |
| Corvus (structured only) | 0 | 0 | no |
| Corvus + S12 | 0 (Δ uptime=0.05 < 0.30) | 0 | yes — Operational/uptime_sla |
| Meridian + S13 | 0 | 1 (Experiential — no adoption/satisfaction data) | no |

### Golden outcomes — unchanged after B4

All fingerprint pins (F2, F3, F4, L5, P1, P2), bands, and stances are unchanged. MetaCognitionResult is annotation-only and is not included in `BeliefSnapshot` fingerprint inputs. Confirmed by 108 tests passing and all 7 CI invariant lanes green.

### Tests (Class T — B4)

| ID | Description |
|---|---|
| T1 | Contradiction fires (Δ=0.90 ≥ 0.30) → `Cautions.Count > 0` |
| T2 | Gap fires (only Operational signal) → `EvidenceGaps.Count == 3` |
| T3 | Control — all 4 dims covered, no big deltas → both lists empty |
| T4 | Anchor fires on Corvus+S12 → `ReasoningTrail.Meta.EpistemicSummary` contains "anchor" |
| T5 | Determinism — identical inputs → identical `Cautions` and `EvidenceGaps` |

---

## B4a — Calibrated confidence + anchor provenance

**Branch:** `devb/llm-metacog`

### PART 1 — Gaps lower posture confidence (config-driven)

Extended the penalty formula in `PostureModule.Assign`:

```
confidence = Clamp(ConfidenceFloor − (perContradiction × contradictions) − (perGap × gaps), 0, 0.95)
```

Both rates live in `catalogue/profiles/saas/bands.saas.v1.json` under `confidence_floor`:

```json
"per_contradiction_penalty": 0.10,
"per_gap_penalty": 0.05
```

They are loaded into `BandsConfig.PerContradictionPenalty` / `BandsConfig.PerGapPenalty` by `Catalogue.cs`. `PostureModule.Assign` reads them from `profile.Bands` — no magic numbers in code.

**Rationale for 0.05 < 0.10:** A gap is *missing* evidence; a contradiction is *conflicting* evidence. Missing evidence reduces certainty less than active conflict. The default rate asymmetry encodes this business rule; both rates are configurable without code change.

**Downstream-only:** Both penalties apply to `PostureAssignment.Confidence` only. They are computed after banding and never fed back into `EntityIndex.ConfidenceFloor`, the Critical gate, or the fingerprint. Band and stance are always determined from the raw index.

### PART 2 — Anchor provenance in the reasoning trail

Added three annotation fields to `Belief` (in `Kozmo.Contracts/Generated/Belief.cs`):
- `AnchorRawConfidence` — the effective confidence before anchoring (the Reported-tier value)
- `AnchorPredecessorId` — ID of the predecessor belief that provided the confidence floor
- `AnchorPredecessorTier` — SourceTier of that predecessor

When `AnchorConfidences` fires, these fields are set on the returned in-memory `Belief` copy alongside the raised `Confidence`. The stored belief is unchanged.

`GetReasoningTrailAsync` now returns `anchored` beliefs (instead of raw stored beliefs) as `ReasoningTrail.CurrentBeliefs`. This means the drill-down view shows current effective confidence with anchor provenance. The `DtoMapper.ToTrail` maps all three fields to new nullable properties on `BeliefViewDto` (`AnchorRawConfidence`, `AnchorPredecessorId`, `AnchorPredecessorTier`).

These are annotation fields — they do **not** appear in `BeliefSnapshot` (the fingerprint input) and were confirmed not to affect any fingerprint by the unchanged golden pins.

### Meridian confidence: before vs after B4a

Meridian post-B3 seed state (DemoClock.Fixed = 2026-06-15):
- Operational/uptime_sla (Verified, age≈30d): confidence ≈ 0.714
- Financial/payment_timeliness (Verified, age≈26d): confidence ≈ 0.735
- Strategic/renewal_intent (Reported/LLM, age≈8d): confidence ≈ 0.415
- ConfidenceFloor ≈ 0.415 (Reported-tier Strategic belief, the weakest)
- Experiential dimension: **no evidence** → 1 gap

| State | `PostureAssignment.Confidence` |
|---|---|
| Before B4a (gaps ignored) | ≈ 0.415 (= ConfidenceFloor) |
| After B4a (gap penalty 0.05) | ≈ 0.365 (= ConfidenceFloor − 0.05) |

Band (Healthy) and stance (Maintain) are unchanged — the penalty is annotation-side only.

### Non-goals

- No contradiction seeded (separate decision required).
- No band/stance change from meta penalties.
- No fingerprint change — confirmed: all 6 golden pins (F2, F3, F4, L5, P1, P2) unchanged.

### Tests (Class T — B4a additions)

| ID | Description |
|---|---|
| T6 | 1 gap → `Confidence < ConfidenceFloor` by exactly `PerGapPenalty` |
| T7 | 0 gaps + 0 contradictions → `Confidence == ConfidenceFloor` (control) |
| T8 | Gap + contradiction → penalty is additive (combined sum formula) |
| T9 | Heavy penalty → `Confidence` clamped into `[0, 0.95]` |
| T10 | Corvus+S12 anchor: `AnchorRawConfidence < Confidence`, predecessor ID + Verified tier exposed in trail |

---

## B5 — Demo UI (Razor shell + vanilla JS, served from Kozmo.Api)

**Branch:** `devb/ui`

### Stack decision

The UI is served from the **same `Kozmo.Api` host** — no separate Node process, no npm build step, no CORS. The host was already `Microsoft.NET.Sdk.Web`; adding Razor Pages + static files required only two service registrations (`AddRazorPages()`, `UseStaticFiles()`, `MapRazorPages()`) and a `Pages/` + `wwwroot/` directory.

**All assets are local.** No CDN, no external fonts, no remote chart library. The trajectory chart is hand-rolled SVG inside `wwwroot/app.js`. CI Lane 6 (added in this phase) enforces the offline constraint by scanning all `wwwroot/` and `Pages/` files for `http://` or `https://` references pointing outside `localhost`.

### Pure-client rule

The UI is a **pure client**: it fetches from the HTTP endpoints and renders what the API returns. Specifically:

- Composite scores, band, stance, confidence, fingerprint are all displayed as received — no rounding, massaging, or recomputation.
- If a field is needed that the API does not expose, the correct fix is an API change, not a JS workaround.
- No scoring logic, fusion logic, or fingerprint computation exists in JavaScript.

### What was built

| Step | Deliverable |
|---|---|
| 1 — List + drill-down + fingerprint + reset | Vendor list (band-colour-coded, sidebar); drill-down trail from posture → band → index → dimension scores → beliefs → raw signal; anchor provenance shown inline; meta Cautions/EvidenceGaps surfaced; Reset button POSTs `/demo/reset`, re-renders, highlights fingerprint match. |
| 2 — Trajectory | Hand-rolled SVG line chart from `/vendors/{id}/trajectory`; band colour regions (Healthy/AtRisk/Critical); data points coloured by band; thresholds from `trail.band.thresholds` (API-sourced, not hardcoded). |
| 3 — Replay | Replay button POSTs `/demo/replay`, subscribes to `GET /events` (EventSource), updates vendor list on each `replay-step` event, refreshes trail on `replay-complete`. |

### CI Lane 6 (no-CDN invariant)

`InvariantTests.UI_assets_reference_no_external_urls` — tagged `[Trait("Category","Invariant")]` so it runs under `ci/check-invariants.sh` without script change.

The test walks up from `AppContext.BaseDirectory` to locate `host/dotnet/Kozmo.Api/wwwroot/` and `host/dotnet/Kozmo.Api/Pages/`, reads all `.html`, `.js`, `.css`, `.cshtml` files, and asserts no `https?://` pattern pointing outside `localhost`. Fails non-empty: it asserts the directories are populated (catches misconfigured builds).

### Non-goals

- No authentication or multi-user support (single-presenter demo).
- No server-side rendering of API data — the Razor page is a static HTML shell only.
- No fingerprint, scoring, or decision logic in JavaScript.
- No new API endpoints or contract changes.
