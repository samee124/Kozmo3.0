# Kozmo Phase E1 — Document-Type-Aware Extraction & the Metadata Store

**Status:** Specification — not yet started
**Depends on:** Click-path phase (complete), Supersession fix (complete, `b5f1325`)
**Precedes:** E2 (question banks per category), E3 (document retention + re-read), F (SQL Server)
**Owner:** Sam (deterministic core / extraction), integration boundary with Ritiesh frozen

---

## 0. One-line statement

Turn extraction from a fixed 5-fact skimmer into a **catalogue-driven, document-type-aware** engine that produces, from a single LLM pass per document, two routed outputs: **beliefs** (scored, confidence-weighted, the decision-driving core) and **metadata** (structured, retained, agent-facing, never scored). The deterministic core — Rubric, Index, Posture, tier math — is untouched. This is a generalization of existing config, not a rewrite.

---

## Part 1 — Why E1 exists

### 1.1 The current constraint

Extraction today asks **the same 5 questions of every document**, regardless of type: `sla_uptime`, `csat`, `payment_terms`, `renewal_date`, `annual_value`. The 5 exist because they map to the SaaS question bank's dimensions — the belief catalogue was scoped *down* to what the completeness demo needs, not *up* to what documents contain.

Consequences observed on real data:

- **A rich MSA yields ~2 beliefs.** Not because the document is thin, but because the extractor only recognizes 5 fact types. Termination clauses, liability caps, governing law, data-processing terms, warranties — read by the LLM, then discarded.
- **Type confusion.** IIVS's six per-engagement invoices each produced an `annual_value` belief from a "TOTAL DUE" line, because there is no periodicity check and no document-type awareness. Per-invoice milestone amounts masquerade as annual contract values.
- **Nothing is retained for agents.** To recommend "renegotiate the termination clause," an agent must know the termination clause exists. Today it doesn't — the clause was never captured.

### 1.2 Why this is a prerequisite, not a nicety

The platform's stated purpose is governance and action — confidence-gated autonomy, outcome memory, a closed decision loop. **Agents cannot act on evidence the system never captured.** A 5-fact skimmer can score a narrow completeness view; it cannot support an agent recommending an intervention grounded in a contract's actual terms. E1 is the step that makes the sensing layer deep enough for the action layer to stand on.

### 1.3 The governing insight: fixed → variable

The current system hardcodes catalogues (5 belief types, one SaaS lens) where reality is variable — documents assert different things, of different types, in different quantities. **E1 makes the catalogues config-driven and the extraction context-selected.** Every mechanism below is a generalization of an existing extension point (the catalogue, the classification stage, `DocTypeInferrer`, the tier system), not new infrastructure.

---

## Part 2 — What E1 delivers

Five layers. Each is described as a change to something that already exists.

### 2.1 Beliefs — catalogue-driven extraction

**Today:** `BeliefExtractionPrompt.System` hand-authors 5 rules as prose.

**E1:** the extraction prompt is **generated from the catalogue**. Each claim key in `claim_key_catalogue.*.json` carries its own:
- `definition` (what the fact is)
- `value_type` (numeric / date-string / enum / free-text)
- `dimension` (or `""` for structural)
- `positive_example` and `negative_example`
- optional `deterministic_guard` reference (see §5.1)

The extractor projects the relevant subset of catalogue entries into the prompt at runtime. **Adding a belief type becomes a config change + eval + re-record — not a code change.**

The catalogue grows from 5 keys toward 30+: `liability_cap`, `termination_notice`, `governing_law`, `delivery_terms`, `defect_rate`, `auto_renewal`, `notice_period`, `indemnification`, `warranty_term`, `data_processing_terms`, and so on — each held to the same abstain-when-ambiguous bar as the original 5.

> **Instance count is already document-driven.** A document yields as many belief *instances* as facts it states; a document stating none yields none (abstention). E1 expands the number of recognized *types*, not the per-document instance logic.

### 2.2 Document-type-aware schemas — type selects the subset

**Today:** classification (stage 2) and `DocTypeInferrer` determine document type and tier, but that classification does **not** drive what gets extracted — every document is asked the same 5.

**E1:** add a mapping **document type → extraction schema**, where a schema declares:
- which claim keys to extract (as beliefs)
- which metadata fields to capture (see §2.3)

Examples:

| Document type | Belief claim keys (scored) | Metadata fields (retained, not scored) |
|---|---|---|
| Invoice | `invoice_amount`, `invoice_date` | `po_reference`, `tax`, `remit_to`, `billing_period`, `line_items` |
| Purchase Order | `committed_amount`, `delivery_terms` | `requisition_ref`, `cost_center`, `approver`, `delivery_date` |
| MSA | `payment_terms`, `liability_cap`, `term_length` | `termination_clause`, `notice_period`, `governing_law`, `ip_terms`, `data_processing`, `warranty`, `confidentiality` |
| Order Form | `annual_value`, `payment_terms` | `skus`, `quantities`, `discount_schedule` |
| SLA Exhibit | `sla_uptime`, `support_tier` | `measurement_method`, `exclusions`, `credit_schedule` |
| Insurance Certificate | `coverage_amount`, `policy_dates` | `insurer`, `policy_numbers`, `named_insured` |

**This fixes the type-confusion at the root.** An invoice's amount is extracted as `invoice_amount` (a transaction fact), not `annual_value` (a contract fact). The IIVS per-invoice problem disappears because the invoice schema never produces an `annual_value` belief.

**One extraction call per document**, projecting the type's schema into the prompt; beliefs and metadata come out of the same pass, routed to their respective stores.

### 2.3 Metadata — a second store, with a hard wall

**New structure** (`VendorKnowledge` / `DocumentMetadata`): structured fields per document type, keyed to vendor + document, retained and queryable by agents — and **never confidence-scored, never read by scoring** (`RubricModule`, `IndexModule`, `PostureModule`, completeness).

The metadata store is what lets extraction capture *a lot* while keeping the scored core *clean*. Thirty extracted clauses inform agent action; they do not become thirty low-confidence beliefs polluting a dimension score.

**The wall is CI-enforced, not conventional** — a new invariant lane (BannedSymbols-style, analogous to the existing lanes): no reference to the metadata store from any scoring assembly. Convention drifts; the CI lane does not.

### 2.4 Documents — persistence for the metadata/beliefs to point at

**In E1 (minimal):** the extracted text is already stable (the `.txt` corpus proves it). E1 persists the association needed so beliefs and metadata have a durable source reference — the provenance locator resolves to a stored document identity (id, vendor, type, tier, hash).

**Deferred to E3:** full-text retention as a queryable store, and the **bounded query-time re-read** capability (agent asks a stored document a question, gets an answer at INFERRED tier capped ≤ 0.3 — the Hebbia mitigation). E1 lays the reference; E3 builds the re-read.

> Confirmed in scope discussion: full document *retention as a re-readable store* waits for E3. E1 only needs enough document identity for provenance to resolve.

### 2.5 Answering — deterministic retrieval over the open pool

**Today:** `QuestionAnsweringStage` hands the LLM the question text plus **every** belief, unfiltered, and the LLM cites what it used. This works at 5 beliefs. At 100 it does not — the pool is too large to pass wholesale.

**E1:** keep retrieval **deterministic, not semantic search.** Each question declares its dimension (already does) and, optionally, its relevant claim keys; the stage filters the belief pool by that declaration, applies a size cap (the `MaxDocChars` pattern), and passes the filtered set to the LLM to ground or abstain. Same proven grounding mechanism, fed a config-filtered pool instead of the whole store.

- Glass-box preserved: the retrieval is auditable config; the grounding is the LLM discipline already in place.
- The **ordinal-id fix** (`1b56b1c`) carries forward unchanged — beliefs are labelled by ordinal in the prompt, citations mapped back to real ids.

> **Note on the question bank's design.** Diagnosis established the question bank has *no* claim-key field today — grounding is pure LLM discretion by deliberate design. E1's optional per-question claim-key declaration is an **additive filter for pool-sizing**, not a rigid 1:1 mapping. If the pool is small enough, the filter is a no-op and the existing "LLM sees all beliefs" behavior stands. Do **not** retrofit a mandatory claim-key mapping onto the question bank — that fights the glass-box design and was explicitly considered and rejected.

---

## Part 3 — The load-bearing invariant

> **The completeness/Q&A engine consumes beliefs. Beliefs = what the question banks ask about. Metadata is strictly additive — things the question banks do NOT ask about. Never demote a question-bank-relevant fact from belief to metadata.**

This is the single rule that keeps Q&A working through E1. Its corollaries:

- The existing 5 keys **stay beliefs** — they are what the question bank asks about. E1 adds types around them; it never moves them.
- Expanding the belief catalogue is additive (more grounding for the same questions — the key insight: *8 stable questions grounded in whichever of 100 beliefs are relevant*, not more questions).
- Adding metadata is additive (new agent-facing knowledge, invisible to Q&A).
- Neither expansion removes what Q&A depends on.

**Violating this rule breaks Q&A.** It is the acceptance test for every schema decision in E1: *does this change keep every question-bank-relevant fact as a belief?*

---

## Part 4 — Deferred items that land in E1

These were surfaced during the click-path and cleanup work and deferred *specifically because they depend on E1's extraction changes*. They are E1 scope:

| Item | What it is | How E1 resolves it |
|---|---|---|
| **`annual_value` periodicity guard** | Milestone/invoice amounts wrongly extracted as annual contract value (IIVS) | The invoice schema extracts `invoice_amount`, not `annual_value`; a deterministic guard rejects periodic/milestone language for `annual_value` (mirrors `ContainsTerminationLanguage`) |
| **Per-invoice claim key** | Invoice totals need transaction semantics, not contract semantics | New `invoice_amount` claim key with sum/period semantics, distinct from `annual_value` |
| **Real per-document dates (`observedAt`)** | KYV stamps the run's `now`; supersession's recency leg has no real signal | Document-type-aware extraction of the effective/signed/invoice date per type; supersession's same-tier recency leg becomes semantically correct (no logic change — it finally has a real date) |
| **Completeness-definition consolidation** | `expected_belief_sets` duplicates rather than derives; drifted from the catalogue | Catalogue becomes the single source; `expected_belief_sets` derives from it. The question-bank retrofit is **not** done (see §2.5 note) |
| **Fixture `Criterion` mismatch** | `FixtureBeliefs.cs` uses Criterion strings matching no catalogue key; works only because Q&A doesn't validate | When the catalogue is authoritative, retrofit fixtures to real catalogue claim keys |
| **`RulesExtractor` translation-dict bug** | Looks up `sla_uptime`/`csat` but the rubric keys them `uptime_sla`/`csat_score`; the translation dict exists in `BeliefPersistenceStage` but isn't reused | Consolidate onto one keying path as the catalogue becomes the source of truth |
| **Banded-value display** | `renewal_date` renders as raw epoch, scored beliefs show `1.00` in the File tab | Fixed as belief shapes settle (per-invoice keys, real dates change what's displayed); display layer only, scoring untouched (option 1a) |

---

## Part 5 — Disciplines that make E1 safe

All three are extensions of disciplines already enforced in this codebase.

### 5.1 Precision scales with breadth

The two overreach bugs at 5 keys (`study-quality`-as-CSAT, `termination-notice`-as-`payment_terms`) **multiply at 30**. Every new claim key gets:
- an explicit abstain bar (when the document doesn't clearly state it, emit nothing)
- a negative example in the catalogue entry
- where the rule is crisp, a **deterministic post-extraction guard** in code (the `ContainsTerminationLanguage` / `ContainsNonCustomerQualityLanguage` pattern) — the LLM extracts and quotes; deterministic code filters and computes

Document-type-awareness *helps* precision: asking an invoice for invoice-things and an MSA for contract-things is inherently more precise than asking every document the same 5.

### 5.2 The wall (metadata never enters scoring)

CI-enforced invariant lane. No scoring assembly references the metadata store. This is what permits broad metadata capture without risking the glass-box score. Non-negotiable and machine-checked.

### 5.3 Cassette economics improve

Type-specific prompts mean **prompt-per-schema**. A schema edit invalidates only *that schema's* cassette entries — strictly better isolation than today's single-prompt model, where any prompt edit invalidates every document's cache key. E1 makes re-records cheaper and more targeted, not more expensive.

---

## Part 6 — What E1 does NOT touch

Explicit non-goals, to keep the phase bounded:

- **The deterministic core** — `RubricModule`, `IndexModule`, `PostureModule`, tier math, `ConfidenceFloor`, decay. Unchanged. This is the proof E1 is a generalization, not a rewrite. If a core scoring file changes, the scope has slipped.
- **Question banks per vendor category** → E2.
- **Vendor-category classification** (SaaS / supplier / consultancy / counterparty) → E2.
- **Full document retention as a re-readable store + bounded re-read** → E3.
- **Signal-interpretation intake** (Slack / check-ins / integrations → LLM → beliefs) → E-family, separate. *Note: this is the same architectural pattern as E1's document-type-aware extraction (typed intake → interpreted beliefs → shared model). E1's interpretation framework should be built so the signal intake can reuse it later.*
- **SQL Server migration** → F, after E1's schema settles (migrate once, not twice).
- **Objectives / Commercial Matters / Super Indexes / ledgers / learning** → H.
- **Generated questions** — permanently rejected; destroys determinism. Questions stay authored, versioned, selected.

---

## Part 7 — Sequencing within E1

Suggested build order, each step proven through the click-path (not just tests) before the next — the lesson of the click-path phase: a green suite proved the engine while the product was broken.

1. **Catalogue schema expansion** — extend the catalogue entry format (definition, value_type, examples, guard ref). No behavior change yet; the extractor still reads the existing 5. Prove the generated prompt reproduces current behavior byte-identically (golden pins hold).
2. **Prompt generation from catalogue** — replace hand-authored `BeliefExtractionPrompt` prose with catalogue-projected generation, still for the 5 keys. Re-record; prove identical extraction.
3. **Document-type → schema mapping** — wire classification/`DocTypeInferrer` to select a schema. Start with invoice + MSA + Order Form schemas. Prove the IIVS `annual_value`→`invoice_amount` fix on real data.
4. **Metadata store + CI wall** — add the store and the invariant lane *before* extracting metadata into it, so the wall exists from the first write.
5. **Metadata extraction** — extend schemas to capture metadata fields; route the single-pass output to both stores.
6. **Deterministic retrieval in answering** — add the config-filtered pool + size cap to `QuestionAnsweringStage`. Prove Q&A unchanged for the existing question set.
7. **Fold in the deferred fixes** (Part 4) — periodicity guard, real dates, consolidation, fixture Criterion, RulesExtractor, banded-value display.
8. **Full dry-run** — the phase acceptance test: a rich vendor (MSA + Order Form + invoices) shows a full belief set correctly typed, metadata captured and agent-queryable, scoring unchanged, Q&A grounding richer on the same questions, no type-confusion, honest gaps still honest.

---

## Part 8 — Acceptance criteria

E1 is done when:

- The extraction prompt is generated from the catalogue; adding a claim key requires no code change.
- At least invoice / MSA / Order Form document types select distinct extraction schemas.
- A rich vendor yields a document-type-appropriate belief set (invoice amounts as `invoice_amount`, contract terms as their own beliefs) — the IIVS `annual_value` confusion is gone on real data.
- Metadata is captured into its store and is agent-queryable; the CI wall lane is green and proves no scoring assembly references it.
- The load-bearing invariant holds: every pre-existing question-bank-relevant fact is still a belief; Q&A percentages on the existing question set are unchanged (the disjointness confirmed in the completeness diagnosis).
- Deterministic retrieval handles a large pool without passing it wholesale; grounding/abstention behavior preserved.
- The deferred fixes (Part 4) are resolved.
- Deterministic core files are untouched (generalization, not rewrite).
- Golden 26/26, CI invariants green (including the new wall lane), full dry-run passes through the click-path.

---

## Appendix — Glossary

- **Belief** — a scored, confidence-weighted, dimension-mapped, evidence-grounded fact. Feeds Rubric/Index/Posture/completeness. The decision-driving core.
- **Metadata** — a structured, retained, agent-facing fact. Never scored, never read by scoring. Informs agent action.
- **Claim key** — a catalogued fact type (`payment_terms`, `liability_cap`, `invoice_amount`…). Carries dimension, value_type, examples, optional guard.
- **Extraction schema** — the per-document-type declaration of which claim keys (beliefs) and which metadata fields to extract.
- **The wall** — the CI-enforced invariant that no scoring assembly references the metadata store.
- **The load-bearing invariant** — beliefs = what question banks ask; metadata additive; never demote a question-bank fact to metadata.
