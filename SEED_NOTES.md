# Seed Notes — Demo Vendor Answer Key

This document records the intended scoring for the two vendors added in the Phase 6
seed-data extension. Use it to verify that the pipeline produces the expected output
after replaying `fixtures/signals.json`.

---

## Northwind Systems (`eeeeeeee-0005-0000-0000-000000000001`)

### Signals submitted (in chronological order)

| ID suffix | Observed     | Source             | Criterion         | Raw value | Rubric score |
|-----------|--------------|--------------------|-------------------|-----------|--------------|
| N1        | 2026-05-20   | MonitoringPlatform | uptime_pct        | 99.7      | 1.00         |
| N2        | 2026-05-21   | CRM                | csat_score        | 4.7       | 1.00         |
| N3        | 2026-05-22   | BillingSystem      | days_overdue      | 0         | 1.00         |
| N4        | 2026-05-23   | CRM                | roadmap_fit_score | 0.85      | 1.00         |

### Intended per-dimension belief scores

| Dimension      | Criterion         | Belief value |
|----------------|-------------------|--------------|
| Operational    | uptime_pct        | 1.00         |
| Experiential   | csat_score        | 1.00         |
| Financial      | payment_timeliness| 1.00         |
| Strategic      | roadmap_alignment | 1.00         |

### Composite and outcome

- Composite = 0.25×1.00 + 0.25×1.00 + 0.25×1.00 + 0.25×1.00 = **1.00**
- Band: **Healthy** (≥ 0.65)
- Trajectory: stable (all signals high, single data point per dimension — flat)
- Posture: **Maintain**
- Contradictions: **none**
- Gaps: **none** (all four dimensions covered)

---

## Ridgeline Software (`eeeeeeee-0006-0000-0000-000000000001`)

### Signals submitted (in chronological order)

| ID suffix | Observed     | Source             | Criterion          | Raw value | Rubric score | Notes                          |
|-----------|--------------|--------------------|--------------------|-----------|--------------|--------------------------------|
| R1        | 2026-05-25   | MonitoringPlatform | uptime_pct         | 98.3      | 0.45         |                                |
| R2        | 2026-05-27   | CRM                | roadmap_fit_score  | 0.80      | 1.00         |                                |
| R3a       | 2026-05-29   | BillingSystem      | payment_timeliness | 0 days    | 1.00         | baseline — superseded by R3b   |
| R3b       | 2026-06-01   | BillingSystem      | payment_timeliness | 25 days   | 0.30         | supersedes R3a; fires contradiction |

### Intended per-dimension belief scores (current, after supersession)

| Dimension      | Criterion          | Belief value |
|----------------|--------------------|--------------|
| Operational    | uptime_pct         | 0.45         |
| Experiential   | *(no signal)*      | —            |
| Financial      | payment_timeliness | 0.30         |
| Strategic      | roadmap_alignment  | 1.00         |

### Composite trajectory

After each signal the running composite is:

| After signal | Op   | Exp  | Fin  | Strat | Composite | Band   |
|--------------|------|------|------|-------|-----------|--------|
| R1           | 0.45 | —    | —    | —     | 0.1125    | —      |
| R2           | 0.45 | —    | —    | 1.00  | 0.3625    | —      |
| R3a          | 0.45 | —    | 1.00 | 1.00  | 0.6125    | Healthy|
| R3b          | 0.45 | —    | 0.30 | 1.00  | **0.4375**| AtRisk |

*(Missing dimension contributes 0.0 to the weighted sum.)*

### Outcome

- Composite = 0.25×0.45 + 0.25×0.00 + 0.25×0.30 + 0.25×1.00 = **0.4375**
- Band: **AtRisk** (≥ 0.40 and < 0.65)
- Trajectory: **Declining** (composite fell from 0.6125 → 0.4375 at the last signal step)
- Posture: **Renegotiate** (AtRisk + Declining → Renegotiate per `postures.saas.v1.json`)

### Contradiction

- Criterion: `payment_timeliness` (Financial dimension)
- Superseded belief value: **1.00** (R3a, 2026-05-29)
- Current belief value: **0.30** (R3b, 2026-06-01)
- Delta: |1.00 − 0.30| = **0.70** ≥ 0.30 threshold → contradiction fires

### Gap

- Dimension: **Experiential**
- Reason: no signal ever submitted for a `csat_score` or equivalent Experiential criterion
- Detection: `ComputeMeta` gap detector flags any `Dimension` enum value with no current belief

---

## MCP Registry Limitation

The signals are keyed on fixed GUIDs. `IiFacade.SubmitSignalAsync` processes any
entity_id regardless of registry membership, so the scoring pipeline runs correctly.

However, `VendorQueryService.AnswerAsync` resolves a vendor name to a GUID via
`EntityRegistry`. The registry is populated at startup from two sources:

1. `BuildRegistry()` — hardcoded entries for the original four vendors
2. `LoadPersistedVendorsAsync()` — reads the `vendors` table (rows written only by
   `/vendors/resolve-name` or KYV run endpoints)

Northwind Systems and Ridgeline Software are in neither source, so the MCP cannot
resolve them by name. Two lines must be added to `BuildRegistry()` in
**both** `Kozmo.Mcp/Program.cs` and `Kozmo.Api/Program.cs`:

```csharp
reg.Register(Guid.Parse("eeeeeeee-0005-0000-0000-000000000001"), "Northwind Systems");
reg.Register(Guid.Parse("eeeeeeee-0006-0000-0000-000000000001"), "Ridgeline Software");
```

This is an infrastructure wiring change (not scoring logic). Once added, the six
verification queries below should produce the expected text.

---

## Six Verification Queries (MCP tools)

| # | Tool                    | Input                               | Expected output summary                              |
|---|-------------------------|-------------------------------------|------------------------------------------------------|
| 1 | `vendor_overview`       | Northwind Systems                   | Healthy / Maintain, all four dimensions strong       |
| 2 | `vendor_overview`       | Ridgeline Software                  | AtRisk / Renegotiate, declining composite            |
| 3 | `vendor_dimension_detail` | Ridgeline Software, Financial     | Weak (0.30), cites payment delay contradiction       |
| 4 | `vendor_dimension_detail` | Ridgeline Software, Experiential  | Gap — no evidence collected for this dimension       |
| 5 | `vendor_open_questions` | Ridgeline Software                  | Flags Experiential gap + Financial contradiction     |
| 6 | `vendor_open_questions` | Northwind Systems                   | No open questions (all dimensions covered, no gaps)  |

---

## Files Changed (seed-data extension only)

- `fixtures/signals.json` — 8 new signal objects appended (signals N1–N4, R1–R2, R3a–R3b)
- `SEED_NOTES.md` — this file (documentation only)

No `.cs`, catalogue config, or test file was modified as part of the seed-data task.
