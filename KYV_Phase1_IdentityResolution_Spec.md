# KYV — Phase 1 Frozen Spec: Identity Resolution + Registry

**Scope of this phase:** turn a bag of candidate identity beliefs (from a folder of
documents) into a set of canonical vendors written to the Registry, with each candidate
dispositioned by a confidence gate: auto-confirm, provisional, or triage. Nothing
downstream (substance extraction, judgement, render) is in this phase. The classifier
(Phase 2), check-in loop (Phase 3), pipeline (Phase 4), and connector (Phase 5) are out
of scope and must not be built here.

**Registry starting state:** EMPTY. On this run everything is new — there is nothing to
match against, so resolution is primarily *intra-batch clustering + canonicalization*.
The match-against-Registry path is built but mostly exercised on later runs. The Registry
fills as a side effect of this phase.

**Test bed:** the adversarial folder (see §7). Do NOT test against clean fixtures.

**Architectural rules carried from prior phases (non-negotiable):**
- Reuse existing primitives; do not build a parallel matching engine where reconcile logic
  applies. Identity resolution is the existing reconcile pattern pointed at identity claims.
- LLM only at the soft edge (candidate extraction, entity-typing where ambiguous). All
  clustering, matching, gating, and disposition logic is deterministic.
- Build as composable stages with typed inputs/outputs — not one fused method. This phase
  produces stages the program runner (Phase 4) will later sequence.
- Append-and-supersede semantics; tier caps confidence.

---

## 0. What this is, and the reuse/new boundary (read before building)

**Altitude.** This is NOT a KYV feature. It is a generic platform capability that KYV is the
first program to invoke.
- **Subsystem (owner):** Identity & Governance.
- **Module (the logic):** Identity Resolution — a NEW module in Identity & Governance. It earns
  module status because it owns a distinct responsibility (resolve who an entity is) that no
  existing module owns. It is NOT a new subsystem (does not pass the four-property test) and
  NOT a KYV step.
- **Stage (the invocation surface):** `resolve_identity` — the typed, callable wrapper a program
  uses. KYV calls this stage; so will any future program. No KYV-specific logic may live in the
  stage or module path. Test: a future Compliance-Audit program must be able to call
  `resolve_identity` against the same Registry with zero KYV branches.
- **Record (the data):** the Registry — owned by Knowledge & Memory (the "Brain" tier), WRITTEN
  by this module. The Registry is the platform's canonical entity store, not "KYV's vendor list."

**The module is mostly REUSE with a thin layer of new logic. Build the new; WIRE TO the reused —
do NOT reimplement the reused parts.**

| Concern | REUSE (call existing mechanism — do NOT rebuild) | NEW (identity-specific logic this module adds) |
|---|---|---|
| Conflict / contradiction between competing claims | existing reconcile mechanism | — |
| Confidence clamped to source-tier ceiling | existing tier-clamp | — |
| Confidence-gate mechanism (the auto/provisional/triage dispositioner) | existing gate | the identity-specific *thresholds & rules* fed into it |
| Append-and-supersede semantics | existing store write-path | — |
| Name → comparison key | — | normalization (suffix strip, noise, fuzzy key) |
| Grouping variants into one entity | — | clustering by comparison-key + fuzzy |
| What kind of entity this is | — | entity-typing (company/person/product/internal/non-vendor) |
| Two similar names, different signals | — | collision detection (block merge) |
| Choosing the entity's name | — | canonical-name selection |
| Persistent canonical store | (Knowledge & Memory holds it) | the Registry RECORD SHAPE (§1.2) |

**The hard anti-proliferation check:** the invariant "conflicts resolve by tier" must live in ONE
place. If this module reimplements conflict/tier/gate logic, that invariant now exists in two
places and will drift. Reuse keeps it singular. If the agent writes new clustering-conflict or
tier logic instead of calling the existing mechanism, that is a reuse-not-replace violation —
reject it.

---

## 1. The data shapes (contracts)

### 1.1 CandidateIdentityBelief (input to this phase)
One per (document, candidate) pair. Produced upstream (stubbed for this phase — see §6).
```
candidate_id
raw_name                  # exactly as written in the document, e.g. "Cloud Wave Inc."
source_tier               # PRIMARY (signed contract) | VERIFIED | REPORTED (email mention)
confidence                # ≤ tier ceiling
provenance                # {doc_id, page, span}
signals                   # optional corroborating: {domain?, address?, tax_id?, country?}
role_hint                 # optional: counterparty | reseller | manufacturer | internal | unknown
```

### 1.2 CanonicalVendor (Registry record — the new persistent shape)
```
vendor_id                 # minted GUID
canonical_name            # the chosen display/legal name for the cluster
aliases[]                 # every raw_name that resolved into this vendor, with provenance
comparison_key            # normalized key used for matching
entity_type               # COMPANY | PERSON | PRODUCT | INTERNAL | NON_VENDOR | UNKNOWN
confidence                # cluster confidence
flags[]                   # see §4
rebrand_map_ref           # nullable; empty this run
acquisition_map_ref       # nullable; empty this run
created_at, source_evidence[]
status                    # CONFIRMED | PROVISIONAL | TRIAGE
```
The Registry holds canonical_name + aliases + comparison_key + entity_type + confidence +
empty rebrand/acquisition maps. This is the Knowledge & Memory "Brain" tier becoming real —
NOT a name→GUID dictionary.

### 1.3 ResolutionDisposition (output of this phase — designed to feed Phase 3)
One per candidate cluster. **This shape is the seam to the check-in loop**, so it must carry
everything a check-in needs.
```
cluster_id
member_candidate_ids[]
proposed_canonical_name
comparison_key
entity_type
disposition               # AUTO_CONFIRM | PROVISIONAL | TRIAGE
confidence
flags[]
triage_reason             # nullable; set when disposition=TRIAGE (e.g. COLLISION, SUSPECTED_REBRAND, WEAK_EVIDENCE)
triage_question           # nullable; the human-readable question Phase 3 will email
                          #   e.g. "Are 'CloudWave' and 'Cloud Wave Inc.' the same vendor?"
```

---

## 2. The stages (composable, in order)

### Stage A — Normalize
Deterministic. For each candidate: lowercase, strip punctuation, normalize whitespace,
strip legal suffixes (Inc/LLC/Ltd/Limited/GmbH/Corp), remove noise words → `comparison_key`.
Preserve `raw_name` untouched. No matching yet.
- Out: candidates with comparison_keys.

### Stage B — Entity-type classification
Determine entity_type per candidate: COMPANY | PERSON | PRODUCT | INTERNAL | NON_VENDOR.
Deterministic rules first (person-name patterns, internal-department keywords, document-title
patterns); LLM only for genuinely ambiguous cases. Candidates typed PERSON / PRODUCT /
INTERNAL / NON_VENDOR are **dropped from vendor clustering** (recorded with reason, not silently).
- Out: company-typed candidates proceed; others set aside with reason.

### Stage C — Cluster + canonicalize (the hard core, the tuning loop lives here)
Deterministic clustering of company candidates by comparison_key + fuzzy similarity:
- Exact comparison_key match → same cluster.
- Fuzzy match above MERGE_THRESHOLD → same cluster.
- Fuzzy match in [REVIEW_THRESHOLD, MERGE_THRESHOLD) → same cluster but flag LOW_CONFIDENCE_MATCH.
- Below REVIEW_THRESHOLD → separate clusters.
Corroborating signals adjust: same domain/tax_id strengthens a merge; **conflicting domain
on similar names BLOCKS the merge** (collision — see Stage D).
Pick canonical_name per cluster (prefer the most complete legal form among members).
- Out: clusters, each with members + proposed canonical_name + comparison_key.
- **This is where the split-vs-merge threshold is tuned. Expect iterations.**

### Stage D — Collision + lifecycle flagging
- **Collision:** two clusters with similar names but conflicting signals (different domain,
  different country) → do NOT merge; flag both with `COLLISION` → TRIAGE.
- **Suspected rebrand/acquisition (empty maps this run):** if two clusters look related but
  there's no map entry, do NOT auto-link → flag `SUSPECTED_REBRAND` → TRIAGE. (Maps are empty
  this phase, so all rebrand/acquisition pairs go to triage — this is correct behavior.)
- Out: clusters annotated with collision/lifecycle flags.

### Stage E — Gate 1: disposition
Deterministic gate per cluster:
- **AUTO_CONFIRM** — strong cluster (exact/high-fuzzy + corroborating signal, or single clean
  candidate with PRIMARY source) and entity_type=COMPANY and no blocking flags.
- **PROVISIONAL** — company, plausible cluster, but weak (single REPORTED source, or
  LOW_CONFIDENCE_MATCH, or no corroborating signals).
- **TRIAGE** — COLLISION, SUSPECTED_REBRAND, ambiguous entity_type, or below review threshold.
  Sets `triage_reason` and `triage_question`.
- Out: ResolutionDisposition per cluster.

### Stage F — Write to Registry
- AUTO_CONFIRM → write CanonicalVendor status=CONFIRMED, aliases recorded.
- PROVISIONAL → write status=PROVISIONAL with flags.
- TRIAGE → write status=TRIAGE (vendor exists but marked), disposition carries the question
  for Phase 3 to email. Do NOT silently create a merged vendor for a collision.
- Out: Registry populated; dispositions returned.

---

## 3. Confidence gate thresholds (tunable constants, frozen for the build)
```
MERGE_THRESHOLD     = <set in tuning>   # ≥ this fuzzy score → same cluster
REVIEW_THRESHOLD    = <set in tuning>   # [review, merge) → cluster but flag low-confidence
AUTO_CONFIRM_MIN    = <set in tuning>   # cluster confidence ≥ this AND corroborated → auto
```
These are the dials. Start conservative (prefer splitting over merging — a duplicate vendor
is visible and fixable; a wrong merge corrupts reality). Tune against §7 until all six cases pass.

## 4. Flag library (subset for this phase)
`FUZZY_MATCH, ALIAS_MATCH, LOW_CONFIDENCE_MATCH, AUTO_CONFIRMED, GENERIC_NAME, MULTIPLE_MATCHES,
PERSON_ENTITY, PRODUCT_ENTITY, INTERNAL_ENTITY, NON_VENDOR_ENTITY, COLLISION, SUSPECTED_REBRAND,
WEAK_EVIDENCE, SINGLE_SOURCE_ONLY, PROVISIONAL_VENDOR, TRIAGE_REQUIRED`

## 5. The Gate-1 → Phase-3 seam
TRIAGE dispositions carry `triage_reason` + `triage_question`. Phase 3 (check-in loop) consumes
these unchanged — it does not re-derive the question. Design Stage E so the question is fully
formed here. This is why identity is built knowing the check-in loop is coming.

## 6. What is stubbed in this phase
- **Candidate extraction** (the upstream that produces CandidateIdentityBeliefs) — stub it:
  read the adversarial folder, hand-produce or lightly-extract candidates so Phase 1 has input.
  Real extraction is part of Phase 2 (classifier) / the pipeline.
- **Rebrand/acquisition maps** — empty. All rebrand pairs go to TRIAGE (correct this run).
- **External validation** (web/registry) — out of scope (later evidence lane).

## 7. Test contract — the adversarial folder (the definition of done)
The folder MUST contain these six cases, and the phase is done when each resolves correctly:

1. **One vendor, 3+ variants** ("CloudWave", "Cloud Wave Inc.", legal entity name) →
   ONE CanonicalVendor, all spellings as aliases, AUTO_CONFIRM. *(Tests clustering doesn't split.)*
2. **Collision** (two different "Phoenix Consulting", different domains) →
   TWO CanonicalVendors, both flagged COLLISION → TRIAGE, NOT merged. *(Tests merge doesn't over-merge.)*
3. **Rebrand pair** (Blackboard docs + Anthology docs, same entity) →
   TWO vendors this run (empty maps), flagged SUSPECTED_REBRAND → TRIAGE with a question.
   *(Tests we don't auto-link without a map, and route to human.)*
4. **Person** ("John Smith", a signatory) → typed PERSON, dropped from vendors, recorded.
5. **Internal department** ("IT Procurement") → typed INTERNAL, dropped, recorded.
6. **Document-title trap** ("Amendment 3 – Aramark") → resolves to vendor "Aramark", NOT a
   vendor named "Amendment 3". *(Tests title/contract noise doesn't become an entity.)*

Tests (mirror the prior phases' style):
- `Identity_ThreeVariants_ResolveToOneVendor` — case 1, asserts one vendor + 3 aliases + AUTO_CONFIRM.
- `Identity_Collision_NotMerged_BothTriaged` — case 2, asserts two vendors + COLLISION + TRIAGE.
- `Identity_RebrandPair_Triaged_NotAutoLinked` — case 3, asserts two vendors + SUSPECTED_REBRAND + a triage_question.
- `Identity_Person_TypedAndDropped` — case 4.
- `Identity_Internal_TypedAndDropped` — case 5.
- `Identity_DocumentTitle_ResolvesToRealVendor` — case 6.
- `Identity_RegistryPopulated_AliasesRecorded` — after the run, the Registry holds the canonical
  vendors with their aliases, ready for run-two recognition.

## 8. Done when
- All six cases pass as specified; Registry is populated with canonical + aliases + entity_type.
- Disposition output carries triage_reason + triage_question for every TRIAGE case (the Phase 3 seam).
- Stages A–F exist as independently-callable units (composable), not one fused method.
- Clustering thresholds tuned so NO wrong-merge occurs on the adversarial folder (splitting a
  true duplicate into two is a soft fail to fix; merging two real vendors into one is a HARD fail).
- No downstream (extraction, judgement, render, check-in email) built — out of scope.

## 9. The one hard checkpoint (the equivalent of "did fingerprints move")
**No wrong-merge.** The single failure that corrupts reality is merging two distinct vendors
into one. Before declaring done, confirm on the adversarial folder that the collision case (2)
and any near-name pairs produced SEPARATE vendors. Over-splitting (a duplicate vendor) is
acceptable and fixable; wrong-merge is not. This is the checkpoint to insist on before Phase 2.
