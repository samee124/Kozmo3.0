namespace Wc.Contracts;

public enum ProcessOutcome { Ok, NotFound, NotAnswered, AlreadyProcessed }

/// <summary>
/// Result of process_response (§4). Outcome describes the guard result;
/// AffectedVendorId is the vendor that was recomputed (non-null on Ok).
/// </summary>
public sealed record ProcessResult(ProcessOutcome Outcome, Guid? AffectedVendorId = null);
