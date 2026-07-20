# Northstar Software — Scenario

## Overview

Northstar Software is a stable, two-year SaaS vendor approaching a commercially sensitive renewal
window. Three active pressure points converge on a single 75-day deadline, making it an ideal
subject for the Phase 7 briefing engine.

| Field            | Value                                |
|------------------|--------------------------------------|
| Vendor ID        | `dd000001-0000-0000-0000-000000000001` |
| Domain           | `northstarsoftware.com`              |
| Canonical name   | Northstar Software                   |
| Aliases          | Northstar, NStar                     |
| Current ACV      | £285,000                             |
| Renewal date     | 2026-09-28                           |
| Notice deadline  | 2026-07-29 (60 days before renewal)  |

## Three Commercial Pressure Points

### 1 — Pricing uplift (7%)

Vendor proposed a 7% increase for the 2026/27 term (£285,000 → £304,950) in a Jul 8 email.
The previous renewal (2025 → 2026) was 5.6%. A counter-position (targeting a 3% cap) is being
prepared internally.

**Evidence**: `dd000001-0005` (vendor email, Correspondence) + `dd000001-0007` (commitment note).

### 2 — Outstanding Q2 SLA report

The Q2 2026 SLA performance report was due 30 June per §10.4. It has not been received.
A formal request was sent on 10 July; vendor has promised delivery by 18 July. The report
is a prerequisite for renewal sign-off.

**Evidence**: `dd000001-0006` (commitment note — no claim extracted; Phase 7 gap).

### 3 — 75-day renewal window / notice deadline

The contract renews automatically on 28 September 2026. The 60-day non-renewal notice must
be served by **29 July 2026** — 14 days from the seeding date. Missing this deadline triggers
auto-renewal at the vendor's proposed price unless a signed amendment is in place beforehand.

**Evidence**: `dd000001-0008` (commitment note — notice deadline alert).

## Seeded Evidence

| ID       | DocType        | Tier          | Observed at  | Claims extracted                                 |
|----------|----------------|---------------|--------------|--------------------------------------------------|
| 0001     | SignedContract | Primary       | 2026-01-15   | annual_value, renewal_date, notice_period, auto_renewal, sla_uptime, payment_terms, contract_on_file |
| 0002     | SignedContract | Primary       | 2025-01-15   | annual_value (270k), renewal_date (2026-01-15), notice_period, auto_renewal (0) |
| 0003     | OwnerNote      | Reported      | 2026-04-10   | sla_uptime (0.99 — verbal confirmation in meeting) |
| 0004     | OwnerNote      | Reported      | 2026-06-30   | renewal_intent (0.65 — positive but price-dependent) |
| 0005     | Email          | Correspondence| 2026-07-08   | renewal_intent (0.55 — cautious after pricing proposal) |
| 0006     | OwnerNote      | Reported      | 2026-07-10   | (none — commitment: SLA report outstanding)      |
| 0007     | OwnerNote      | Reported      | 2026-07-12   | (none — commitment: counter-proposal in draft)   |
| 0008     | OwnerNote      | Reported      | 2026-07-14   | (none — commitment: notice deadline 2026-07-29)  |

## Known Gaps (Phase 7)

The following commercial signals have no catalogue key and therefore produce no belief:

- `pricing_uplift_pct` — percentage uplift proposed by vendor (7%)
- `sla_report_overdue` — boolean flag for missing Q2 report
- `counter_proposal_status` — internal negotiation status
- `notice_deadline` — calendar date of the 60-day non-renewal cut-off

Add these to `catalogue/profiles/saas/claim_key_catalogue.saas.v1.json` in Phase 7 to enable
structured extraction and scoring.

## Email Fixtures

18 email records spanning 2024-06-15 to 2026-07-14 are in `fixtures/northstar_emails.json`.
They cover the full commercial arc: initial engagement → contract 2025 → SLA incident (Aug 2025)
→ renewal 2026 → pricing uplift dispute.

## How to Seed

```bash
# 1. Run the seeder (requires MicrosoftGraph user secrets to be configured)
dotnet run --project tools/GraphAuthHarness -- seed-northstar

# 2. Start the API (if not already running)
dotnet run --project host/dotnet/Kozmo.Api

# 3. Ingest the evidence fixture (absolute path required)
curl -X POST http://localhost:5000/vendors/dd000001-0000-0000-0000-000000000001/vendor-file/ingest \
     -H "Content-Type: application/json" \
     -d '{"fixturePath":"<absolute-path>/fixtures/vendor-file/northstar.evidence.json"}'

# 4. Verify
curl http://localhost:5000/vendors/dd000001-0000-0000-0000-000000000001/vendor-file
curl http://localhost:5000/vendors/dd000001-0000-0000-0000-000000000001/vendor-file/markdown
```

## Idempotency

The seeder checks for an existing vendor by ID and domain before writing. Running it twice is safe:
- Identity registry: `SaveAsync` upserts by VendorId (INSERT OR REPLACE).
- Vendors table: `SaveVendorAsync` uses INSERT OR REPLACE.
- Calendar event: a new event is created each run (deduplicate manually in Outlook if needed).
- Evidence ingest: the evidence fixture uses fixed GUIDs — re-running the ingest endpoint will
  re-append evidence rows and may create duplicate beliefs. Clear the DB or use fresh GUIDs for
  a clean re-seed.
