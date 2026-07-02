# KYV — Phase 4 Frozen Spec: Pipeline Wiring (the declared program run)

**Why this phase exists.** Phases 1–3 built and unit-tested each capability in isolation:
resolution (P1), extraction (P2), the check-in loop (P3). They are NOT connected in the
running app. Phase 3 exposed the seam concretely: the identity-merge path is end-to-end-inert
because resolved vendors are never persisted into `registry_vendors` in a live run. Phase 4
wires the capabilities into one declared, deterministic program run so the whole KYV loop
executes end-to-end in the app — ingest → classify → extract → filter → resolve → PERSIST →
raise check-ins → (process responses) — and the previously-inert paths go live.

**This is the phase that turns validated components into a running system.**

**Scope OUT:** the connector (Phase 5 — this phase runs over a local folder), enrichment
(Phase 6), free-text responses, real email, the action/ledger loop, the analyst's
not-yet-built scenarios (05–16).

---

## 0. The central decision: UNIFY, not bridge

There are two vendor stores today:
- old in-memory `EntityRegistry` (Ii.Spine) — seeds the original vendor-file demo (Cloudwave…)
- new `registry_vendors` (SQLite, Identity & Governance) — where KYV resolution (Stage F) writes

**Decision: the KYV pipeline's canonical registry (`registry_vendors`) is the source of truth
for KYV program runs. Do NOT bridge the two stores or sync them.** The old in-memory
EntityRegistry stays ONLY as the legacy vendor-file demo's own path (untouched, so that demo
keeps working); the KYV *program run* reads and writes exclusively the canonical registry.
Rationale: bridging creates two sources of truth that drift (the exact anti-pattern the
platform avoids). A KYV run is self-contained — it discovers, resolves, and persists its own
vendors into the canonical store, and check-ins fire against THOSE. No dependency on the
seeded EntityRegistry.

Consequence: when a KYV run persists resolved CanonicalVendors into `registry_vendors`, the
Phase-3 identity-merge path becomes LIVE automatically — the vendors a check-in references are
now in the table the real IdentityRegistry reads.

---

## 1. Altitude and reuse/new boundary

- **Subsystem:** Operations — owns program execution (the runner).
- **Module:** the Program Runner — EXTEND the existing stage-runner, do NOT build a new one and
  do NOT build an autonomous orchestrator. A program is a DECLARED sequence of stages; the
  runner executes the declaration deterministically. KYV is "the first declared program."
- **Stages invoked (all built in P1–P3):** `extract_candidates` (P2), `filter` (P2),
  `resolve_identity` (P1, Stages A–F incl. Stage F persist), `raise_checkins` (P3),
  `process_response` (P3, async). Classify (P2) and ingest are the front.

**Reuse / new:**
| Concern | REUSE (built) | NEW (this phase) |
|---|---|---|
| Every stage's logic | P1–P3 modules | — |
| Recompute a vendor | RecomputeVendorAsync | — |
| Persist canonical vendors | IdentityRegistry / Stage F | — |
| Belief write, tier clamp | VendorFileWriteService | — |
| Sequencing the stages | (the existing stage-runner) | the KYV program DECLARATION + the run record |
| Program run identity/state | — | a ProgramRun (id, stages executed, status) |

The genuinely new work is small: **a declared stage sequence + a runner that executes it end-
to-end over a folder, persisting resolved vendors so downstream stages (check-ins) fire against
real data.** Most of Phase 4 is WIRING existing stages, not new logic.

## 2. The declared KYV program (the sequence)

A declaration (data, not code) naming the ordered stages and their two gates:
```
program: Know Your Vendor
stages:
  1 ingest           (read folder → evidence)
  2 classify         (type each document)          [P2]
  3 extract_candidates (parties + roles)            [P2]
  4 filter           (deterministic post-filter)    [P2]
  5 resolve_identity (Stages A–F, incl. F persist)  [P1]  ── GATE 1: auto/provisional/triage
  6 raise_checkins   (triage + gaps → check-ins)    [P3]  ── GATE 2: raise questions
  (async) process_response                          [P3]
```
The runner executes 1→6 deterministically over the evidence, then per resolved vendor where
stages are per-vendor. `process_response` runs asynchronously when answers arrive (P3).

## 3. What Phase 4 actually wires (the work)

1. **A `ProgramRun`** — id, program name, source folder, started/finished, status, and the
   ordered stage executions (this is also the source for the activity log, later).
2. **The runner executes the declared sequence** over a local folder end-to-end: each stage's
   real output feeds the next. No stage re-implemented — called in order.
3. **Stage F persists** resolved CanonicalVendors (confirmed/provisional/triage) into
   `registry_vendors` — so the run's vendors are in the canonical store.
4. **raise_checkins fires against the persisted vendors** — the triage cases (ABC
   POSSIBLE_SAME_ENTITY) and gaps (Regulus/Aequitas) become live check-ins referencing
   vendors that now EXIST in registry_vendors.
5. **The identity-merge path goes live** (the Phase-3 inert path): because the vendors are now
   persisted, an IDENTITY_CONFIRM=YES answer resolves a real GetAsync and merges (non-destructive
   Absorbed) — end-to-end, not just in unit tests.

## 4. Test contract (end-to-end, on the real folder Scenarios 01–04)

A single program run over D:\June\Kozmo Workspace produces, live (not in isolated unit tests):
1. **Run completes** through all stages; a ProgramRun record with the stage sequence + status.
2. **Vendors persisted** to registry_vendors: IIVS (one, confirmed), Aequitas, Regulus
   (confirmed), ABC Tech + ABC Technologies (two, triage/POSSIBLE_SAME_ENTITY); customers
   (Revolution Medicines, Meridian, Prudential, Biogen) NOT persisted as vendors.
3. **Check-ins raised live** against persisted vendors: ABC identity, Regulus gap, Aequitas gap.
4. **Identity-merge now works LIVE:** answering the ABC check-in YES against the running app
   (real IdentityRegistry, vendors present in registry_vendors) merges the two into one
   (absorbed marked Absorbed→survivor, not deleted), recomputes. This is the path that was
   inert after Phase 3 — assert it now works end-to-end.
5. **Gap-fill works live** (already did): Regulus answer → belief → recompute.
6. **No-wrong-merge + wrong-match guard** still hold end-to-end.

Tests:
- `ProgramRun_RealFolder_CompletesAllStages`
- `ProgramRun_PersistsCorrectVendorSet_CustomersExcluded`
- `ProgramRun_RaisesLiveCheckins_AgainstPersistedVendors`
- `ProgramRun_AbcIdentityAnswerYes_MergesLive_Absorbed_NotDeleted`  ← the previously-inert path
- `ProgramRun_WrongMatchGuard_HoldsEndToEnd`

## 5. Done when
- One command / entry point runs the declared KYV program over the local folder end-to-end.
- Resolved vendors persist to registry_vendors (canonical = source of truth; old EntityRegistry
  untouched, legacy demo still works).
- Check-ins raise live against persisted vendors; the identity-merge path WORKS end-to-end
  (no longer inert) — the ABC YES merge resolves and is non-destructive.
- Gap-fill and the wrong-match guard hold end-to-end.
- The runner executes a DECLARED sequence (KYV = first declared program), NOT a hardcoded
  monolith and NOT an autonomous planner.
- Existing tests green (exact per-dll count before/after); the legacy vendor-file demo and its
  tests UNCHANGED (unify does not disturb the old path).

## 6. The hard checkpoint
**The previously-inert identity-merge path now works end-to-end, AND the legacy demo is
undisturbed.** Two-sided: (a) prove the ABC YES merge fires live against the real registry with
vendors persisted by the run (the Phase-3 gap closed); (b) prove the old vendor-file demo and
its tests are unchanged (unify onto canonical registry for KYV did not break the seeded path).

## 7. Build order (commits)
1. **Commit 1 — ProgramRun + the declared sequence + the runner executing ingest→…→resolve**
   end-to-end over the folder, persisting vendors to registry_vendors. Stop before check-ins.
   Checkpoint: correct vendor set persisted, customers excluded, legacy demo untouched.
2. **Commit 2 — wire raise_checkins into the run** against persisted vendors; confirm the
   identity-merge path is now LIVE (the ABC YES merge works end-to-end, non-destructive).
   Checkpoint: §6 two-sided.
3. **(defer) activity log** — the ProgramRun record is its source; build after the run works.
