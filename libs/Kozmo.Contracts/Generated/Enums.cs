// GENERATED — do not hand-edit; regenerate via tools/codegen/generate.ps1

namespace Kozmo.Contracts;

public enum Dimension
{
    Operational,
    Experiential,
    Financial,
    Strategic
}

public enum SourceTier
{
    Verified      = 0,
    Inferred      = 1,
    Reported      = 2,
    Unverified    = 3,
    Primary       = 4,  // of-record, executed — vendor file only
    Correspondence = 5  // informal correspondence (email) — strictly weakest; E-signal Part 5 Step 1
}

public enum DocType
{
    SignedContract,
    ExecutedAgreement,
    Amendment,
    Addendum,
    PurchaseOrder,
    Invoice,
    UsageCsv,
    SpendCsv,
    SystemExport,
    Quote,
    Proposal,
    Email,
    Communication,
    OwnerNote,
    Feedback,
    WebProfile,
    News,
    ModelDerived
}

public enum Band
{
    Healthy,
    AtRisk,
    Critical
}

public enum Stance
{
    Maintain,
    Monitor,
    Renegotiate,
    Escalate,
    Remediate
}

public enum SourceSystem
{
    MonitoringPlatform,
    BillingSystem,
    CRM,
    SupportSystem,
    UsageAnalytics,
    Email,
    ContractSystem,
    HumanReport
}

public enum TrendPattern
{
    Improving,
    Stable,
    Declining
}

public enum ContradictionSeverity { Low, Medium, High }

/// <summary>Which pipeline path detected a contradiction or gap.</summary>
public enum DetectionSource { Deterministic, Llm }

/// <summary>How a belief was classified. Annotation only — not a fingerprint input.</summary>
public enum ClassificationMethod { Rule, Lexicon, Llm }
