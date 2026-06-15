using Kozmo.Contracts;

namespace Kozmo.Api;

/// <summary>
/// Demo vendor IDs and the 10 seed signals from fixtures/signals.json.
/// Replay ingests these in order (received_at ascending) to reproduce the deterministic state.
/// </summary>
internal static class SeedData
{
    internal static readonly Guid CloudwaveId = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");
    internal static readonly Guid CorvusId    = Guid.Parse("eeeeeeee-0002-0000-0000-000000000001");
    internal static readonly Guid MeridianId  = Guid.Parse("eeeeeeee-0003-0000-0000-000000000001");

    internal static readonly Guid   CustomerId  = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    internal static readonly Guid[] VendorIds = [CloudwaveId, CorvusId, MeridianId];

    internal static readonly Signal[] AllSignals =
    [
        // 1 — Cloudwave: uptime 98.5% → Operational
        new Signal(
            Id:           Guid.Parse("11111111-0000-0000-0000-000000000001"),
            EntityId:     CloudwaveId,
            CustomerId:   CustomerId,
            SourceSystem: SourceSystem.MonitoringPlatform,
            ExternalId:   "mon-cw-2026-0514-001",
            Payload:      new Dictionary<string, object?> { ["uptime_pct"] = 98.5 },
            ObservedAt:   new DateTimeOffset(2026, 5, 14, 8,  0, 0, TimeSpan.Zero),
            ReceivedAt:   new DateTimeOffset(2026, 5, 14, 8,  5, 0, TimeSpan.Zero),
            TraceId:      Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001")),

        // 2 — Meridian: uptime 99.2% → Operational
        new Signal(
            Id:           Guid.Parse("11111111-0000-0000-0000-000000000002"),
            EntityId:     MeridianId,
            CustomerId:   CustomerId,
            SourceSystem: SourceSystem.MonitoringPlatform,
            ExternalId:   "mon-mer-2026-0516-001",
            Payload:      new Dictionary<string, object?> { ["uptime_pct"] = 99.2 },
            ObservedAt:   new DateTimeOffset(2026, 5, 16, 8,  0, 0, TimeSpan.Zero),
            ReceivedAt:   new DateTimeOffset(2026, 5, 16, 8,  5, 0, TimeSpan.Zero),
            TraceId:      Guid.Parse("a2a2a2a2-0000-0000-0000-000000000001")),

        // 3 — Cloudwave: adoption 35% → Experiential
        new Signal(
            Id:           Guid.Parse("11111111-0000-0000-0000-000000000003"),
            EntityId:     CloudwaveId,
            CustomerId:   CustomerId,
            SourceSystem: SourceSystem.UsageAnalytics,
            ExternalId:   "ua-cw-2026-0518-001",
            Payload:      new Dictionary<string, object?> { ["adoption_pct"] = 35.0 },
            ObservedAt:   new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero),
            ReceivedAt:   new DateTimeOffset(2026, 5, 18, 10, 15, 0, TimeSpan.Zero),
            TraceId:      Guid.Parse("a3a3a3a3-0000-0000-0000-000000000001")),

        // 4 — Meridian: 0 days overdue → Financial
        new Signal(
            Id:           Guid.Parse("11111111-0000-0000-0000-000000000004"),
            EntityId:     MeridianId,
            CustomerId:   CustomerId,
            SourceSystem: SourceSystem.BillingSystem,
            ExternalId:   "bill-mer-2026-0520-001",
            Payload:      new Dictionary<string, object?> { ["days_overdue"] = 0.0 },
            ObservedAt:   new DateTimeOffset(2026, 5, 20, 9,  0, 0, TimeSpan.Zero),
            ReceivedAt:   new DateTimeOffset(2026, 5, 20, 9,  2, 0, TimeSpan.Zero),
            TraceId:      Guid.Parse("a4a4a4a4-0000-0000-0000-000000000001")),

        // 5 — Corvus: uptime 96.5% → Operational
        new Signal(
            Id:           Guid.Parse("11111111-0000-0000-0000-000000000005"),
            EntityId:     CorvusId,
            CustomerId:   CustomerId,
            SourceSystem: SourceSystem.MonitoringPlatform,
            ExternalId:   "mon-cor-2026-0522-001",
            Payload:      new Dictionary<string, object?> { ["uptime_pct"] = 96.5 },
            ObservedAt:   new DateTimeOffset(2026, 5, 22, 8,  0, 0, TimeSpan.Zero),
            ReceivedAt:   new DateTimeOffset(2026, 5, 22, 8,  5, 0, TimeSpan.Zero),
            TraceId:      Guid.Parse("a5a5a5a5-0000-0000-0000-000000000001")),

        // 6 — Cloudwave: email, renewal_intent neutral → Strategic (Reported tier, alias resolution)
        new Signal(
            Id:           Guid.Parse("11111111-0000-0000-0000-000000000006"),
            EntityId:     CloudwaveId,
            CustomerId:   CustomerId,
            SourceSystem: SourceSystem.Email,
            ExternalId:   "email-2026-0524-cw-renewal",
            Payload:      new Dictionary<string, object?>
            {
                ["renewal_intent"] = "neutral",
                ["exec_sentiment"] = "concerned",
                ["entity_name"]    = "Cloudwave",
                ["subject"]        = "RE: upcoming contract renewal discussion",
                ["body_excerpt"]   = "We've been reviewing our vendor portfolio. Cloudwave has had some operational issues this quarter that we need to discuss before committing to renewal."
            },
            ObservedAt:   new DateTimeOffset(2026, 5, 24, 14, 30, 0, TimeSpan.Zero),
            ReceivedAt:   new DateTimeOffset(2026, 5, 24, 14, 35, 0, TimeSpan.Zero),
            TraceId:      Guid.Parse("a6a6a6a6-0000-0000-0000-000000000001")),

        // 7 — Corvus: CSAT 2.3/5 → Experiential
        new Signal(
            Id:           Guid.Parse("11111111-0000-0000-0000-000000000007"),
            EntityId:     CorvusId,
            CustomerId:   CustomerId,
            SourceSystem: SourceSystem.CRM,
            ExternalId:   "crm-cor-2026-0526-csat",
            Payload:      new Dictionary<string, object?> { ["csat_score"] = 2.3 },
            ObservedAt:   new DateTimeOffset(2026, 5, 26, 11, 0, 0, TimeSpan.Zero),
            ReceivedAt:   new DateTimeOffset(2026, 5, 26, 11, 10, 0, TimeSpan.Zero),
            TraceId:      Guid.Parse("a7a7a7a7-0000-0000-0000-000000000001")),

        // 8 — Cloudwave: 10 days overdue → Financial
        new Signal(
            Id:           Guid.Parse("11111111-0000-0000-0000-000000000008"),
            EntityId:     CloudwaveId,
            CustomerId:   CustomerId,
            SourceSystem: SourceSystem.BillingSystem,
            ExternalId:   "bill-cw-2026-0528-overdue",
            Payload:      new Dictionary<string, object?> { ["days_overdue"] = 10.0 },
            ObservedAt:   new DateTimeOffset(2026, 5, 28, 9,  0, 0, TimeSpan.Zero),
            ReceivedAt:   new DateTimeOffset(2026, 5, 28, 9,  2, 0, TimeSpan.Zero),
            TraceId:      Guid.Parse("a8a8a8a8-0000-0000-0000-000000000001")),

        // 9 — Corvus: $52,000 overdue → Financial (Critical indicator)
        new Signal(
            Id:           Guid.Parse("11111111-0000-0000-0000-000000000009"),
            EntityId:     CorvusId,
            CustomerId:   CustomerId,
            SourceSystem: SourceSystem.BillingSystem,
            ExternalId:   "bill-cor-2026-0530-invoice-047",
            Payload:      new Dictionary<string, object?>
            {
                ["overdue_amount_usd"]           = 52000.0,
                ["invoice_number"]               = "INV-CORVUS-2026-047",
                ["invoice_date"]                 = "2026-04-30",
                ["due_date"]                     = "2026-05-15",
                ["contract_value_monthly_usd"]   = 70000.0,
                ["outstanding_invoices"]         = 1.0
            },
            ObservedAt:   new DateTimeOffset(2026, 5, 30, 10, 0, 0, TimeSpan.Zero),
            ReceivedAt:   new DateTimeOffset(2026, 5, 30, 10, 5, 0, TimeSpan.Zero),
            TraceId:      Guid.Parse("a9a9a9a9-0000-0000-0000-000000000001")),

        // 10 — Corvus: roadmap fit 0.30 → Strategic
        new Signal(
            Id:           Guid.Parse("11111111-0000-0000-0000-000000000010"),
            EntityId:     CorvusId,
            CustomerId:   CustomerId,
            SourceSystem: SourceSystem.CRM,
            ExternalId:   "crm-cor-2026-0601-roadmap",
            Payload:      new Dictionary<string, object?> { ["roadmap_fit_score"] = 0.30 },
            ObservedAt:   new DateTimeOffset(2026, 6,  1,  9, 0, 0, TimeSpan.Zero),
            ReceivedAt:   new DateTimeOffset(2026, 6,  1,  9, 15, 0, TimeSpan.Zero),
            TraceId:      Guid.Parse("b0b0b0b0-0000-0000-0000-000000000001")),
    ];
}
