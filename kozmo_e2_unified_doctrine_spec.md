# Kozmo · E2 — Vendor Typing & the Unified Doctrine

**Specification — v1 (Draft for review)**

*How completeness, check-ins, and scoring become one structure, so a real vendor can earn a real posture*

Kozmo Commercial Intelligence Platform
Status: Specification draft · Audience: Engineering · Scope: KYV pipeline, doctrine configs, Index/Rubric, check-in engine

---

## What This Document Is

This is the build specification for phase **E2**, expanded to its true scope as revealed by the design documents (*Commercial Reality Concept*, *Cognition/Memory/NBA*, the *Subsystems* reference §22, the *I&I Build Plan*): not merely "question banks per category," but the **unification of the three currently disconnected lists** — the completeness expected-set, the scoring-rubric criteria, and the check-in question source — into the single per-vendor-type doctrine structure the architecture always intended, plus the **vendor-type classifier** that selects which doctrine applies.

The governing insight, from the Subsystems reference: the rubric's inputs *are* Q&A-graph questions, questions are keyed by `entity_type`, and completeness is coverage against the same graph. One structure, three consumers. The current build drifted into three hand-maintained lists; this spec repairs the drift.

> **The one-line goal:** a real ingested vendor — with only its honestly extracted beliefs plus human-answered check-ins — computes a real, auditable Index and shows a posture and stance. No seed data, no identity-resolution dependency, no rubric dilution by stealth.

---

# Part I — The Problem, Stated Precisely

## 1.1 The three lists today

| List | Lives in | Keys it knows | Consumer |
| --- | --- | --- | --- |
| Completeness expected-set | `expected_belief_sets` (completeness config) | `payment_terms`, `invoice_amount`, `renewal_date`, … (financial/document keys) | `/vendors/{id}/vendor-file` completeness %, gap chips, check-in generation |
| Rubric criteria | `scoring_rubric.saas.v1.json` | `uptime_sla`, `mttr`, `support_response_time`, `incident_volume` (operational keys) | `RubricModule` → `IndexModule.Aggregate` → posture/stance |
| Extraction catalogue | `claim_key_catalogue.saas.v1.json` | all 16 claim keys (both families) | `DocumentBeliefExtractor` / email extractors |

The extraction catalogue is the only place both families coexist — but it carries no requirement semantics, and neither downstream consumer reads it as authority. Consequence: a real vendor's beliefs satisfy List 1, are invisible to List 2, and `recompute_index (0 items)` short-circuits to null. **Completeness rises; the score never exists.** Answering today's check-ins can take a vendor to 100% complete and still 0% scoreable.

## 1.2 What the design documents actually prescribe

- **Subsystems §22.1** — rubric inputs are Q&A-graph nodes; rubrics are versioned Catalogue *configuration*, not code.
- **Subsystems `Question`** — `entity_type` is a first-class field on every question: the bank is per-type by construction.
- **I&I Build Plan 7e** — `compute_rating` performs *"confidence-weighted sum, with neutral imputation for missing inputs"* and *"errors if required inputs missing"*: the design distinguishes **required** from **optional** criteria; partial coverage can score.
- **Commercial Reality §4** — *"grounded enough to act is coverage across the vectors a specific decision needs, not global completeness."* Per-dimension coverage, per-type relevance.
- **Cognition/NBA Part III** — determinism via `index_version` + `derivation_hash`; doctrine fixed at run time; **Wisdom Loop changes doctrine offline, between runs, never during them.**

## 1.3 Design decision: evolve, don't proliferate

Three candidate shapes were considered:

| Option | Shape | Verdict |
| --- | --- | --- |
| A — New master "doctrine" file superseding all four configs | One mega-file per type | Rejected: large migration, breaks every existing loader and test at once |
| B — Fifth cross-reference file linking the existing four | Additive index file | Rejected: adds a fourth list to keep in sync — the disease, not the cure |
| **C — Promote `claim_key_catalogue.<type>.vN.json` to single source of truth; derive the other consumers from it** | Existing file gains fields; `expected_belief_sets` becomes generated, not authored | **Adopted.** Smallest diff, honors anti-proliferation, the catalogue already contains every key |

> **Boxed principle — one authority, derived views.** After E2, `claim_key_catalogue.<type>.vN.json` is the sole authored definition of what a vendor of that type should be known by. Completeness sets, check-in questions, and rubric input lists are *derived* from it. Hand-editing a derived list is a build error, enforced by a boot-time coherence validator.

---

# Part II — The Unified Doctrine Schema

## 2.1 Catalogue entry — extended shape

Each entry in `claim_key_catalogue.<type>.vN.json` gains four fields (★ new):

```jsonc
{
  "claim_key": "payment_terms",
  "value_type": "duration_days",
  "dimension": "Financial",                    // ★ authoritative dimension binding
  "requirement": "required",                   // ★ required | expected | optional
  "rubric_criterion": "payment_terms_risk",    // ★ nullable → completeness-only key
  "checkin_question": {                        // ★ nullable → not askable (extract-only)
    "text": "What payment terms (net days) are currently agreed with this vendor?",
    "response_shape": "TYPED_VALUE",
    "answer_tier": "Confirmed"                 // tier granted to a human answer (0.65)
  },
  "prompt_fragment": "…existing literal fragment…",
  "guards": ["…existing…"],
  "sources": ["document"]                      // document | email | checkin
}
```

Field semantics:

- **`dimension`** — the single authoritative binding. `dimensions.<type>.vN.json` is retained as the dimension *registry* (list, display order, per-type weights) but its criterion membership lists become **validated against** the catalogue, not independently authored. A mismatch fails boot.
- **`requirement`** —
  - `required`: the dimension cannot be **assessed** without a live belief for this key. Missing ⇒ dimension "not assessed"; gap chip rendered; check-in raised with priority HIGH.
  - `expected`: contributes to coverage % and check-in generation; missing ⇒ **neutral imputation** in the rubric (per Build Plan 7e), with a confidence penalty (see §3.3).
  - `optional`: tracked if present, never asked, never imputed, never blocks.
- **`rubric_criterion`** — nullable. Non-null ⇒ `scoring_rubric.<type>.vN.json` **must** contain weights/bands for it (validator-enforced). Null ⇒ the key exists for record/completeness only and can never move a score — an explicit, reviewable statement rather than an accidental gap.
- **`checkin_question`** — nullable. Present ⇒ the check-in engine may raise it when the key is a gap in a dimension that matters for the type. Absent ⇒ the key is fillable only by extraction (e.g., keys where a human answer would be unreliable).

## 2.2 The vendor-type taxonomy

New file: `vendor_types.v1.json`

```jsonc
{
  "version": "v1",
  "default_type": "saas",
  "types": [
    {
      "id": "saas",
      "display": "SaaS / Software",
      "doctrine": "claim_key_catalogue.saas.v2.json",
      "dimension_weights": { "Operational": 0.35, "Financial": 0.25, "Compliance": 0.20, "Relationship": 0.20 }
    },
    {
      "id": "financial_institution",
      "display": "Bank / Financial Institution",
      "doctrine": "claim_key_catalogue.finserv.v1.json",
      "dimension_weights": { "Compliance": 0.40, "Financial": 0.30, "Relationship": 0.20, "Operational": 0.10 }
    }
  ]
}
```

Two types at v1 — `saas` (evolving the existing family) and one genuinely different type to prove the mechanism. First National Bank of Maryland is already in the vendor list as the natural second-type subject. `default_type` guarantees the pipeline never stalls on an unclassifiable vendor: unclassified ⇒ `saas` doctrine + a `vendor_type` gap + an IDENTITY-style confirmation check-in.

## 2.3 Versioning & determinism obligations

- Doctrine files are **versioned in the filename** (existing convention). A doctrine change is a version bump — never an in-place edit of an active version. The Index fingerprint's config-hash input now includes `(vendor_types.version, doctrine file version, scoring_rubric version)`; a doctrine bump therefore bumps `index_version` — a deliberate, visible event, exactly like the existing golden-test re-pin discipline.
- The classifier prompt is versioned in the prompt registry like every extraction prompt; classification runs through `CachingLlmClient`, so the run is cassette-recordable and replayable.
- **No runtime doctrine mutation, ever.** New types, requirement changes, weight tuning: offline, reviewed, next-run pickup (Wisdom-Loop cadence, per Cognition/NBA Part VIII).

---

# Part III — Behavioral Specifications

## 3.1 Vendor-type classification (the soft edge)

**When:** once per vendor per ingestion run, after identity resolution/clustering, before belief persistence (a new pipeline stage: `VendorTypeClassificationStage`, mirroring `DocumentPersistenceStage`'s placement pattern).

**Input:** a compact evidence summary assembled deterministically from the run — document filenames, extracted document types (MSA/invoice/W9/certificate), and the vendor's extracted metadata clauses. **Not** raw full-text (bounded token cost; stable input surface).

**Output (typed, constrained):**
```jsonc
{ "vendor_type": "saas | financial_institution | unknown", "confidence": 0.0-1.0, "rationale": "…" }
```

**Persistence:** the classification is itself a belief — `claim_key: "vendor_type"`, tier **Inferred (0.3)** when LLM-derived, superseded to **Confirmed (0.65)** when a human answers the confirmation check-in. This keeps "everything is beliefs" intact and gives classification full provenance/audit for free.

**Selection rule (deterministic):** doctrine = type of the current, live `vendor_type` belief if `confidence ≥ 0.5`; else `default_type`, plus a raised confirmation check-in. Same recorded inputs + same prompt version ⇒ same classification (cassette-backed).

**Re-classification:** only on ingestion runs or a human check-in answer — never on a timer, never mid-run.

## 3.2 Completeness — per-dimension coverage

Replaces the flat expected-set computation. For a vendor of type *T* with doctrine *D*:

```
for each dimension d in D:
  required(d)  = keys in D where dimension=d, requirement=required
  expected(d)  = keys in D where dimension=d, requirement in {required, expected}
  covered(k)   = a live belief exists for k (not superseded, not decayed past valid_until,
                 tier ≥ key's minimum tier if declared)
  assessable(d)      = all of required(d) covered
  coverage(d)        = |covered ∩ expected(d)| / |expected(d)|
overall completeness = Σ_d weight_T(d) × coverage(d)     // weights from vendor_types.v1.json
```

**API surface:** `/vendors/{id}/vendor-file` gains a `dimensions[]` block — `{name, assessable, coverage, missingRequired[], missingExpected[]}` — alongside the existing flat fields (kept for one release for UI compatibility). The UI's gap chips become dimension-grouped; "not assessed" on a dimension now names *which required keys* are absent, turning the honest wall into an honest to-do list.

## 3.3 Rubric & Index — required/optional semantics

`RubricModule` changes (all still deterministic, no LLM, no clock):

1. **Dimension scoring precondition:** `assessable(d)` (all required criteria covered). Not assessable ⇒ dimension emits no score — unchanged honest silence, but now by *declared* rule rather than accidental emptiness.
2. **Neutral imputation:** an assessable dimension with missing `expected` criteria imputes the neutral band value (0.5) for each missing one at **zero confidence contribution** — the score computes, and `confidence_floor` is dragged down by the imputation (each imputed criterion contributes confidence 0). This implements Build Plan 7e's "neutral imputation," and it means thin-but-real coverage produces a *low-confidence* score instead of no score — which the posture layer already knows how to treat (low floor ⇒ RECONFIRM-style caution rather than confident action).
3. **Index aggregation:** unchanged short-circuit rule, but restated on the new predicate — Index is null only when **no dimension is assessable**. One assessable dimension is enough for a partial, honestly-labeled Index (`dimensions` block shows unassessed dimensions explicitly).
4. **Fingerprint:** now hashes over `(contributing belief versions, doctrine version, rubric version, vendor_types version)`.

## 3.4 Financial dimension — the v1 scoring decision (product checkpoint)

To make the walking skeleton land on IIVS's *real* data, the `saas.v2` doctrine proposes:

| Key | Requirement | Rubric criterion | Bands (proposal) |
| --- | --- | --- | --- |
| `payment_terms` | required (Financial) | `payment_terms_risk` | ≤30d → 0.9 · 31–60d → 0.7 · 61–90d → 0.4 · >90d → 0.2 |
| `invoice_amount` | expected (Financial) | *(null — completeness-only)* | — (magnet-for-wrong-figures history; record, don't score) |
| `renewal_date` | expected (Financial) | *(null v1; candidate for renewal-proximity criterion v2)* | — |
| `annual_value` | optional | *(null)* | — (known extraction hazard; never imputed, never asked) |

> **Explicit product decision required before build:** is a Financial dimension scored from `payment_terms` alone (plus imputed neutrals) meaningful enough to display? The spec's position: yes, *because* the confidence floor will be visibly low and the UI labels unassessed dimensions — this is "honest, thin, real" rather than "fabricated full." The alternative (demand ≥2 required criteria) is a one-line doctrine edit, not a code change — which is precisely the point of the unification.

Operational criteria are unchanged (`uptime_sla`, `mttr`, `support_response_time`, `incident_volume` — all `expected`, none `required` v1, since their evidence is email-sourced and dormant until identity resolution; the dimension simply remains "not assessed" for document-only vendors, honestly).

## 3.5 Check-in generation — doctrine-driven

The check-in engine's gap source changes from the flat expected-set to: **gaps in `required`/`expected` keys, in dimensions whose type-weight > 0, that carry a `checkin_question`.** Priority: required-gaps first, then expected, ordered by dimension weight. Answer path is unchanged (existing Confirmed-tier loop) — but because the asked keys now carry `rubric_criterion`, **an answered check-in moves the score**. That is the loop-closure this entire spec exists for.

---

# Part IV — Build Plan (walking-skeleton order, proof gates)

| Step | Deliverable | Proof gate (must pass before next step) |
| --- | --- | --- |
| **0 — Diagnosis** | The unification matrix: every claim key × (in expected-set? rubric criterion? check-in question? dimension?) from the *actual current configs* | Matrix reviewed; no key unaccounted for |
| **1 — Contract freeze** | `saas.v2` catalogue schema + `vendor_types.v1.json` schema + boot-time coherence validator (orphan criterion / dimension mismatch / derived-list edit ⇒ fail fast) | Validator green on current configs migrated verbatim; suite green (no behavior change yet) |
| **2 — Unify (saas only)** | Completeness, check-in gen, and Rubric all read the catalogue; `expected_belief_sets` deleted as authored config (generated at boot); per-dimension completeness API block | **IIVS on real data:** Financial dimension `assessable=true`, real score + low confidence floor, Index non-null, posture renders; Operational honestly "not assessed" naming its missing required keys. Golden fingerprints re-pinned as a deliberate versioned event |
| **3 — Classifier** | `VendorTypeClassificationStage` + `vendor_type` belief + confirmation check-in + `finserv.v1` doctrine | First National classifies `financial_institution` (or falls to default + check-in); the two vendors demonstrably run different doctrines in one program run; cassette replay reproduces classification bit-identically |
| **4 — Loop closure (the demo moment)** | Nothing new — orchestrated proof | A real vendor: gap shown → check-in answered by a human → Confirmed belief lands → dimension flips assessable → Index recomputes → **stance appears**, with drill-down to the answer as provenance |
| **5 — Hardening** | ~edge cases: unclassifiable vendor, doctrine version bump mid-history, imputation-only dimension, contradiction between extracted and check-in value (existing supersede semantics apply) | Suite + golden + CI lanes green; `KYV_KNOWN_GAPS.md` updated (identity-resolution entries unchanged) |

Explicitly **out of scope** for E2: identity resolution; email-sourced keys reaching live vendors; `escalation_count`/`incident_volume` (deferred, logged); L2/L3 depth ladder & adaptive probing; quadrants; Q&A/report generation; multi-customer catalogue; SQL Server (Phase F).

---

# Part V — Governing Rules (for ratification)

1. `claim_key_catalogue.<type>.vN.json` is the single authored authority on what a vendor of that type should be known by; completeness sets, check-in questions, and rubric input lists are derived, never hand-edited.
2. Every claim key declares exactly one dimension and one requirement level; every non-null `rubric_criterion` must resolve to weights and bands, enforced at boot.
3. A dimension is assessed only when all its `required` keys hold live beliefs; missing `expected` keys impute neutrally at zero confidence contribution; `optional` keys never impute, never ask, never block.
4. The Index is null only when no dimension is assessable; a partially-assessed Index is legal and must label its unassessed dimensions.
5. Vendor type is a belief (`vendor_type`), Inferred when machine-classified, Confirmed when human-answered; doctrine selection reads the live belief with a declared default fallback.
6. Doctrine, taxonomy, and rubric versions are inputs to the Index fingerprint; any doctrine change is a version bump and a deliberate golden re-pin, never a silent edit.
7. Doctrine changes happen offline, between runs (Wisdom-Loop cadence); nothing reclassifies or re-weights during a run.
8. A check-in may only ask questions declared in the doctrine; every askable key that is scoreable must move the score when answered — the loop must close by construction.
9. Extraction breadth (new claim keys) and scoring reach (new criteria) grow only through the catalogue, preserving multi-pass extraction grouping as a load-bearing invariant.
10. Honest silence outranks fabricated coverage: no imputation may ever make an unassessable dimension appear assessed, and the UI must render "not assessed" with the named missing required keys.

---

*— end of specification v1 —*
