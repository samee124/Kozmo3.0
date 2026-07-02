namespace Kozmo.Api;

public sealed record VendorSummaryDto(
    string         EntityId,
    string         Name,
    string         Band,
    string         Stance,
    double         Confidence,
    string         Fingerprint,
    DateTimeOffset AsOf);

public sealed record VendorDetailDto(
    string         EntityId,
    string         Name,
    DateTimeOffset AsOf,
    IndexViewDto   Index,
    PostureViewDto Posture);

public sealed record IndexViewDto(
    double                           Composite,
    double                           ConfidenceFloor,
    string                           Band,
    string                           Fingerprint,
    string                           ConfigVersion,
    string                           BandDrivenBy,
    DimensionMinDto?                 WorstDimension,
    IReadOnlyList<DimensionScoreViewDto> Dimensions);

public sealed record DimensionMinDto(string Dimension, double Score);

public sealed record DimensionScoreViewDto(
    string Dimension,
    double Score,
    double Confidence,
    double Weight,
    double Contribution,
    int    BeliefCount);

public sealed record PostureViewDto(
    string                 Stance,
    double                 Confidence,
    string                 Rationale,
    IReadOnlyList<string>  Cautions,
    IReadOnlyList<string>  EvidenceGaps,
    RenewalViewDto?        Renewal);

public sealed record RenewalViewDto(
    DateTimeOffset RenewalDate,
    bool           WindowActive,
    int            DaysToRenewal);

public sealed record ReasoningTrailDto(
    PostureViewDto                      Posture,
    BandViewDto                         Band,
    IndexViewDto                        Index,
    IReadOnlyList<DimensionBeliefViewDto> Dimensions);

public sealed record BandViewDto(
    string           Band,
    BandThresholdsDto Thresholds,
    string           DrivenBy);

public sealed record BandThresholdsDto(
    double Critical,
    double AtRisk,
    double Healthy);

public sealed record DimensionBeliefViewDto(
    string                    Dimension,
    double                    Score,
    double                    Confidence,
    double                    Weight,
    IReadOnlyList<BeliefViewDto> Beliefs);

public sealed record BeliefViewDto(
    string       BeliefId,
    string       Dimension,
    string       Criterion,
    double       Value,
    double       Confidence,
    string       SourceTier,
    string       ClassificationMethod,
    string?      ReasoningSummary,
    double       Freshness,
    SignalRefDto? Signal)
{
    // ── Confidence-anchor provenance — annotation only, never fingerprint inputs ─

    /// <summary>Confidence before the anchor was applied. Non-null when anchor fired.</summary>
    public double? AnchorRawConfidence { get; init; } = null;

    /// <summary>Id of the predecessor belief that provided the confidence floor.</summary>
    public string? AnchorPredecessorId { get; init; } = null;

    /// <summary>SourceTier name of the predecessor belief.</summary>
    public string? AnchorPredecessorTier { get; init; } = null;
}

public sealed record SignalRefDto(
    string         SignalId,
    string         Type,
    DateTimeOffset Timestamp,
    string         Source,
    string?        Summary);

public sealed record TrajectoryPointDto(
    DateTimeOffset Timestamp,
    string?        SignalId,
    double         Composite,
    string         Band,
    string         Stance,
    string         Fingerprint);

// ── Vendor file ───────────────────────────────────────────────────────────────

public sealed record VendorFileIngestRequest(string FixturePath);

// ── Vendor name resolution (identity upsert) ──────────────────────────────────

public sealed record NameResolveRequest(string VendorName);

public sealed record NameResolveResponse(string VendorId, bool IsNew, string CanonicalName);

// ── Live signal injection ──────────────────────────────────────────────────────

public sealed record LiveSignalRequest(string Body);

public sealed record ClassificationView(
    string Dimension,
    string Criterion,
    double Value,
    double MethodConfidence,
    string ReasoningSummary,
    string SourceTier);

public sealed record LiveSignalResponse(
    ClassificationView Classification,
    VendorSummaryDto   Vendor,
    IndexViewDto       Index);
