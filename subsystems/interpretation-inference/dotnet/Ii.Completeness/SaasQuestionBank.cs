using Kozmo.Contracts;

namespace Ii.Completeness;

/// <summary>
/// Authored question bank for the SaaS/services vendor category. Version saas.v1.
/// Questions are doctrine — authored offline, never LLM-generated. Two questions per
/// dimension per depth level (6 per dimension, 24 total). The intelligence is in
/// answering them well, not in having hundreds.
/// </summary>
public static class SaasQuestionBank
{
    public const string Category = "saas";
    public const string Version  = "saas.v1";

    public static IReadOnlyList<Question> All { get; } = BuildAll();

    private static IReadOnlyList<Question> BuildAll() =>
    [
        // ── Operational ──────────────────────────────────────────────────────
        // L1: baseline — every SaaS vendor
        new("saas.op.l1.1", Dimension.Operational, "Does the vendor have a documented uptime SLA?",
            AnswerType.YesNo,      DepthLevel.L1, 0.60),
        new("saas.op.l1.2", Dimension.Operational, "What is the vendor's contracted uptime SLA percentage?",
            AnswerType.TypedValue, DepthLevel.L1, 0.60),
        // L2: standard
        new("saas.op.l2.1", Dimension.Operational, "Does the vendor have a documented incident response procedure?",
            AnswerType.YesNo,      DepthLevel.L2, 0.65),
        new("saas.op.l2.2", Dimension.Operational, "What is the average incident response time specified in the SLA?",
            AnswerType.TypedValue, DepthLevel.L2, 0.65),
        // L3: deep scrutiny
        new("saas.op.l3.1", Dimension.Operational, "Has the vendor achieved SOC 2 Type II certification?",
            AnswerType.YesNo,      DepthLevel.L3, 0.70),
        new("saas.op.l3.2", Dimension.Operational, "What is the vendor's disaster recovery RTO/RPO target?",
            AnswerType.TypedValue, DepthLevel.L3, 0.70),

        // ── Experiential ─────────────────────────────────────────────────────
        new("saas.exp.l1.1", Dimension.Experiential, "Has the vendor met their contracted SLA over the past 12 months?",
            AnswerType.YesNo,       DepthLevel.L1, 0.60),
        new("saas.exp.l1.2", Dimension.Experiential, "What is the current CSAT or NPS score recorded for the vendor?",
            AnswerType.TypedValue,  DepthLevel.L1, 0.60),
        new("saas.exp.l2.1", Dimension.Experiential, "Have there been unresolved escalations with the vendor in the past 6 months?",
            AnswerType.YesNo,       DepthLevel.L2, 0.65),
        new("saas.exp.l2.2", Dimension.Experiential, "What is the vendor's support ticket average resolution time?",
            AnswerType.TypedValue,  DepthLevel.L2, 0.65),
        new("saas.exp.l3.1", Dimension.Experiential, "Has the vendor delivered a major roadmap commitment on schedule in the past year?",
            AnswerType.YesNo,       DepthLevel.L3, 0.70),
        new("saas.exp.l3.2", Dimension.Experiential, "What is the trend in support ticket volume over the past 12 months?",
            AnswerType.StatusSelect, DepthLevel.L3, 0.70),

        // ── Financial ────────────────────────────────────────────────────────
        new("saas.fin.l1.1", Dimension.Financial, "Is there a signed contract with defined payment terms?",
            AnswerType.YesNo,      DepthLevel.L1, 0.60),
        new("saas.fin.l1.2", Dimension.Financial, "What is the total annual contract value?",
            AnswerType.TypedValue, DepthLevel.L1, 0.60),
        new("saas.fin.l2.1", Dimension.Financial, "Have there been billing disputes or invoicing errors in the past 12 months?",
            AnswerType.YesNo,      DepthLevel.L2, 0.65),
        new("saas.fin.l2.2", Dimension.Financial, "What are the contract termination notice period and exit provisions?",
            AnswerType.TypedValue, DepthLevel.L2, 0.65),
        new("saas.fin.l3.1", Dimension.Financial, "Does the vendor's contract include price-lock or cost-cap provisions?",
            AnswerType.YesNo,      DepthLevel.L3, 0.70),
        new("saas.fin.l3.2", Dimension.Financial, "What are the vendor's latest available revenue and financial health indicators?",
            AnswerType.TypedValue, DepthLevel.L3, 0.70),

        // ── Strategic ────────────────────────────────────────────────────────
        new("saas.str.l1.1", Dimension.Strategic, "Is the vendor's product roadmap aligned with our strategic direction?",
            AnswerType.YesNo,       DepthLevel.L1, 0.60),
        new("saas.str.l1.2", Dimension.Strategic, "What is the contractual renewal date for the vendor agreement?",
            AnswerType.TypedValue,  DepthLevel.L1, 0.60),
        new("saas.str.l2.1", Dimension.Strategic, "Has the vendor shared a product roadmap covering the next 12 months?",
            AnswerType.YesNo,       DepthLevel.L2, 0.65),
        new("saas.str.l2.2", Dimension.Strategic, "What is the strategic substitutability level for this vendor?",
            AnswerType.StatusSelect, DepthLevel.L2, 0.65),
        new("saas.str.l3.1", Dimension.Strategic, "Is there a documented exit or migration plan if the vendor relationship ends?",
            AnswerType.YesNo,       DepthLevel.L3, 0.70),
        new("saas.str.l3.2", Dimension.Strategic, "Has the vendor demonstrated compliance with our data sovereignty requirements?",
            AnswerType.YesNo,       DepthLevel.L3, 0.70),
    ];
}
