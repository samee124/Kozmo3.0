# Kozmo Phase E-signal — Email & Signal Intake

**Status:** Specification — **FINALIZED** (three subsystem decisions diagnosed against the codebase and decided — §2.4). Ready to build.
**Depends on:** E1 (document-type-aware extraction + metadata store — CLOSED). Reuses E1's typed-intake framework.
**Precedes:** E-sheet (spreadsheet intake), E-docdepth (invoice/PO document depth), and eventually the other signal sources (Slack, third-party integrations) which reuse this phase's framework.
**Owner:** Sam (deterministic core / intake), integration boundary with Ritiesh frozen

---

## 0. One-line statement

Turn the **338 `.eml` files** (currently invisible to the pipeline — 2:1 more than PDFs) into two routed outputs: **low-tier beliefs** (where an email explicitly states a catalogued fact — feeding the *existing* completeness/vendor-file consumers immediately, at correspondence-tier confidence that defers to documents) and **relationship signals** (responsiveness, sentiment, commitments, issues — feeding the future momentum layer, and surfacing now where mappable). Parse MIME → interpret → route beliefs to the belief store (low tier) and signals to the signal store. Email is **correspondence, not a clause-bearing document** — it gets signal-interpretation, not MSA-style clause-metadata extraction. Cross-source conflicts (email vs. contract) are handled by the belief store's **existing** tier-then-recency + keep-both-for-contradiction model: documents win the score, disagreements surface. This is the first concrete instance of the "signal-interpretation intake" the E1 spec named as a future sibling; the framework built here is reused by later signal sources (Slack, integrations).

---

## Part 1 — Why E-signal exists

### 1.1 The finding

A corpus audit found the real workspace contains **496 files**: 151 PDFs (the only type the pipeline reads today), **338 `.eml` files**, 6 `.xlsx`, 1 `.docx`. The 338 emails are concentrated in Scenario 07 ("Email-Driven Relationship," 300 files) plus smaller amounts across Scenarios 01/03/04/05. **Email outnumbers PDF more than 2:1, and is completely invisible to the pipeline** — never enumerated, never opened, never seen by any code path. Scenario 07 has no PDF value without them.

### 1.2 Why this is prioritized ahead of more document depth

E1 made document extraction deep. But depth on the PDF half while the email half sits entirely dark is polishing one input while ignoring a larger one. Two reasons email intake beats further document-depth expansion:

- **Breadth over depth.** Half the corpus being unread is a bigger deficit than invoice metadata being less rich than MSA metadata. The highest-*volume* input is unprocessed.
- **Email is where the relationship lives.** The strategy documents emphasize relationships as momentum drivers — "trust, sponsorship, responsiveness, stakeholder alignment." Contracts encode *terms*; emails encode the *relationship*. For a system whose destination (Phase H) is commercial momentum, the relationship signal in 338 emails is high-value, not incidental.

### 1.3 Email is a signal, not a document — the governing distinction

An `.eml` is structurally **correspondence**: one message, a sender and recipients, a point-in-time exchange. It does **not** have the "contract with clauses" shape that E1's multi-pass metadata groups target. Forcing email through the document path would be a category error — there is no `termination_clause` or `governing_law` in a status-update email, and a single short email does not warrant a 4-5-field-per-group metadata schema the way a 41,000-character MSA does.

So email belongs to a **different intake pattern**: not "extract clauses from a document," but "interpret what this correspondence tells us about the relationship." It produces **signals**, not document-metadata.

---

## Part 2 — Scope: email produces BOTH low-tier beliefs AND signals

**Decided: Option 2.** Email feeds the system two ways, because signals-only would have no consumer today (the momentum layer is Phase H, unbuilt), whereas beliefs have an existing consumer (completeness, the vendor file) *now*.

- **Beliefs (low tier):** where an email explicitly states a catalogued fact (a `payment_terms`, a clear commitment mapping to a claim key), it produces a **belief at correspondence tier** — a new tier at or below `Reported`, the weakest. This grounds the same completeness questions documents do, and shows in the vendor file **immediately**, using consumers that already exist.
- **Signals:** responsiveness, sentiment, issues raised, stakeholder engagement — things with no claim-key equivalent — are stored as signals for the future momentum layer (Phase H), and surface now where mappable (an `issue_raised` → a flag/check-in via existing mechanisms).

### 2.1 Why this is useful NOW (the "what's the use" answer)

Signals-only signals would sit in a store feeding nothing until Phase H — dead weight. Beliefs, by contrast, feed the **existing** completeness/vendor-file machinery the moment they're written. So email producing low-tier beliefs makes it immediately useful: an email confirming a payment term, raising an issue that maps to a dimension, or filling a gap a document is silent on shows up in the vendor's assessed picture today.

### 2.2 Why the cross-source collision is ALREADY solved (not new work)

The concern with email→belief grounding is "email says Net 45, the signed contract says Net 30 — which wins?" **The belief store already answers this.** Its supersession is tier-then-recency with keep-both-on-strong→weak (for contradiction detection) — the exact model built and committed in the cleanups. Email beliefs are **low-tier by nature** (correspondence, never Primary). So:

- **Email contradicts a document** (email Net 45 vs. contract Net 30): the document's Primary belief **wins the score** (tier rule); the email's low-tier belief is **kept**, so contradiction detection **surfaces the disagreement** ("a recent email discusses Net 45, but the contract says Net 30"). That surfaced conflict is itself a *valuable signal*, not noise.
- **Email corroborates a document** (email confirms Net 30): corroboration, no conflict.
- **Email states something documents are silent on**: the email is the sole, low-tier source — grounds the question at low confidence, honestly, filling a gap.

**None of this is new machinery.** Email beliefs are just low-tier beliefs entering a tier model that already ensures weak evidence defers to strong, corroborates where it agrees, and surfaces where it conflicts. "Email updates beliefs" does **not** mean "email overrides your contract" — the tier system structurally prevents that.

### 2.3 The real risk: precision, not collision

The genuine hazard of Option 2 is **not** the cross-source conflict (solved above) — it's **extraction precision on informal text.** Email is chatty; an offhand "sounds like Net 45 works" is not a real commitment to `payment_terms`. Extracting a belief from casual correspondence is more error-prone than from a contract, and a misread email belief — even low-tier — adds noise to the vendor's picture.

So Option 2 requires the **abstain-over-guess discipline harder than documents did**: only produce a belief from an email when it is a **clear, explicit, unambiguous statement of a catalogued fact** — not a casual mention, a hypothetical, a question, or a negotiation-in-progress. When in doubt, it's a *signal* (sentiment/discussion), not a *belief*. This is the load-bearing discipline of the phase (see Part 7).

### 2.4 Finalized subsystem decisions (diagnosed against the codebase)

Three subsystem-shaping questions were diagnosed against the actual code and are now **decided** (not open). They shape the build.

**Decision 1 — The correspondence tier: ADD, weight 0.25, provably isolated.**
- Tiers are **string-keyed config** (`source_tiers.saas.v1.json`, `Dictionary<string, SourceTierConfig>`), NOT fixed-size arrays. The `SourceTier` enum ordinals are already scrambled and carry no meaning; persistence is string-keyed (`source_tier TEXT`). So a new member is safe at any ordinal.
- `RubricModule` **never references `SourceTier`** — it computes Σ(value×confidence)/Σ(confidence) over surviving confidence. Tier acts *only* as a write-time confidence clamp (`SqliteEntityStore.AppendBeliefAsync`). Structural-vs-scored is decided by the claim key's `class` field, never by tier.
- **DECIDED:** add `Correspondence` to the enum (any unused ordinal) + a `"Correspondence"` entry in `source_tiers.saas.v1.json` at **weight 0.25** — strictly the weakest (below Reported 0.5 and Inferred 0.3, no tie). It is a **scored** claim key (not structural), so email facts flow into `RubricModule`'s weighted average with real but weak confidence — exactly the Option-2 safety mechanism.
- **Isolation is provable and required as an acceptance gate:** nothing iterates "all tiers"; the two tier-referencing CI checks name Reported/Inferred literally; no existing belief can carry a tier that doesn't exist yet. So the tier is **inert until used** and adding it changes **no document-driven score** — prove this on the real corpus.
- **Note the pre-existing drift:** a second, disagreeing ceiling table exists in `DocTypeInferrer.TierCeiling` (non-monotonic — pre-existing, NOT caused by this). If email routes through the KYV extractor, mirror `Correspondence` into it at a consistent weak value. Do **not** fix the existing drift as part of this phase.

**Decision 2 — Signal storage: REUSE `Km.Store.Metadata` (signals as a type within it), NOT the `Signal`/`SubmitSignalAsync` path.**
- The existing `Signal`/`SubmitSignalAsync` path was diagnosed and **rejected for email signals** — three independent mismatches: (a) it *requires* classifying into a mandatory `Dimension` + a 0-1 rubric `Value` (`ObservationModule.Classify`), so responsiveness/sentiment/issue would need a **fabricated dimension/score mapping** — the exact invented-mapping E1 rejected in the 59a767d fixture cleanup; (b) its supersession is **winner-take-all keyed on (EntityId, Dimension, Criterion)** — it would **erase accumulating relationship history** (two emails shouldn't overwrite each other); (c) `AnchorConfidences` floors a new belief at a predecessor's confidence — meaningless for non-scored signals.
- **DECIDED:** relationship signals are **metadata-like** (structured, retained, not scored, agent-queryable) — so store them **in `Km.Store.Metadata`** as another type/`DocumentType` alongside document metadata (`EmailSignal`/`RelationshipKnowledge` shape: EntityId, message ref, SignalType, Value, Derivation, ObservedAt; **append-only**, never touching `Belief`/`IEntityStore`). This **rides the existing CI wall** (the metadata-store lane already covers every scoring assembly — no new lane needed) rather than building a new store + a new wall lane. Confirm during build that the signal shape fits `Km.Store.Metadata` cleanly; if it strains the shape, fall back to a distinct store extending the same wall lane.

**Decision 3 — Email→vendor resolution: REUSE the document-candidate/clustering path (for identity only), parse header domains, and STOP DISCARDING `EntityRole`.**
- Resolution infrastructure **already works** and is document-type-agnostic: `RoleHint` is **LLM-assigned** per extracted party from relationship cues (`ExtractionPrompt` rule 4 — "(Sponsor)", "BILL TO", "engaged/retained"), so an email body already extracts party roles. An email *from the customer* mentioning the vendor by name already resolves the vendor correctly today (if ingested). The `.eml` files contain literal `From:`/`To:` header lines (Scenario 07 confirmed); there's no header-domain parser yet, and `KyvProgramRunner` globs `*.pdf` only.
- **The real gap is narrow:** distinguishing "email **from** the vendor" vs. "email **about** the vendor written by the customer/internal" for the same named party — which needs the **sender's own role**, not just party-roles-in-body. And the fix is **"stop discarding data that already exists"**: `EntityRole` is computed at Stage C (`ClusteringStage`) and **thrown away at Stage F** (`RegistryWriter` builds `CanonicalVendor` with no `EntityRole` field — the role is used once as a binary gate then dropped). This is the discarded-`EntityRole` finding from E1's naming diagnosis.
- **DECIDED (3 parts):** (1) ingest `.eml` as a document type **through the existing `DocumentCandidateExtractor`/`ClusteringStage` match-by-name path** — **for identity resolution only** (see the coherence note below); (2) extend the extraction prompt to parse `From:`/`To:` header domains into the existing `CandidateSignals.Domain` field (config-level, cheap); (3) **stop discarding `EntityRole`** — carry it through to where the signal store keys its records, so email intake distinguishes "from vendor" vs. "about vendor." Only #3 is a genuine change, and it's "stop dropping existing data," not new architecture.

> **Coherence note — "email as a document type" vs. "email is a signal":** These are not in conflict. Email is ingested *through the document-candidate/clustering infrastructure* for the **identity-resolution** step only (who is this about?) — reusing that path rather than building a parallel one. But email is **interpreted** as a **signal** (the signal soft edge, §3.2), NOT run through document clause-metadata extraction. Two distinct stages: *resolution* reuses the document path; *interpretation* is the new signal soft edge. "Email as a document type" means for resolution, never "email gets clause-metadata."

> **Two-registries clarification:** `Ii.Spine.EntityRegistry` (used only for renewal-date lookups) is **unrelated** to the identity/`CanonicalVendor` resolution pipeline. The spec never conflates them; email resolution concerns the *identity* pipeline (Stages A–F), not `Ii.Spine.EntityRegistry`.

---

## Part 3 — What E-signal delivers

Four layers, mirroring E1's structure. Where possible, each reuses an E1 mechanism.

### 3.1 Ingestion — enumerate and parse email

**Today:** `KyvProgramRunner` Stage 1 is `Directory.EnumerateFiles(workspacePath, "*.pdf", ...)` — PDF only.

**E-signal:**
- Enumerate `.eml` files (decided: ingest as a document type through the existing `DocumentCandidateExtractor`/`ClusteringStage` path — **for identity resolution only**, per §2.4 Decision 3 and its coherence note). A dedicated signal-ingestion stage vs. extending Stage 1 — see §5.
- **Parse MIME properly.** The `.eml` files are genuine RFC 5322 / MIME (verified: real headers, RFC 2047 encoded Subject lines) — **not plain text.** This requires an email-parsing library (**MimeKit** — the standard .NET choice; no email parser is currently referenced anywhere in the repo). Parse into: sender, recipients, date, subject, body (decoded), thread/reference headers. Parse `From:`/`To:` header domains into the existing `CandidateSignals.Domain` field (§2.4 Decision 3).
- Extract clean body text (decoded, HTML-stripped where needed) as the input to interpretation.

### 3.2 Interpretation — email → beliefs + signals (the soft edge)

**This is the signal-interpretation soft edge — the sibling of E1's document extraction.** An LLM pass reads a parsed email and interprets it into two routed outputs: **beliefs** (explicit catalogued facts) and **signals** (relationship intelligence). Reuses E1's pattern: catalogue-driven, prompt generated from catalogue entries, abstain-when-ambiguous.

**Beliefs from email** (route to the belief store, at correspondence tier):
- Only where the email **explicitly and unambiguously states a catalogued fact** — e.g. an email that says "confirming our agreed Net 45 payment terms" → a `payment_terms` belief at correspondence tier. The claim keys are the *same* catalogue as documents (payment_terms, etc.) — email is just another (weak) source for them.
- **Hard abstain bar** (Part 2.3): casual mentions, hypotheticals, questions, negotiations-in-progress → NOT beliefs. "Maybe Net 45?" or "what if we did Net 45?" is a *signal* (a discussion), never a belief. When in doubt, signal not belief.
- Enters the belief store as low-tier; the existing tier-then-recency + contradiction model handles document-vs-email conflict (Part 2.2).

**Signals from email** (route to the signal store — relationship intelligence with no claim-key equivalent):
- `responsiveness` — reply latency / engagement cadence (**derive deterministically** from timestamps + thread structure where possible — not LLM; see Appendix #3)
- `sentiment` — tone toward the relationship (positive / neutral / strained), grounded in quoted text
- `commitment` — a promise stated ("we'll deliver by Friday") — *distinct from a belief*: a commitment is a trackable future obligation, not a current catalogued fact
- `issue_raised` — a problem, complaint, or escalation ("the last delivery was late")
- `stakeholder_signal` — who is engaged, contact changes, sponsorship
- `request` — an ask requiring action

Each output (belief or signal) carries a **grounded quote** (evidence span — same grounding integrity as document beliefs), sender, timestamp, source email reference. Ungrounded outputs are dropped (evidence-span-or-nothing, per E1's rule).

**The belief-vs-signal routing is the crux of interpretation:** the prompt must clearly distinguish "an explicit statement of a catalogued fact" (belief) from "relationship information" (signal). This is where the precision discipline (Part 2.3, Part 7) lives — err toward signal, because a wrong signal is low-stakes (feeds the future momentum layer) while a wrong belief pollutes the vendor's assessed picture now.

**Attention-wall discipline applies:** per E1's finding (~5 categories per pass = high recall, 18 = near-zero), if the combined belief-key + signal-type set is large, interpret in attention-sized groups. Emails are short, so a single email likely fits one or two passes — but the *sizing discipline* carries over.

### 3.3 Storage — beliefs to the belief store, signals to `Km.Store.Metadata`

Two routed destinations (both decided — §2.4):

- **Beliefs** → the existing **belief store** (`IEntityStore`), at **correspondence tier (weight 0.25)**. They feed completeness/vendor-file immediately, and the existing tier-then-recency + contradiction machinery handles conflicts with document beliefs. **This is the reuse that makes email useful now** — no new consumer needed.
- **Signals** → **`Km.Store.Metadata`** (relationship signals as a type/`DocumentType` within it — `EmailSignal`/`RelationshipKnowledge`: EntityId, message ref, SignalType, Value, Derivation, ObservedAt; append-only). This rides the **existing CI wall** — no new store, no new lane. NOT the `Signal`/`SubmitSignalAsync` path (rejected — §2.4 Decision 2).

> **The correspondence tier (weight 0.25) is the safety mechanism.** Because email beliefs are strictly the weakest tier, the existing tier system structurally ensures email always defers to documents (Part 2.2). It's a real scored belief (nonzero, weak) — not structural (Confidence-0). **Signals do NOT enter scoring** — they live in `Km.Store.Metadata` behind the existing wall, which the scoring assemblies cannot reference.

### 3.4 Thread awareness — email is conversational

Unlike a document, emails come in **threads**. Interpretation should be thread-aware where it matters: a reply's sentiment relative to the thread, responsiveness measured across a thread's latencies, a commitment tracked to its follow-up. Scenario 07 (300 emails, "Email-Driven Relationship") is specifically a conversational corpus — thread structure is signal, not noise.

---

## Part 4 — Reuse of E1's framework

The E1 spec explicitly reserved this: *"E1's interpretation framework should be built so the signal intake can reuse it later."* Concretely, E-signal reuses:

- **The typed-intake pattern** — typed input → catalogue-driven LLM interpretation → grounded structured output → shared model. Email is a new *input type* in this pattern.
- **Catalogue-driven prompts** — a signal-type catalogue (like the claim-key catalogue) with definitions, examples, and prompt fragments; the interpretation prompt generated from it. Adding a signal type = catalogue edit.
- **The attention-sizing discipline** — ~4-5 categories per pass if the signal set is large (though single emails likely fit one pass).
- **Evidence-span-or-nothing grounding** — every signal cites its quote; ungrounded signals dropped.
- **The wall discipline** — *signals* don't enter scoring; only low-tier *beliefs* do. CI-enforced for signals if they share storage with beliefs.
- **The tier-then-recency + contradiction model** — reused as-is to handle email-vs-document belief conflicts (email is low-tier, defers to documents, conflicts surface).
- **The corpus-diff acceptance gate** — verify interpretation on the real 338-email corpus, not just synthetic tests.

**What is genuinely new (not reusable from E1):**
- MIME parsing (MimeKit) — documents are PDF text; email is MIME.
- Thread awareness — documents are standalone; emails are conversational.
- Timestamp/latency-derived signals (responsiveness) — partly deterministic, not LLM.
- The signal-type vocabulary itself (responsiveness/sentiment/commitment/issue) — different from document claim keys.

---

## Part 5 — Sequencing within E-signal

Each step proven through the real path (corpus diff / observation on real emails), not just tests — per E1's lesson. Subsystem decisions are settled (§2.4), so the sequence starts with the tier (the safety foundation) and builds up.

1. **Correspondence tier (the safety foundation, §2.4 Decision 1).** Add `Correspondence` to the enum + `source_tiers.saas.v1.json` at weight 0.25; mirror into `DocTypeInferrer.TierCeiling` if the email path uses it. Prove: **inert until used** — adding it changes NO document-driven score (full corpus diff), CI green, hash test unaffected. This is the isolation gate before any email data exists.
2. **MIME parsing + ingestion.** Add MimeKit; enumerate and parse `.eml` into structured (sender/recipients/date/subject/body/thread), parsing `From:`/`To:` domains into `CandidateSignals.Domain`. Reuse `DocumentCandidateExtractor`/`ClusteringStage` for identity resolution only. Prove: the 338 files parse (real MIME, encoded headers), PDFs untouched. No interpretation yet.
3. **Stop discarding `EntityRole` (§2.4 Decision 3, part 3).** Carry the Stage-C `EntityRole` through to persistence so "from vendor" vs. "about vendor" is distinguishable. This is the one genuine resolution change — "stop dropping existing data." Prove document resolution is unchanged (the role was discarded before; carrying it must not alter existing vendor resolution).
4. **Interpretation catalogue + prompt (belief-vs-signal routing).** Define the interpretation catalogue: which email-stated facts map to existing claim keys (→ correspondence-tier beliefs) and the signal-type set (responsiveness/sentiment/commitment/issue_raised/stakeholder_signal/request → signals). Generate the prompt (E1's pattern). The **belief-vs-signal routing** is the crux — the prompt must distinguish explicit catalogued facts from relationship information. Prove on a handful of real emails first.
5. **Interpretation pass — email → beliefs + signals.** Wire the LLM interpretation, grounded (evidence-span-or-nothing), cassette-recorded. Beliefs route to the belief store at correspondence tier; signals route to `Km.Store.Metadata`. Responsiveness derived deterministically (not LLM). Prove recall AND routing correctness on a sample.
6. **Storage wiring + wall (§2.4 Decision 2).** Signals into `Km.Store.Metadata` (as a type within it); extend the existing wall lane's banned-namespace check to cover the signal type. Prove the wall holds (scoring can't read signals) and beliefs flow into completeness.
7. **Thread awareness.** Add thread-relative interpretation where it matters (responsiveness latencies, reply sentiment).
8. **Full dry-run — the acceptance test.** Run the real 338-email corpus (Scenario 07 especially): beliefs + signals captured with grounding, correct belief-vs-signal routing, cross-source conflict working (email defers to documents, conflicts surface), scoring provably untouched by signals + unchanged for documents, no regression to E1 or the click-path.

---

## Part 6 — What E-signal does NOT touch

- **The deterministic scoring CORE** — Rubric/Index/Posture math is untouched. Email beliefs enter as a low tier through the *existing* belief path; the scoring *logic* doesn't change (it already weights by tier). Adding the correspondence tier is a tier-weight/config addition, not a scoring-logic change.
- **SIGNALS do not enter scoring** — responsiveness/sentiment/issues feed the relationship/momentum side (Phase H), not the scored dimensions. Only *beliefs* (explicit catalogued facts) enter scoring, at low tier. If signals share storage with beliefs, the metadata-store CI wall discipline applies: signals not read by scoring assemblies.
- **Document extraction (E1)** — PDFs and their belief/metadata extraction are untouched. Email is a parallel intake, not a change to the document path.
- **The tier-then-recency + contradiction MODEL** — reused as-is, not modified. Email beliefs are new low-tier inputs to the existing model; the model's logic (documents win, conflicts surface) is not changed.
- **The other signal sources** (Slack, third-party integrations) — this phase builds the *framework* they reuse, not the sources themselves. Later, plugging into the pattern established here.
- **Outbound email + check-in responses (Phase G)** — *inbound unsolicited* email ingestion (this phase) is distinct from the *check-in loop* (Phase G): composing/sending outbound questions, and *targeted* interpretation of a response ("does this reply answer the specific question we asked?"). **But the overlap is real and deliberate:** a check-in response arriving by email is inbound email that must be parsed and entity-resolved — exactly E-signal's foundation. So **E-signal builds the reusable inbound-email foundation** (MIME parsing, email→structured, address→vendor resolution) that G reuses; G adds only the *targeted, question-scoped* interpretation on top (which needs the check-in context E-signal doesn't have). Same pattern as E1→E-signal reuse. E-signal's *open* interpretation (what does this email tell us) and G's *targeted* interpretation (does this answer the asked question) are different tasks on the shared parsing/resolution base.
- **The relationship/momentum consumer layer** — *signals* are produced here; the layer that reasons over them for momentum/health is Phase H. This phase fills the signal store; H uses it. (Signals captured now are valuable even before H — the data H will reason over, queryable by agents meanwhile. Email *beliefs*, unlike signals, DO have a consumer now: completeness/vendor-file.)

---

## Part 7 — Disciplines

Extensions of what E1 and the cleanups established:

- **Belief-vs-signal precision (THE load-bearing discipline of this phase)** — only produce a *belief* from an email when it explicitly and unambiguously states a catalogued fact. Casual mentions, hypotheticals, questions, negotiations-in-progress → *signals*, never beliefs. Err toward signal: a wrong signal is low-stakes (feeds the future momentum layer); a wrong belief pollutes the vendor's assessed picture now. This is the email analogue of the abstain-over-guess discipline — and stricter, because informal text is more error-prone than a contract. (The `annual_value`-magnet lesson: watch for the email equivalent — an offhand dollar figure or "sounds good" becoming a phantom belief.)
- **Grounding integrity** — every belief AND signal cites its evidence quote; ungrounded outputs dropped. Same rule as document beliefs.
- **The correspondence tier does the safety work** — email beliefs are low-tier by construction, so the existing tier system structurally prevents them overriding documents. Don't weaken this by assigning email beliefs too high a tier.
- **Signals never enter scoring** — CI-enforced if shared storage with beliefs. Only low-tier *beliefs* score.
- **LLM interprets, code computes** — responsiveness/latency derived deterministically from timestamps+threads, not the LLM (the renewal_date lesson). Reserve the LLM for genuinely interpretive outputs (sentiment, commitment, issue, and the belief-vs-signal judgment).
- **Corpus-diff / real-data acceptance** — the 338 emails are the test, not synthetic samples. Verify recall, grounding, AND belief-vs-signal routing on the real corpus (especially Scenario 07). Critically: confirm email beliefs don't wrongly override document beliefs and that conflicts surface (Part 2.2) on real data.
- **Determinism at the seam** — email interpretation is the soft edge (LLM, cassette-recorded); downstream is deterministic. Same architecture as document extraction.
- **Thread awareness is signal** — don't flatten conversations where the thread carries meaning.

---

## Part 8 — Acceptance criteria

E-signal is done when:

- The 338 `.eml` files are enumerated and parsed (MIME, encoded headers handled) — no longer invisible.
- A real email yields, where warranted, **low-tier beliefs** (explicit catalogued facts) that feed completeness/the vendor file, AND **signals** (commitments, issues, sentiment, responsiveness) — both grounded with evidence quotes, via a catalogue-driven interpretation pass reusing E1's pattern.
- **Belief-vs-signal routing is correct on real data** — explicit facts become low-tier beliefs; casual/hypothetical/negotiation mentions become signals, not beliefs. Verified on the real corpus (the precision discipline holds).
- **Cross-source conflict works, proven on real data** — where an email belief and a document belief disagree on a slot, the document (higher tier) wins the score and the conflict surfaces via contradiction detection; where they agree, corroboration; where the document is silent, the email grounds at low confidence. (Using the existing tier-then-recency + contradiction model — confirm it behaves correctly with correspondence-tier beliefs added.)
- **Signals are stored, queryable, and provably do NOT enter scoring** (the wall holds); only low-tier beliefs score.
- Thread-relative signals (responsiveness, reply sentiment) captured where the thread carries meaning; responsiveness derived deterministically.
- Scenario 07 (the 300-email relationship corpus) produces coherent relationship signal AND any warranted low-tier beliefs for its vendor(s).
- No regression: document extraction (E1), the click-path, golden 26/26, the metadata wall, the existing document beliefs' scores — all intact. Email intake is additive; adding correspondence-tier beliefs must not change any *document-driven* score.
- Determinism: interpretation is cassette-recorded; the same emails produce the same beliefs and signals.

---

## Appendix — Remaining detail-level questions (resolve during the build)

The three subsystem-shaping questions (correspondence tier, signal storage, email resolution) are **decided** — see §2.4. What remains is detail-level, safe to resolve during the build:

1. **Ingestion stage placement (§3.1):** enumerate `.eml` within the existing Stage 1 alongside PDFs, or a dedicated signal-ingestion stage? A dedicated stage keeps document and signal intake cleanly separate (likely cleaner given they're different patterns), while still reusing `DocumentCandidateExtractor`/`ClusteringStage` for the resolution step (§2.4 Decision 3). Detail, not subsystem-shaping.
2. **Responsiveness — the deterministic/LLM split (§3.2):** reply latency and cadence are derivable from timestamps + thread headers *deterministically*. Prefer deterministic derivation for what can be computed; reserve the LLM for genuinely interpretive outputs (sentiment, commitment, issue, and the belief-vs-signal judgment). The "LLM interprets, code computes" split from E1 — the exact per-signal boundary is a build detail.
3. **Signal-shape fit in `Km.Store.Metadata`:** §2.4 Decision 2 reuses `Km.Store.Metadata`; confirm during build that the `EmailSignal`/`RelationshipKnowledge` shape fits cleanly as a type within it. If it strains the shape, fall back to a distinct store **extending the same wall lane** (not a new lane). Low risk, worth a quick check at first write.
4. **Cost/cassette economics:** 338 emails × interpretation calls. Emails are short (likely 1-2 passes each), but 338 live calls to record is more than any prior corpus. Batch/scope the recording; consider whether all 338 need recording or a representative subset for dev.
5. **Signal-type vocabulary refinement:** the §3.2 set (responsiveness/sentiment/commitment/issue_raised/stakeholder_signal/request) is a starting set; refine against what the real Scenario 07 corpus actually contains.
