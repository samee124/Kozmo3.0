using Kozmo.Contracts;

namespace Ii.Completeness.Tests;

/// <summary>
/// Authored fixture belief sets for the two-sided completeness test:
///   IIVS  — rich evidence across all four dimensions → high coverage at L1.
///   Regulus — sparse evidence (Financial only) → gaps in Op/Exp/Str at L1.
///
/// SYNC CONTRACT: the recorder tool (tools/Kozmo.CompletenessRecorder/Program.cs) defines
/// the SAME beliefs in its own FixtureBeliefs class. Both must stay in sync — a divergence
/// in any field value changes the AnsweringPrompt.User string → cassette key mismatch →
/// LlmCacheMissException → obvious test failure that signals re-recording is needed.
/// </summary>
internal static class FixtureBeliefs
{
    // ── Shared anchor ───────────────────────────────────────────────────────
    private static readonly DateTimeOffset AnchorDate =
        new(2024, 11, 15, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid Trace1 = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // ── IIVS (rich vendor) ──────────────────────────────────────────────────

    public static readonly Guid IivsVendorId =
        Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6");

    public static IReadOnlyList<Belief> Iivs =>
    [
        MakeBelief("b1000001-0000-0000-0000-000000000001", IivsVendorId,
            Dimension.Operational, "uptime_sla_exists",
            0.85, SourceTier.Primary, 0.90,
            "MSA Section 3.1 specifies 99.9% uptime SLA with measurement period of calendar month"),

        MakeBelief("b1000002-0000-0000-0000-000000000002", IivsVendorId,
            Dimension.Operational, "uptime_sla_percentage",
            0.85, SourceTier.Primary, 0.90,
            "Contracted uptime SLA is 99.9% as specified in the executed Master Services Agreement"),

        MakeBelief("b1000003-0000-0000-0000-000000000003", IivsVendorId,
            Dimension.Experiential, "sla_met_last_12_months",
            0.80, SourceTier.Verified, 0.82,
            "Monitoring platform reports 99.95% uptime over the past 12 months, exceeding the 99.9% SLA"),

        MakeBelief("b1000004-0000-0000-0000-000000000004", IivsVendorId,
            Dimension.Experiential, "csat_score",
            0.75, SourceTier.Inferred, 0.70,
            "CSAT score of 4.2 out of 5.0 recorded in Q3 2024 survey across 12 respondents"),

        MakeBelief("b1000005-0000-0000-0000-000000000005", IivsVendorId,
            Dimension.Financial, "signed_contract_with_payment_terms",
            0.90, SourceTier.Primary, 0.95,
            "Master Services Agreement signed 2022-03-15 with net-30 payment terms and auto-renewal clause"),

        MakeBelief("b1000006-0000-0000-0000-000000000006", IivsVendorId,
            Dimension.Financial, "annual_contract_value",
            0.80, SourceTier.Primary, 0.95,
            "Annual contract value is $285,000 USD as specified in executed Order Form OF-2022-003"),

        MakeBelief("b1000007-0000-0000-0000-000000000007", IivsVendorId,
            Dimension.Strategic, "roadmap_alignment",
            0.70, SourceTier.Reported, 0.60,
            "VP Engineering confirmed vendor roadmap aligns with platform modernisation initiative in Q4 2024 business review"),

        MakeBelief("b1000008-0000-0000-0000-000000000008", IivsVendorId,
            Dimension.Strategic, "renewal_date",
            0.80, SourceTier.Primary, 0.90,
            "Contract renewal date is 2025-03-14 as specified in MSA Section 12.2"),
    ];

    // ── Regulus (sparse vendor — Financial only) ────────────────────────────

    public static readonly Guid RegulusVendorId =
        Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    public static IReadOnlyList<Belief> Regulus =>
    [
        MakeBelief("b2000001-0000-0000-0000-000000000001", RegulusVendorId,
            Dimension.Financial, "signed_contract_with_payment_terms",
            0.80, SourceTier.Primary, 0.88,
            "Purchase Order PO-2023-047 signed with net-60 payment terms; total value $42,000"),

        MakeBelief("b2000002-0000-0000-0000-000000000002", RegulusVendorId,
            Dimension.Financial, "annual_contract_value",
            0.65, SourceTier.Inferred, 0.72,
            "Estimated annual spend of $42,000 inferred from single executed purchase order"),
    ];

    // ── Builder ─────────────────────────────────────────────────────────────

    private static Belief MakeBelief(
        string     idStr,
        Guid       entityId,
        Dimension  dimension,
        string     criterion,
        double     value,
        SourceTier tier,
        double     confidence,
        string     derivation) =>
        new(
            Id:            Guid.Parse(idStr),
            EntityId:      entityId,
            Dimension:     dimension,
            Criterion:     criterion,
            Value:         value,
            SourceTier:    tier,
            Confidence:    confidence,
            Freshness:     1.0,
            Derivation:    derivation,
            SourceSignals: [],
            Version:       1,
            SupersededBy:  null,
            CreatedAt:     AnchorDate,
            TraceId:       Trace1);
}
