# DIAGNOSIS PROMPT — (c) The Namespace Unification

## What this is
The recurring root disease across this whole project is namespace fragmentation: facts about vendors
are labeled in multiple non-aligned naming systems, so storing, scoring, completeness/gap-discovery,
and check-ins don't line up. This has caused the scoring↔completeness disconnect, the E2.2c
"nothing to bind" result, the broken gap-discovery, and the "everything shows as a gap" finding — all
the same root.

This prompt is **DIAGNOSIS ONLY. Change absolutely nothing.** The goal is a complete, authoritative map
of the fragmentation and a proposed unification design + migration path — so a human can decide the
target state before any code moves. This is the highest-risk change on the roadmap (it touches scoring
for every vendor); the diagnosis must be exhaustive and the fix must NOT be attempted in this prompt.

## The known namespaces (verify and complete this list)
- **ClaimKey** — e.g. `sla_uptime`. Written by the vendor-file/document/check-in path. Read by
  CompletenessService (gap discovery).
- **Criterion** — e.g. `uptime_sla`. Written by the signal pipeline (ObservationModule via
  classification.saas.v1.json). Read by the scoring rubric (RubricModule).
- **Question.Id** — e.g. `saas.op.l1.2`. Keys check-ins (CheckIn.TargetField) and the question bank.
- **Expected-set keys** — SaasProfile.ExpectedBeliefSets — which namespace are these in?
- (Find any others — e.g. metadata field names, extraction-schema keys, rubric_criterion links.)

## PART A — MAP EVERY NAMESPACE AND EVERY READ/WRITE SITE
For each namespace above (and any others found), report exhaustively (quote code + file:line):
1. **Definition & domain:** what are its values, where is the canonical list authored (which config
   file / enum / class)?
2. **Every WRITE site:** every place a Belief (or equivalent) gets this field populated. Critically:
   - Confirm the signal pipeline writes `Criterion` but leaves `ClaimKey` empty (Belief.cs doc-comment
     + ObservationModule). Quote it.
   - Confirm the vendor-file/check-in path writes `ClaimKey`. Quote it.
   - Does anything write BOTH? Does anything map between them at write time?
3. **Every READ site:** every place that reads this field to make a decision (scoring, completeness,
   check-in raising, UI endpoints like /vendors/{id}/questions, band lookup, etc.). Quote each.
4. **The cross-namespace links that already exist:** e.g. claim_key_catalogue's `rubric_criterion`
   field (ClaimKey→Criterion), Question.TargetClaimKey (Question.Id→ClaimKey), the
   RubricCriterion ?? claimKey fallback. Map every existing bridge and note which are load-bearing.

## PART B — THE MISMATCH MATRIX
Produce a single table: for each of the ~16 claim keys / criteria, show its value in EVERY namespace
(ClaimKey name, Criterion name, whether a Question is bound, whether it's in the expected-set, whether
it has rubric bands, what the signal pipeline actually writes for it). This is the definitive picture
of where the namespaces align and where they diverge. Mark each row: ALIGNED (same effective identity
across all namespaces) or DIVERGENT (and how).

Specifically answer:
- For the seeded demo vendors (Cloudwave/Corvus/Meridian/Helix): their beliefs carry `Criterion` (e.g.
  `uptime_sla`) but empty `ClaimKey`. So which of the 12 expected claim keys do they actually satisfy
  if we matched on the RIGHT field? I.e., if CompletenessService matched Criterion↔claim_key (via the
  catalogue's rubric_criterion mapping) instead of ClaimKey, how many gaps would genuinely close for a
  seeded vendor? Show the real number.
- Is the core problem that beliefs SHOULD carry both ClaimKey and Criterion (and the signal pipeline is
  just failing to populate ClaimKey), OR that there should be ONE identifier and the second namespace
  is redundant? Assess both framings.

## PART C — UNIFICATION DESIGN OPTIONS (describe, do NOT implement)
Lay out the realistic target-state designs, each with what it touches, its migration path, and its
blast radius (this touches scoring for every vendor — blast radius assessment is mandatory):

- **Option 1 — One canonical identifier.** Collapse to a single namespace (e.g. everything keys on
  claim_key; Criterion becomes an alias/derived). What has to change at every write site (esp. make the
  signal pipeline write claim_key), every read site, every config file. What breaks. Migration for
  existing beliefs.
- **Option 2 — Keep both, guarantee the mapping.** Beliefs always carry both ClaimKey and Criterion,
  populated via the catalogue's existing rubric_criterion mapping at every write site (fix the signal
  pipeline to also set ClaimKey). Every read site keeps working; the fields just always agree. Less
  invasive? Assess.
- **Option 3 — Match on Criterion, not ClaimKey.** Leave writes as-is; make the consumers that
  currently read ClaimKey (esp. CompletenessService) instead match via Criterion↔claim_key through the
  catalogue mapping. Smallest change? What are the risks (the fallback fragility, name-coincidence
  keys)?
- For EACH: does it preserve the deterministic/glass-box guarantee (no new LLM, byte-stable
  fingerprints)? Does it require a data migration of existing beliefs, or is it forward-only? Which CI
  invariants does it touch?

## PART D — RECOMMENDATION + SEQUENCING
- Recommend the target design (which option), with reasoning grounded in the map, the blast radius, and
  the glass-box doctrine.
- Sequence it into contained, individually-provable sub-steps (like E2 was sub-stepped) — this is too
  big for one change. Each sub-step must be independently verifiable with zero behavior change until
  the final switch, or an explicit, tested behavior change.
- State explicitly what this unification, once done, UNBLOCKS: gap-discovery actually working, the
  cassette fix becoming trivial (or moot), E2.2c bindings becoming meaningful, more dimensions
  scoring. Tie it back to the momentum-workspace vision.
- Identify what MUST be decided by a human before implementation begins (the interpretive calls).

Report A, B, C, D. **Change nothing.** The bar: an exhaustive, authoritative map of the namespace
fragmentation, the real "how many gaps would close if matched correctly" number for a seeded vendor,
the honest menu of unification designs with blast radius, and a sequenced plan — so a human can decide
the target state before any code touches scoring. Do NOT implement any fix.
```
