# Kozmo Vendor File — Phase 0 Frozen Spec

**Status:** FROZEN for the demo build. Coding agents implement against this; do not change it mid-build.
**Goal:** feed a vendor's evidence (contract PDF + CSV + signals) through the existing signal→posture spine once, and render a layered `Vendor.md`. Single-pass, ingestion-time. No loops, no agents, no autonomous orchestrator.
**Reuse, do not rebuild:** `Observation` (signals), `Rubric`, `Index` (band + fingerprint), `Posture`, `Decay`, `MetaCognition` core, `SqliteEntityStore` tables, `Catalogue`.

---

## 0. How to use this spec

This is the contracts-first freeze. Two halves build in parallel against §1 (the belief type): the **edge** (intake lanes, §6) and the **core wrapper** (write path + control plane, §7–8). They meet only at the frozen belief record. Nothing starts until §1–§5 are locked.

Each phase has a gate (§11). A module is "done" when its fixture (§9) produces the intended output and its test is green.

---

## 1. The belief record (FROZEN)

Lives in `Kozmo.Contracts`. Extends the existing `Belief` with `claim_key`, tier, provenance, and dating.

```
Belief {
  belief_id      : guid
  vendor_id      : guid
  claim_key      : string        // MUST be in the catalogue (§4)
  value          : typed         // value_type per claim_key (§4)
  source_tier    : enum          // PRIMARY|VERIFIED|REPORTED|INFERRED|UNVERIFIED (§2)
  confidence     : float [0..1]  // <= tier ceiling (§2). HARD INVARIANT.
  provenance     : { evidence_id: guid, locator: string }   // §6 locator forms
  observed_at    : timestamp     // timestamp of the EVIDENCE, not of ingestion
  half_life_days : int | null    // from catalogue; null => decays only by supersession
  valid_until    : timestamp|null
  version        : int
  status         : enum          // active | superseded
  superseded_by  : guid | null
}
```

```
Evidence {                       // the document/file itself — NOT a belief
  evidence_id : guid
  vendor_id   : guid
  doc_type    : enum             // §3
  source_tier : enum             // derived from doc_type (§3)
  ref         : string           // filename / blob ref
  doc_version : int
  ingested_at : timestamp
}
```

**The document is evidence.** Its metadata (filename, type, date) lives on the `Evidence` row. The *claims inside it* become `Belief` rows that cite the evidence via `provenance`. One document → one Evidence row → many Beliefs.

---

## 2. Source tiers (FROZEN — five-tier ladder)

Replaces the running 4-tier config. Set in `catalogue/profiles/saas/source_tiers.saas.v1.json`.

| Tier | Ceiling | Meaning |
|---|---|---|
| PRIMARY | 1.0 | Of-record, executed (signed contract, amendment) |
| VERIFIED | 0.8 | Strong system fact (system export, invoice, PO) |
| REPORTED | 0.5 | One voice (email, owner note, feedback, quote) |
| INFERRED | 0.3 | Model-derived analytical claim |
| UNVERIFIED | 0.2 | Weak (web profile, unsourced) |

**Gate invariant:** `critical_min_confidence = 0.60`. INFERRED (0.3) and UNVERIFIED (0.2) sit **below** the gate by construction — a claim at those tiers can inform a read but can never force a Critical band on its own. This is the property the whole design protects. Enforce `confidence = min(extractor_confidence, ceiling)` **in the write path, not the caller.**

---

## 3. Document-type → tier map (FROZEN)

| doc_type | tier |
|---|---|
| signed_contract / executed_agreement | PRIMARY |
| amendment / addendum | PRIMARY |
| purchase_order | VERIFIED |
| invoice | VERIFIED |
| usage_csv / spend_csv / system_export | VERIFIED |
| quote / proposal | REPORTED |
| email / communication | REPORTED |
| owner_note / feedback | REPORTED |
| web_profile / news | UNVERIFIED |
| model_derived | INFERRED |

---

## 4. Claim_key catalogue (FROZEN)

The slot list. A belief's `claim_key` MUST be here. Two classes:

- **Scored** — feeds a dimension via the rubric; produces a (rubric value 0–1, confidence) pair.
- **Structural** — does NOT score a dimension; drives flags, deadlines, completeness. Carries a typed value + confidence.

| claim_key | class | value_type | dimension | typical tier | half_life_days |
|---|---|---|---|---|---|
| sla_uptime | scored | percent | Operational | VERIFIED | 30 |
| support_responsiveness | scored | metric | Operational | VERIFIED | 30 |
| csat | scored | rating | Experiential | REPORTED/VERIFIED | 60 |
| usage_trend | scored | percent | Financial | VERIFIED | 30 |
| invoice_accuracy | scored | percent | Financial | VERIFIED | 90 |
| roadmap_alignment | scored | score | Strategic | REPORTED | 90 |
| renewal_intent | scored | enum | Strategic | REPORTED | 60 |
| annual_value | structural | money | Financial(ctx) | PRIMARY | null |
| renewal_date | structural | date | — (flag) | PRIMARY | null |
| notice_period | structural | duration | — (deadline) | PRIMARY | null |
| auto_renewal | structural | bool | — (flag) | PRIMARY | null |
| payment_terms | structural | enum | Financial(ctx) | PRIMARY | null |
| liability_cap | structural | money | — (risk) | PRIMARY | null |
| contract_on_file | structural | bool | — (completeness) | PRIMARY | null |

`half_life_days = null` ⇒ contractual fact, true until superseded (no time decay). Observed facts decay on the clock.

---

## 5. Dimensions (FROZEN — 4, as built)

The spine scores 4 dimensions, weight 0.25 each. (The 6-dimension model in the vendor-file doc — adding Relationship and Compliance — is the **target, deferred**; relationship signals fold into Strategic, compliance facts ride as structural risk flags this week.)

| Dimension | Fed by (scored claim_keys) |
|---|---|
| Operational | sla_uptime, support_responsiveness |
| Experiential | csat |
| Financial | usage_trend, invoice_accuracy |
| Strategic | roadmap_alignment, renewal_intent |

---

## 6. Intake lanes (FROZEN behaviour, §Phase 2)

| Lane | Input | Emits | Provenance locator |
|---|---|---|---|
| Rules | CSV / semi-structured docs | VERIFIED/PRIMARY beliefs | `row:N` / `cell:R,C` / `field:name` |
| PDF (isolated) | 1–2 real contract PDFs | PRIMARY beliefs | `page:P §clause` + quoted span text |
| Signals (reuse) | feedback / email text | REPORTED beliefs | `message_ref` |

PDF lane: **recorded in rehearsal**, replays deterministically; recording is the live-failure fallback. `extractor_confidence` may be < ceiling if the read is uncertain; the cap still applies.

---

## 7. Deterministic rules (FROZEN)

```
confidence        = min(extractor_confidence, tier_ceiling)      // write-path enforced
rubric_value(k,x) = threshold table for claim_key k applied to raw x   // existing rubric
dim_score(D)      = Σ(value_i · conf_i) / Σ(conf_i)   over scored beliefs in D
dim_conf(D)       = max_i(conf_i)
composite         = Σ_D 0.25 · dim_score(D)
ConfidenceFloor   = min_D dim_conf(D)

composite_band    : ≥0.65 Healthy | 0.40–<0.65 AtRisk | <0.40 Critical
worst_dim_band    : a dimension is Critical if dim_score(D) < 0.40 AND dim_conf(D) ≥ 0.60
band              = more severe of (composite_band, worst_dim_band)
GATE              : a Critical band requires ConfidenceFloor ≥ 0.60; else clamp to AtRisk
posture_conf      = base − 0.10·#contradictions − 0.05·#gaps

completeness      = |filled expected claim_keys| / |expected claim_keys|   (per vendor class, §8)
gaps              = expected − filled

freshness(b)      = 0.5 ^ (age / half_life_days)     if half_life_days != null   (age in days)
                  = 1.0                               if half_life_days == null  (contractual)
effective_conf(b) = confidence · freshness(b)
```

**Supersession** (per `(vendor, claim_key)` only): writing a belief where an active one exists → if new tier ≥ old tier, OR same tier with later `observed_at`, mark old `status=superseded, superseded_by=new`, bump `version`. Other claim_keys untouched. Walk the **full chain** when anchoring (R-1 fix).

**Contradiction:** two active beliefs, same `(vendor, claim_key)`, conflicting value from different sources where the lower tier does not cleanly supersede → raise contradiction; higher tier is current; flag + penalty. (Plus existing scored rule: `|Δvalue| ≥ 0.30` vs direct predecessor.)

---

## 8. Expected-belief sets (FROZEN — completeness targets)

Completeness measures filled vs expected. One class for the demo (`saas_vendor`):

**Expected:** sla_uptime, csat, usage_trend, invoice_accuracy, roadmap_alignment, renewal_intent, contract_on_file, renewal_date, annual_value. *(9 slots.)*

A vendor with 7 of 9 filled → completeness 0.78. Missing slots become gaps with importance from their dimension weight.

---

## 9. Fixture set (FROZEN — 5–6 vendors, each proves one thing)

Inputs are fixed; exact composite/confidence are **golden-pinned after first run**, band/completeness/intent are the contract.

| # | Vendor | Evidence in | Intended read | Proves |
|---|---|---|---|---|
| 1 | Cloudwave | contract PDF (PRIMARY) + usage/spend CSV (VERIFIED) + feedback/email (REPORTED) | high Operational/Financial, low Strategic → **Critical via worst-dim floor**; completeness ~0.83; renewal-deadline flag | full multi-lens read, real PDF span, the flip, worst-dim floor, glass-box |
| 2 | Helix | web profile only (UNVERIFIED) | all dims thin; ConfidenceFloor < 0.60 → **cannot assert Critical**, stays AtRisk/UNVERIFIED; completeness ~0.22; no flags | confidently-incomplete honesty + the gate |
| 3 | Northwind | signed contract only (PRIMARY) | high confidence on contract terms, operational/experiential **gaps**; completeness low–mid | completeness ≠ confidence |
| 4 | Vertex | contract PRIMARY (payment_terms Net 45) + email REPORTED (Net 30) | **contradiction raised**, PRIMARY wins (Net 45), confidence_floor penalty, posture RECONFIRM | contradiction + tier precedence |
| 5 | Aster | usage CSV with **old observed_at** | freshness drags effective_conf; **refresh flag** fired; drill-down shows age vs half_life | decay computed from a timestamp (not hardcoded) |
| 6 | Borealis | quote REPORTED (annual_value) → later contract PRIMARY (annual_value) | quote in belief_history, contract value active; **single-step** supersession | version-control / supersession (R-1-safe) |

Walking skeleton = Vendor 1 end to end first. Then 2,3,5. Then the two engineered edge cases 4,6.

---

## 10. Out of scope this week (state in the room)

Action ledger, outcome ledger, wisdom/learning loop, active inquiry / Q&A elicitation, live decay-over-time (clock), query-time INFERRED extraction, runtime AI agents / agent platform, autonomous program orchestrator (only the deterministic stage-runner is built), real Brain / alias / Registry (seeded). The file *renders* these sections from fixtures or leaves them empty, clearly marked.

---

## 11. Build phases & gates

| Phase | Subsystem · modules | Gate |
|---|---|---|
| 0 Spec | Contracts + Catalogue: belief type, 5-tier config, claim_key catalogue, expected-set, fixtures | types in both bindings, configs load, INFERRED < 0.60 |
| 1 Substrate | K&M write path (EXTEND) + Ii.Spine R-1 fix | tier-capped belief writes & reads back; R-1 test green; 2-step supersession holds band |
| 2 Intake (∥1) | I&I edge: rules extractor (BUILD), PDF lane (BUILD), signals (REUSE) | each lane emits beliefs for one vendor; PDF recorded & replays |
| 3 Reconcile+judge | MetaCognition contradiction (EXTEND); spine REUSED | rich vendor → dims, band, posture, contradiction, stable fingerprint |
| 4 Control plane | K&M completeness/decay-fields/management-block (BUILD); host renderer + stage-runner (BUILD) | one vendor renders a full Vendor.md in the Razor UI |
| 5 Breadth+demo | host: 6 fixtures, glass-box drill-downs | spread holds; supersession & contradiction visible; PDF belief drills to a real span |

**Critical path:** 0 → 1 → 4 → 5. Phase 2 runs alongside 1; Phase 3 is mostly wiring. The two risks — R-1 (Phase 1) and the PDF lane (Phase 2) — are isolated on purpose.

**Module tally:** 6 new (rules extractor, PDF lane, completeness, management-block assembler, renderer, stage-runner) · 3 extended (K&M write path, MetaCognition contradiction, Ii.Spine anchor) · rest reused untouched.
