# PHASE 0 — Foundation (Plan A on Production Structure)

**Subsystem in build:** `subsystems/interpretation-inference/` (thin) + `subsystems/knowledge-memory/` (thin)
**Cognition:** deterministic, rule-based, four-dimension model — **no LLM, no network, no live integrations**
**Structure:** the real `kozmo/` platform monorepo — *not* a demo-only scaffold
**Demo target:** 3 vendors · 10 frozen signals · glass-box drill-down · reproducible fingerprint

---

## What this phase is

Phase 0 lays the **full foundational project structure** and freezes every contract, so the
deterministic cognition core and the layers on either side of it (intake, UI) can be built in
parallel. It produces the skeleton, the contracts, the catalogue, the seed, and the one thing
the whole demo rests on — a **reproducible fingerprint** — but no finished pipeline behaviour.
That is Phase 1.

This supersedes the earlier `PHASE_0.md`/`SPECIFICATION_CORRECTIONS.md` drafts, which were
written against the LLM Q&A-graph design. That design is **not** used. There is no `FakeLlm`,
no Inference-by-LLM, no MetaCognition in this build — those are reserved slots for later.

### The two halves, decided

| | Source |
|---|---|
| **Cognition** — deterministic rule-based classification + 4-dimension scoring → band → stance, with confidence floor and fingerprint | the demo prep brief (Plan A) |
| **Structure** — `kozmo/` monorepo, four core libraries, nine subsystem folders, schema codegen, CI invariants, fakes for unbuilt subsystems | the platform architecture |

The nine catalogue configs already produced (`dimensions / scoring_rubric / dimension_weights
/ bands / postures / source_tiers / classification / decay / entity_resolution` — all
`*.saas.v1.json`) **are** Plan A's cognition. They tie out to the three demo vendors and slot
into the catalogue unchanged. The platform scaffold (`kozmo-platform-scaffold.zip`) is the
structure. Phase 0 marries the two.

---

## Foundational structure (production-real)

Every folder below exists after Phase 0. **Implemented** = real code this build. **Reserved** =
folder + README/contract placeholder only, so the structure is whole and the next subsystem
inherits the template.

```
kozmo/
├── schema/                          # IMPLEMENTED — single source of truth → C# (Python emit reserved)
│   ├── signal.schema
│   ├── belief.schema                # KnowledgeTuple
│   ├── dimension_score.schema
│   ├── entity_index.schema
│   └── posture.schema
│
├── libs/
│   ├── kozmo.platform/              # IMPLEMENTED (thin) — entity context, tracing, fingerprint util
│   ├── kozmo.bus/                   # RESERVED — contract only; FakeBus used in demo
│   ├── kozmo.llm/                   # RESERVED — abstraction present, never called in demo
│   └── kozmo.identity/              # RESERVED — contract only; FakeCustomerContext used
│
├── catalogue/
│   └── profiles/saas/               # IMPLEMENTED — the nine *.saas.v1.json configs live here
│
├── subsystems/
│   ├── interpretation-inference/    # ← BUILD (thin): Observation, Rubric, Index, Posture, Decay
│   ├── knowledge-memory/            # ← BUILD (thin): KnowledgeTuple store, EntityIndex, History
│   ├── integration-fabric/          # RESERVED — faked (FakeBus)
│   ├── identity-governance/         # RESERVED — faked (FakeCustomerContext)
│   ├── programme-orchestration/     # RESERVED
│   ├── workflow-coordination/       # RESERVED
│   ├── commercial-mastery/          # RESERVED
│   ├── economics/                   # RESERVED
│   └── operations/                  # RESERVED
│
├── entities/vendor/demo-customer-001/   # IMPLEMENTED — 3 vendor records
├── fixtures/                            # IMPLEMENTED — signals, golden expectations, edge cases
└── ui/kozmo-ui/                         # IMPLEMENTED (Phase 1) — vendor file + reasoning drill-down
```

I&I internal layout (the template every later subsystem copies):

```
subsystems/interpretation-inference/
├── dotnet/
│   ├── Ii.Contracts/      # GENERATED records + module interfaces + the I&I façade (Contract 2)
│   ├── Ii.Observation/    # rule-based classification + entity/alias resolution + belief formation
│   ├── Ii.Rubric/         # per-dimension scoring over belief sets (deterministic)
│   ├── Ii.Index/          # composite + confidence floor + worst-dim floor + fingerprint + banding
│   ├── Ii.Posture/        # band + pattern + renewal → stance (deterministic)
│   ├── Ii.Decay/          # freshness half-life (deterministic; real, thin)
│   ├── Ii.Spine/          # façade impl, pipeline orchestration, read API, SQLite, reset
│   ├── Ii.Fakes/          # FakeBus, FakeCustomerContext  (NO FakeLlm — no LLM)
│   └── Ii.Tests/          # xUnit — unit + scenario + golden
└── (python/ reserved — not created this build; no LLM module exists yet)
```

> **Inference and MetaCognition are deliberately absent.** In production an LLM will occupy the
> Observation classification slot and emit the *same typed belief* the rule-based classifier
> emits today — so the deterministic spine downstream is unchanged when it arrives. That is the
> architecturally precise answer to "where's the intelligence?": the slot exists; the demo fills
> it with inspectable rules on purpose.

---

## Scope

**In (this milestone):** belief store (KnowledgeTuple, SQLite, append-and-supersede, versioned,
history); multi-dimensional EntityIndex (operational/experiential/financial/strategic) with
confidence floor, deterministic fingerprint, incremental recompute; deterministic per-dimension
scoring → banding → stance (MAINTAIN / MONITOR / RENEGOTIATE / ESCALATE / REMEDIATE); rule-based
classification + entity/alias resolution; seed (3 vendors + 10 signals + harness); vendor-file
UI + reasoning drill-down (both views) + fingerprint display; one-command reset + golden path.

**Out (deliberate):** no agents/autonomy; no validity gate; no contradiction/caution logic; no
message bus or scheduling (harness drives the façade directly; FakeBus is present as the seam,
not exercised); **no LLM; no live integrations; no network; no actions in the world;** no
economics/learning. All later increments.

---

## Phase 0 deliverables

### 1. Repo skeleton
The full tree above — every subsystem folder, the four libs, `schema/`, `catalogue/`,
`fixtures/`, `ui/`. Reserved folders carry a one-line README stating status. The structure is
complete on day one; only the implemented set has code.

### 2. Schema + codegen (single source of truth)
`schema/` defines the types once; codegen emits C# records into `Ii.Contracts/`. Python emission
is wired but unused this build. Types and their enums:

| Record | Key fields | Enums |
|---|---|---|
| `Signal` | id, entity_id, customer_id, source_system, external_id, payload, observed_at, received_at, trace_id | `SourceSystem` |
| `Belief` (KnowledgeTuple) | id, entity_id, dimension, criterion, value, source_tier, confidence, freshness, derivation, source_signals, version, superseded_by, created_at, trace_id | `Dimension`, `SourceTier` |
| `DimensionScore` | entity_id, dimension, score [0–1], confidence, contributing_belief_ids | `Dimension` |
| `EntityIndex` | entity_id, dimension_scores, composite, confidence_floor, band, fingerprint, version, computed_at | `Band` |
| `PostureAssignment` | id, entity_id, band, stance, rationale, evidence_trail, confidence, fingerprint, index_version, assigned_at, valid_until | `Band`, `Stance` |

Enums: `Dimension` (Operational/Experiential/Financial/Strategic) · `SourceTier`
(Verified 0.95 / Inferred 0.70 / Reported 0.50 / Unverified 0.30) · `Band`
(Healthy/AtRisk/Critical) · `Stance` (Maintain/Monitor/Renegotiate/Escalate/Remediate).

**Belief is append-and-supersede, never edited** — a correction is a new version pointing back
via `superseded_by`. This is structural, not convention.

### 3. Catalogue — the nine configs, placed and validated
Copy the nine `*.saas.v1.json` into `catalogue/profiles/saas/`. Phase 0 adds a **config
validator** that loads all nine and asserts they tie out: single-source-of-truth boundaries
(weights live only in `dimension_weights`), every classification criterion exists in
`scoring_rubric`, every tier in `classification` exists in `source_tiers`, and the invariant
guarantees hold (REPORTED 0.50 is permanently below the 0.60 CRITICAL-floor gate). Bad config is
**rejected, not normalised**.

### 4. Contracts to freeze
This is the parallelism budget. Frozen and merged before fan-out; changing one afterward needs
a joint sign-off.

- **Contract 1 — data types** (`schema/` → `Ii.Contracts/`): the five records + enums above.
- **Contract 2 — the I&I façade** (what intake and UI build against):
  `SubmitSignal(signal) → traceId` · `GetPosture(entityId)` · `GetIndex(entityId)` (dimension
  scores, composite, confidence floor, **fingerprint**) · `GetBeliefs(entityId)` ·
  `GetReasoningTrail(entityId)` (the Posture ← Band ← Index ← Beliefs ← Signals chain) ·
  `Reset()`.
- **Module interfaces:** `IObservationModule` (Classify + ResolveEntity), `IRubricModule`
  (ScoreDimension), `IIndexModule` (Aggregate + RecomputeDirty + Fingerprint), `IPostureModule`
  (Assign), `IDecayEngine` (Freshness).
- **Platform/K&M seams:** `IEntityStore` (real, thin — append-supersede beliefs, save/read
  index, history), `ICatalogue` (loads the nine configs), `IKozmoBus` (faked), `ICustomerContext`
  (faked). `IKozmoLlm` exists in `kozmo.llm` but is referenced by nothing.

### 5. Stores and fakes
- **Real, thin (Knowledge & Memory):** `EntityStore` over SQLite — beliefs (append-supersede +
  history), `EntityIndex` persistence, reasoning log. Behind the same repository interface that
  fronts Azure SQL in production.
- **Real:** `Catalogue` (file load of the nine configs — no need to fake a JSON read).
- **Faked:** `FakeBus`, `FakeCustomerContext`. The harness calls the façade directly; FakeBus is
  present so the production seam exists, not because the demo needs async.
- **No `FakeLlm`** — there is no LLM in any path.

### 6. Fixtures / seed (the frozen reality)
- **3 vendors** with contract baselines and renewal dates: Cloudwave Systems ($54k/mo,
  HEALTHY→AT_RISK), Meridian IT Services ($48.5k/mo, HEALTHY control), Corvus Infrastructure
  ($70k/mo, HEALTHY→CRITICAL).
- **10 signals** over ~30 days, exact source/id/timestamp/content per the brief, with full
  payloads for the email (#6) and the Corvus invoice (#9), numbers internally consistent so the
  drill-down math checks out.
- **The deliberate alias:** signal #6 says "Cloudwave"; the record is "Cloudwave Systems Inc." —
  resolution must surface visibly.
- **Golden expectations:** final stances (Cloudwave AT_RISK/RENEGOTIATE, Corvus
  CRITICAL/ESCALATE, Meridian HEALTHY/MAINTAIN), expected per-dimension scores (Cloudwave
  op 0.45 · exp 0.40 · fin 0.55 · strat 0.50), and the **expected fingerprint per vendor** so any
  drift is caught instantly.

### 7. Determinism spike — the load-bearing risk (do this first)
Prove, before anything is built on top: the multi-dimensional index + **deterministic
fingerprint** + incremental recompute. `fingerprint = hash(sorted beliefs ⊕ dimension scores ⊕
weights ⊕ config_version)`. Re-run the same evidence → byte-identical fingerprint. Recompute
only the dimension a new signal touched; copy the rest. **If the fingerprint isn't reproducible,
the entire glass-box story collapses** — so it is gated before fan-out.

### 8. CI invariants (the template for all nine subsystems)
1. **Pipeline direction** — modules talk only through `Ii.Contracts`; no cross-module internal imports.
2. **Belief immutability** — append-and-supersede only; no in-place edits (structural via the store API).
3. **Determinism** — no `DateTime.UtcNow` and no randomness in Observation-classify / Rubric / Index / Posture / Decay; time is injected by `Ii.Spine`. CI blocks clock/RNG references in those projects. The fingerprint spike is the proof.
4. **Confidence discipline** — `confidence = tier × freshness ≤ tier_weight`; the worst-dimension CRITICAL floor fires only when confidence ≥ 0.60; REPORTED (0.50) is structurally below the gate, so a single human-reported signal can never force CRITICAL.
5. **No live dependency** — CI fails on any `anthropic`/HTTP-client/network import in the demo runtime path. (Replaces the LLM-only-in-X invariant; here the rule is *no LLM at all*.)

### 9. Tracer bullet
One signal → classify → belief (append-supersede) → score one dimension → index + fingerprint →
band → stance, end-to-end through Contract 2 against the thin stores, for one vendor. Returns a
real stance. Its only job: prove the seams connect before any module is fully built.

---

## Ownership in Phase 0

Phase 0 is **R1-led** (the brief's three-person plan; collapses cleanly to two if R2 absorbs
intake into UI). Two joint steps, then everyone builds against frozen contracts.

| | Phase 0 work |
|---|---|
| **Joint** | Freeze Contract 1 + Contract 2; agree the tracer-bullet shape |
| **R1 — cognition core** | Schema + codegen; the determinism/fingerprint spike; stub façade so R2/R3 start immediately; config validator; thin `EntityStore` |
| **R2 — intake + data** | The nine configs into catalogue; classification + alias rules against frozen types; the 3-vendor / 10-signal seed + harness |
| **R3 — UI + reasoning** | Inventory the concept UI (the schedule unknown — assess day one); stub the read calls; lay out the drill-down panel against Contract 2 |

R2 and R3 own the front door and the face; the core is R1's alone. They consume it through the
contract only — no reaching into internals.

---

## Build order within Phase 0

```
0.1  Joint: freeze Contract 1 (types) + Contract 2 (façade); merge to main
0.2  R1: schema → codegen → Ii.Contracts; stand up STUB façade
0.3  R1: determinism + fingerprint spike — prove reproducible re-run + incremental recompute
0.4  R2: nine configs into catalogue/profiles/saas/ + config validator green
0.5  R2: seed 3 vendors + 10 signals + harness (types only, against the stub)
0.6  R1: thin EntityStore (SQLite, append-supersede, history) + Catalogue loader
0.7  Joint: tracer bullet green — one signal → stance, end-to-end, through the contracts
0.8  R1: stand up the five CI invariant lanes as hard blocks
```

---

## Fan-out gate — Phase 0 is done when:

- [ ] Full repo structure exists; reserved subsystems/libs carry status READMEs.
- [ ] Schema codegen produces valid C# records; build green.
- [ ] Contract 1 and Contract 2 are merged and frozen.
- [ ] Nine catalogue configs load and pass the validator (tie-outs + invariant guarantees).
- [ ] Thin `EntityStore` works (append-supersede + history) and `Catalogue` loads configs.
- [ ] **Determinism spike proven:** same evidence → identical fingerprint; incremental recompute correct.
- [ ] Tracer bullet passes: one signal → stance, end-to-end, through Contract 2, < 5s, no network.
- [ ] Five CI invariant lanes active and blocking.

Only then do the modules fan out into Phase 1.

---

## Phase 1 preview (the brief's Days 2–5 — do not start in Phase 0)

R1 builds the real core (store, per-dimension scoring, banding, stance, dirty propagation,
fingerprint). R2 finishes intake + harness and verifies all 10 signals classify and map cleanly,
including the alias. R3 wires the vendor-file UI to the read API and builds the drill-down
(business view + technical view + fingerprint display). Integrate end-to-end for one vendor on
**Day 3, not Day 5**. Then the full golden path, the deterministic re-run, one-command reset,
rehearsal, and the backup recording.

**Safe cut if the week tightens:** drop to two dimensions and six signals — never cut the
reasoning panel, the determinism re-run, or the rehearsal. The explainability and a clean run are
what the townhall is for.
