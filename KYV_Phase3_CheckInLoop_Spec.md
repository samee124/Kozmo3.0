# KYV — Phase 3 Frozen Spec: Check-In Loop (Workflow & Coordination)

**Why this phase exists.** Phases 1–2 produce TRIAGE cases and dimension GAPS, each already
carrying a formed question (the seam built deliberately in Phase 1 §1.3 / §5). Phase 3 turns
those questions into an actual human round-trip: ask the owner, wait, take the answer back in
as evidence, and recompute the affected vendor. This is the "asks for what it's missing" half
of the KYV story.

**What is genuinely new and different.** This is the FIRST asynchronous, long-running,
human-in-the-loop part of the system. Everything before was synchronous (evidence in →
beliefs out, one pass). Here the program PAUSES on a vendor, waits possibly days, and resumes
when a human answers. The vendor file is no longer produced in one deterministic pass — it is
built incrementally as evidence (including human evidence) arrives. Frame it honestly:
**deterministic processing, asynchronous gathering.**

**Decisions locked (from discussion):**
- **Owner:** single fixed program owner for the demo (all check-ins go to one pending view).
  Per-vendor ownership is a later refinement.
- **Transport:** SIMULATED in-app round-trip now (a "pending check-ins" view where the owner
  reads the question and types/selects the answer), behind a transport interface so REAL EMAIL
  is a later swap — same pattern as the drive connector. The loop does not know which transport.
- **Response type:** STRUCTURED responses are the Phase 3 core (deterministic). FREE-TEXT
  (LLM reads prose → structured) is a designed-for LATER soft-edge addition, slotted in front
  of the same loop — NOT built in this phase.

**Scope OUT:** real email, free-text response reading, per-vendor owners, the action loop /
ledger (a check-in is NOT an action — see §6), the connector, enrichment.

---

## 0. Altitude and the reuse/new boundary

- **Subsystem:** Workflow & Coordination — promoted specifically for human/multi-party steps.
  This is its FIRST real capability.
- **Module:** Check-In Loop — NEW module. Distinct responsibility (manage the async human
  round-trip) nothing else owns.
- **Stages:** `raise_checkins` (emit requests from triage/gaps) and `process_response`
  (take an answer back in). Note `process_response` runs ASYNCHRONOUSLY, not in the main pass.
- **Cross-subsystem handoff:** I&I detects the triage/gap and forms the question (built) →
  W&C owns the request, pending-state, transport, and matching (this phase) → I&I processes
  the response as evidence (reuses belief→reconcile→judge→recompute, built).

**Reuse / new:**
| Concern | REUSE (do not rebuild) | NEW (this module adds) |
|---|---|---|
| The formed question (triage_reason, triage_question) | Phase 1 §1.3 disposition + gap output | — |
| Response → belief (tiered, cited) | AppendBelief + ConfidenceClamper (built) | — |
| Recompute one vendor after new evidence | RecomputeVendorAsync (built, read-only per vendor) | — |
| Persisting records | Km.Store write infrastructure (built) | the check-in tables |
| Durable pending-state machine | — | open→answered→expired lifecycle |
| Transport (ask out / answer in) | — | ICheckInTransport (simulated now / email later) |
| Matching a response to its request | — | round-trip ID + the wrong-match guard |

---

## 1. The check-in record (durable)

```
checkin_id        # unambiguous round-trip ID — the response carries this back
vendor_id         # the affected vendor (or the triage entity)
program_run_id    # which run raised it
kind              # IDENTITY_CONFIRM | DIMENSION_GAP
question          # the formed, human-readable question (from Phase 1/2 — not re-derived)
response_shape    # YES_NO (identity) | TYPED_VALUE (gap) | STATUS_SELECT (gap)
                  #   — structured only this phase
target_field      # for a gap: which belief/field the answer fills (e.g. renewal_date, contract_ref)
owner             # the fixed program owner (this phase)
status            # OPEN | ANSWERED | EXPIRED
raised_at / answered_at / expires_at
response_value    # null until answered; the structured answer when answered
```

Source: Phase 1 TRIAGE dispositions (kind=IDENTITY_CONFIRM, e.g. the ABC pair) and gap
detection (kind=DIMENSION_GAP, e.g. Regulus "spend, no contract"). The question is COPIED
from the disposition/gap, never re-derived.

## 2. The pending-state lifecycle (durable, the new state machine)

- **OPEN** — raised, awaiting the owner. The vendor file renders with this gap/triage flagged
  "pending check-in" — the program does NOT block; other vendors proceed.
- **ANSWERED** — owner responded; response_value set; triggers §4 processing.
- **EXPIRED** — optional timeout (owner never answers). For the demo, expiry can be a no-op
  status; the point is the state machine supports it.
Persisted (survives restart): a half-finished run must remember its open check-ins.

## 3. Transport interface (simulated now, email later)

```
interface ICheckInTransport {
    Task SendAsync(CheckIn checkin);                 // ask the owner
    // responses arrive via the app (simulated) or an inbox (email, later)
}
```
- **Simulated (this phase):** `SendAsync` surfaces the check-in in an in-app "pending" list;
  the owner reads the question and submits a structured response in-app, carrying checkin_id.
- **Email (later):** same interface, real send + inbound matching. NOT built now.
The loop and processing are identical regardless of transport.

## 4. Response → evidence → recompute (the core path, deterministic)

When a structured response arrives:
1. **Match by checkin_id** to the OPEN check-in — see §5 guard.
2. **Convert the structured answer to a belief / action:**
   - IDENTITY_CONFIRM YES → merge the two triage entities into one canonical vendor (record
     the alias + provenance = this check-in); NO → keep separate, lift both from TRIAGE to
     CONFIRMED. (Reuses the registry write path; the human answer is the corroborating signal
     the system lacked.)
   - DIMENSION_GAP TYPED_VALUE / STATUS_SELECT → append a belief for target_field, tier =
     REPORTED (human-supplied) or VERIFIED if the owner is authoritative; cited to the
     check-in; clamped via ConfidenceClamper.
3. **Re-enter the pipeline:** reconcile → judge → RecomputeVendorAsync for that ONE vendor.
4. **Re-render:** the vendor file updates — gap filled / identity resolved, completeness up,
   status/posture may firm. Check-in → ANSWERED.

## 5. The wrong-match guard (the wrong-merge-class risk — critical)

A response landing on the WRONG check-in silently corrupts a vendor — same failure class as
identity wrong-merge. Therefore:
- A response MUST carry a valid checkin_id matching an OPEN check-in; no fuzzy/positional
  matching. Unmatched or already-ANSWERED → reject, do not apply.
- The applied effect touches ONLY the vendor named in that check-in record.
- An IDENTITY_CONFIRM answer applies ONLY to the specific pair named in that check-in — never
  to a different similar pair.
- Test this explicitly (a response for check-in A must never affect vendor B).

## 6. The check-in ≠ action boundary (hold this line)

A check-in is INFORMATION-GATHERING, not a consequential vendor action. "Ask the owner for
the renewal date" or "are these the same vendor" gathers data — no grounds, no prediction, no
ledger entry. It is ACTIVITY (Part of the activity log), not a decision. Do NOT let Phase 3
drift into the action loop (e.g. "email the vendor to renegotiate") — that is a future Act-loop
capability with grounds + prediction + ledger. Keep check-ins clearly separate so the (empty)
ledger stays meaningful.

## 7. Test contract — the real run's check-in cases (definition of done)

From the validated Scenario 01–04 run:
1. **ABC identity check-in.** kind=IDENTITY_CONFIRM, question names both ("Are 'ABC Tech Inc.'
   and 'ABC Technologies LLC' the same vendor?"), response_shape=YES_NO.
   - Answer YES → the two TRIAGE entries merge into ONE confirmed vendor, alias recorded,
     vendor recomputed and re-rendered.
   - Answer NO → both lift from TRIAGE to CONFIRMED, two separate vendors.
2. **Regulus gap check-in.** kind=DIMENSION_GAP ("spend present, no signed contract — provide
   or confirm none"), response_shape=TYPED_VALUE/STATUS_SELECT, target_field=contract_ref.
   - Answer → belief appended (REPORTED), Regulus recomputed, completeness up.
3. **Aequitas gap check-in.** kind=DIMENSION_GAP ("no executed agreement, draft only — confirm
   status"), STATUS_SELECT. Answer → status belief appended, recomputed.
4. **Pending render.** Before answering, each affected vendor renders with the item flagged
   "pending check-in" and the program does NOT block (other vendors complete).
5. **Wrong-match guard.** A response for the ABC check-in must not affect Regulus or any other
   vendor; a response with an unknown/closed checkin_id is rejected.

Tests:
- `CheckIn_AbcIdentity_AnswerYes_MergesToOneVendor`
- `CheckIn_AbcIdentity_AnswerNo_TwoConfirmedVendors`
- `CheckIn_RegulusGap_AnswerValue_AppendsBelief_RecomputesVendor`
- `CheckIn_PendingState_VendorRendersPending_ProgramDoesNotBlock`
- `CheckIn_WrongMatchGuard_ResponseAppliesOnlyToOwnCheckin`
- `CheckIn_UnknownOrClosedId_Rejected`
- `CheckIn_ResponseBecomesBelief_TieredAndCited`

## 8. Done when
- The three real check-ins raise with their formed questions and structured shapes.
- A structured response matches by ID, becomes a tiered/cited belief (or identity merge/keep),
  recomputes ONLY the affected vendor, and re-renders it.
- Pending state is durable; vendors render "pending" without blocking the run.
- The wrong-match guard holds (response affects only its own check-in's vendor).
- Transport is behind ICheckInTransport (simulated impl now; email is a later swap).
- check-in ≠ action boundary intact (no ledger entries, no vendor actions).
- Existing tests green (exact count before/after); Phases 1–2 and their suites UNCHANGED.

## 9. The hard checkpoint
**A response only ever affects its own check-in's vendor.** The wrong-match guard is the
Phase 3 equivalent of "no wrong merge" — a misrouted human answer silently corrupting a
vendor is the cardinal failure. Confirm on the test cases that a response for one check-in
cannot touch another vendor, and that unknown/closed IDs are rejected, before declaring done.

## 10. Build order (commits)
1. **Commit 1 — the check-in record + pending-state lifecycle + raise_checkins stage**
   (durable, persisted; raise the three real cases; render "pending"; no transport/response
   yet). Testable without transport.
2. **Commit 2 — ICheckInTransport (simulated) + the in-app pending view** (surface OPEN
   check-ins; submit a structured response carrying checkin_id). The round-trip plumbing.
3. **Commit 3 — process_response: match (with the §5 guard) → belief/merge → recompute →
   re-render**, with the §7 test contract and the §9 wrong-match checkpoint. Closes Phase 3.
