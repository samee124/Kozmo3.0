namespace Wc.Contracts;

public enum CheckInKind   { IDENTITY_CONFIRM, DIMENSION_GAP }
public enum ResponseShape { YES_NO, TYPED_VALUE, STATUS_SELECT }
public enum PendingStatus { OPEN, ANSWERED, EXPIRED, PROCESSED }

/// <summary>
/// A durable, round-trip human check-in request (§1).
/// Raised from TRIAGE dispositions (kind=IDENTITY_CONFIRM) or dimension gaps
/// (kind=DIMENSION_GAP). Persists through restart; status moves OPEN→ANSWERED→PROCESSED|EXPIRED.
/// </summary>
public sealed record CheckIn(
    Guid            CheckInId,
    Guid            VendorId,
    Guid            ProgramRunId,
    CheckInKind     Kind,
    string          Question,
    ResponseShape   ResponseShape,
    string?         TargetField,
    string          Owner,
    PendingStatus   Status,
    DateTimeOffset  RaisedAt,
    DateTimeOffset? AnsweredAt,
    DateTimeOffset? ExpiresAt,
    string?         ResponseValue,
    Guid?           PairedVendorId = null,  // IDENTITY_CONFIRM: the second entity in the pair
    // E2 bridge — nullable. Copied from Question.TargetClaimKey by GapCheckInStage at raise time.
    // When set, ProcessCheckInService resolves this claim_key_catalogue key's dimension and
    // rubric_criterion and bands the answer into a real scored belief. Null (every DIMENSION_GAP
    // check-in before this field existed, and every unbound question) keeps the exact prior
    // present-field-only write.
    string?         TargetClaimKey = null
);

/// <summary>
/// Caller-formed gap request handed to RaiseCheckInsStage.
/// The question and shape are formed by the caller from the meta-cognition output;
/// raise_checkins copies them verbatim into the CheckIn record (§0: not re-derived).
/// </summary>
public sealed record VendorGapRequest(
    Guid          VendorId,
    string        Question,
    ResponseShape ResponseShape,
    string?       TargetField
);
