# Kozmo — Momentum-Centric Product Architecture

## The directive this captures
Everything orients around the **Momentum Workspace**. Impact vectors (configurable), context-aware
questions, processing, and check-ins all live there. Home and Portfolio are not separate paradigms —
they are **momentum at higher altitudes**. This document turns that directive into a concrete,
sequenced, buildable plan, grounded in the existing design docs, and honest about what exists today vs.
what each piece requires.

---

## 1. The core model — momentum at three altitudes

The whole product is **one idea (momentum) viewed at three zoom levels**. This is not invented here —
it's exactly what the docs describe (the three index levels: Dimension → Vector Super-Index →
Portfolio/Area Index; the portfolio rollups; programs as cohort-level runs of a vector).

```
┌─────────────────────────────────────────────────────────────────────────┐
│ HOME              "What needs my attention across everything, right now?" │
│                   Momentum at the highest altitude — triage surface.      │
│                   Not a dashboard of vanity metrics; a momentum triage.   │
├─────────────────────────────────────────────────────────────────────────┤
│ PORTFOLIO         "How is momentum moving across a cohort / program /      │
│                   area?" Momentum aggregated across many parties.          │
│                   Rollups: all parties with Renewal Optimization blocked,  │
│                   all suppliers declining, all contracts compliance-       │
│                   critical. A Program is a Portfolio filtered to one       │
│                   Impact Vector across a cohort.                            │
├─────────────────────────────────────────────────────────────────────────┤
│ MOMENTUM          "What is the state of THIS commercial party, and what    │
│  WORKSPACE        should be done?" One party. The operating surface.       │
│                   ┌─────────────────────────────────────────────────────┐ │
│                   │ Impact Vectors (configurable)                        │ │
│                   │   → Dimensions (per-vector, weighted)                │ │
│                   │       → reviewed via context-aware questions +       │ │
│                   │         evidence (processing)                        │ │
│                   │       → Dimension Index                              │ │
│                   │   → Super Index (weighted roll-up)                   │ │
│                   │   → Momentum (the trend of the Super Index)          │ │
│                   │   → Matters (triggered) → Interventions → Outcomes   │ │
│                   │   → Check-ins (fill gaps, move the score live)       │ │
│                   └─────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────┘
```

**The unifying principle:** at every altitude, the organizing quantity is **momentum** — direction of
commercial movement, not a static level. Home triages by momentum; Portfolio aggregates momentum;
the Workspace generates momentum from evidence + human input. This is the through-line the directive
asks for, and it is coherent because the underlying engine already computes indexes and (with a
stored checkpoint series) their trend.

---

## 2. What each surface is

### 2.1 Momentum Workspace (the operating center — mostly built for one vector)
One commercial party. Shows its active Impact Vectors, each decomposed into weighted dimensions, each
dimension reviewed (context-aware questions + evidence) and indexed, rolling up to a Super Index whose
trend is Momentum; weak/urgent dimensions raise Matters; check-ins fill gaps and move the score live.
**This is where configurable vectors, context-aware questions, processing, and check-ins all live** —
exactly per the directive.

### 2.2 Portfolio (momentum across many — not built)
The cohort/program/area view. Answers "where is momentum moving badly across my book?" Renders the
**Portfolio/Area Index** (the third index level) and rollup queries: parties by band, by momentum
direction, by blocked vector. A **Program** is this view scoped to one Impact Vector across a defined
cohort of parties (Renewal Management Program = Renewal Optimization across N vendors). Entry point to
drill into any party's Workspace.

### 2.3 Home (momentum triage — not built)
The highest altitude. Answers "what needs me now?" A momentum-ordered triage across everything:
declining Super Indexes, urgent Matters, gaps blocking assessment, check-ins awaiting answer. Not a
metrics dashboard — a **prioritized momentum feed** that routes the user into the right Portfolio or
Workspace. Framed by the website's "seven-step loop, continuously, without waiting to be asked."

---

## 3. The five capabilities the directive names — built vs. required

Honest status of each thing the directive says "should be for Momentum Workspace":

| Capability | Status today | What full delivery requires |
|---|---|---|
| **Configurable Impact Vectors** | ONE hardcoded (Vendor Readiness, 4 dims, flat 0.25 weights). No selection seam. | **E2.4** — the type/vector-selection seam (de-hardcode `saas`), + per-vector dimension+weight config. Each vector's doctrine authored. |
| **Context-aware questions** | Static authored bank (SaasQuestionBank, 24 Qs), depth-capped at L1, NOT context-aware. | New capability: select/adapt questions by vendor type + situation. Depends on E2.4 (classifier) + situation logic. Beyond current E2 scope. |
| **Processing** (ingest→classify→extract→belief→index→posture) | Mostly real. BUT classification hardcoded (E2.4), and **gap-discovery is currently broken** (completeness cassette can't match live data — see §5). | Fix the cassette-in-live-path bug (§5); E2.4 for real classification. |
| **Check-ins** | Real answer→belief→recompute→stance loop PROVEN. BUT only 2 of 24 questions score, and no new check-ins can be *raised* right now (cassette bug). | Cassette fix (unblock raising); E2.2c is effectively empty (no clean bindings exist — proven); more scoreable questions need authoring or E2.3 (YesNo scoring). |
| **Home / Portfolio focused on momentum** | Neither built as momentum surfaces. | New UI on top of: a working per-party Super Index + Momentum (have), stored checkpoint history (have, after the versioning fix), and Portfolio/Area rollup queries (not built). |

**The honest headline:** the Workspace for ONE vector is real and demoable. Everything else the
directive names sits on **unbuilt foundations** — principally E2.4 (configurable vectors + classifier),
a fix to the broken gap-discovery path, new context-aware-question logic, and new Portfolio/Home UI.

---

## 4. The dependency chain (why order matters)

You cannot build the momentum Home/Portfolio meaningfully before the pieces beneath them work. The
honest dependency order:

```
[FIX] Completeness cassette / gap-discovery        ← currently broken system-wide (§5). Blocks
      (make raising check-ins work for live data)     the whole "processing + check-ins" story.
        │
        ▼
[FIX] Identity resolution (duplicate parties)      ← split data across duplicate rows; corrupts
                                                      any portfolio rollup (a party counted 3×).
        │
        ▼
[E2.3] Scoring semantics + usage_trend + (decide:   ← makes more of the bank scoreable; 12/24
       make YesNo answers scoreable?)                 questions are currently un-scoreable.
        │
        ▼
[E2.4] Vector/type-selection seam + classifier      ← THE unlock for CONFIGURABLE IMPACT VECTORS
                                                      and real (not hardcoded) classification.
        │
        ├────────────► Context-aware questions       ← select questions by type+situation (needs
        │                                              classifier from E2.4).
        │
        ▼
[NEW] Portfolio (momentum rollups across parties)   ← needs working per-party momentum + identity
        │                                              resolution + (ideally) >1 vector.
        ▼
[NEW] Home (momentum triage across everything)      ← sits on Portfolio + Workspace.
```

**Reading this plainly:** the momentum-centric product the directive describes is the *destination*.
The road to it runs through the cassette fix, identity resolution, E2.3, and E2.4 — most of which is
backend. Home and Portfolio (the parts of the directive about "even Home and Portfolio") are near the
*end* of the chain, because they aggregate things that must first work per-party and across a clean,
de-duplicated set of parties.

---

## 5. The critical current bug (blocks the "processing + check-ins" half of the directive)

Diagnosed this session: the **completeness cassette cannot match any live vendor's beliefs**. It was
recorded only against hand-authored fixture belief sets under placeholder GUIDs, and its cache key
includes **volatile free-text derivation strings** — so live beliefs essentially never byte-match. It
is wired into the live `RecomputeVendorAsync` path (`recordMode:false`) but populated with pure test
data. Effect: `CompletenessOrchestrator.RunAsync` throws a cache-miss on the first question for *every*
vendor, silently swallowed — so **no new check-ins can be raised through the live convergence loop, for
any vendor.** Answering an *already-open* check-in still works (belief write + recompute happen before
the completeness step), which is why the demo loop worked — but gap *discovery* is non-functional.

This is the single most important thing to fix for the directive, because "processing" and "check-ins
that fill gaps" both depend on gap discovery working. And note: re-recording the cassette (the obvious
fix) is NOT durable — the volatile cache key means it breaks again as derivation text changes. The real
fix is likely to **decouple the live path from the test cassette, or make the cache key content-stable
(exclude free-text derivation).** Needs its own diagnosis.

---

## 6. Recommended build sequence (turning the directive into steps)

Each step is contained, diagnosis-first, proof-gated — the rhythm that's worked.

1. **Fix gap-discovery (the cassette-in-live-path bug).** Diagnose why a test fixture gates production
   and why the cache key is volatile; decouple or stabilize. Unblocks raising check-ins → makes
   "processing + check-ins" real again. HIGHEST priority for the directive.
2. **Identity resolution.** Merge duplicate parties + fix the ingestion cause. Prerequisite for any
   honest Portfolio rollup (else parties are miscounted) and for clean per-party data.
3. **E2.3.** Wire required/expected/optional into scoring; fix `usage_trend`; decide whether to make
   YesNo answers scoreable (would unlock ~12 currently-inert questions — high value for check-ins).
4. **E2.4 — the configurability unlock.** Build the vector/type-selection seam + classifier. This is
   where **configurable Impact Vectors** and real classification become true. Decide scope here: ship
   the seam proving one real swap (Vendor Readiness + one more authored vector), defer arbitrary-N.
5. **Context-aware questions.** On top of E2.4's classifier: select/adapt the question set by vendor
   type + situation. This is the "context-aware questions" the directive names.
6. **Portfolio.** Build the momentum rollup surface across parties/programs (the Portfolio/Area Index +
   rollup queries), drilling into Workspaces. Needs 1–4 working and clean identity.
7. **Home.** Build the momentum triage surface on top of Portfolio + Workspace — the "what needs me
   now" feed, momentum-ordered.

Steps 1–2 are fixes (unblock reality). 3–4 are E2 (make it configurable). 5 is new logic. 6–7 are the
new momentum surfaces the directive asks Home/Portfolio to become.

---

## 7. What stays true throughout (the honesty spine)
- Never fabricate: not-assessed shows honestly; no fake momentum without a real checkpoint series; no
  question presented as scoring when it doesn't; gaps shown, not hidden. (This is why the demo was
  trustworthy and must remain the discipline as these surfaces are built.)
- Momentum is a derivative — it requires a stored checkpoint series (the versioning fix this session is
  what makes Momentum real rather than a single-point fiction).
- The engine is entity/vector-agnostic; each Impact Vector's *content* (dimensions, catalogue, rubric,
  questions, weights) is authored config, not free — E2.4 provides the seam, authoring fills it.
- Contract-as-entity: the docs (Agents-v2 §B.1; Open Questions §1.2/§7.1) settle contract as a PEER
  entity type whose content is an authored Impact-Vector Review Model, riding E2.4's seam — a peer
  Workspace, consistent with this momentum-centric model.

---

## 8. One-line summary
Kozmo is a **momentum platform**: Home triages momentum, Portfolio aggregates it across cohorts, and
the Momentum Workspace generates it per party from configurable Impact Vectors, context-aware
questions, evidence processing, and check-ins. Today one vector's Workspace is real; the full vision
runs through the gap-discovery fix, identity resolution, E2.3, and E2.4 (the configurability unlock)
before Portfolio and Home can honestly become the momentum surfaces this directive describes.
