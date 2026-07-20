using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Po.VendorCall;

namespace Kozmo.Api.Pages;

/// <summary>
/// Razor Page served at /vendor-calls/{runId}/evidence?token={token}.
///
/// GET: Validates the pre-meeting review token. Shows the source reference IDs
///      from the latest ReviewCheckpoint for this vendor, giving the user visibility
///      into what evidence was used when composing the review email.
///
/// [IgnoreAntiforgeryToken]: token-gated read-only page; no state mutation.
/// </summary>
[IgnoreAntiforgeryToken]
public sealed class VendorCallEvidenceModel : PageModel
{
    private readonly SqliteVendorCallRunStore     _runStore;
    private readonly SqliteReviewCheckpointStore  _checkpointStore;
    private readonly ILogger<VendorCallEvidenceModel> _logger;

    public enum EvidenceState { Pending, Invalid }

    public EvidenceState              State            { get; private set; } = EvidenceState.Invalid;
    public VendorCallRun?             Run              { get; private set; }
    public ReviewCheckpoint?          Checkpoint       { get; private set; }
    public IReadOnlyList<string>      SourceIds        { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string? RunId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public VendorCallEvidenceModel(
        SqliteVendorCallRunStore    runStore,
        SqliteReviewCheckpointStore checkpointStore,
        ILogger<VendorCallEvidenceModel> logger)
    {
        _runStore        = runStore;
        _checkpointStore = checkpointStore;
        _logger          = logger;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        try
        {
            var run = await ResolveAndValidateAsync(RunId, Token);
            if (run is null) return Page();

            Run = run;
            Checkpoint = await _checkpointStore.GetLatestAsync(run.VendorId, HttpContext.RequestAborted);
            SourceIds  = Checkpoint?.SourceReferenceIds ?? [];
            State      = EvidenceState.Pending;
            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in VendorCallEvidence for RunId={RunId}", RunId);
            State = EvidenceState.Invalid;
            return Page();
        }
    }

    private async Task<VendorCallRun?> ResolveAndValidateAsync(string? runIdStr, string? tokenStr)
    {
        if (string.IsNullOrWhiteSpace(runIdStr) ||
            string.IsNullOrWhiteSpace(tokenStr) ||
            !Guid.TryParse(runIdStr, out var runId))
        {
            _logger.LogWarning("Evidence: bad runId or token param. RunId={R} TokenLen={L}", runIdStr, tokenStr?.Length);
            State = EvidenceState.Invalid;
            return null;
        }

        VendorCallRun? run;
        try { run = await _runStore.GetByIdAsync(runId, HttpContext.RequestAborted); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Evidence: GetByIdAsync threw for RunId={RunId}", runId);
            State = EvidenceState.Invalid;
            return null;
        }

        if (run is null)
        {
            _logger.LogWarning("Evidence: run not found in DB for RunId={RunId}", runId);
            State = EvidenceState.Invalid;
            return null;
        }

        if (string.IsNullOrEmpty(run.ReviewToken))
        {
            _logger.LogWarning("Evidence: run {RunId} found but ReviewToken is null/empty (status={Status})", runId, run.Status);
            State = EvidenceState.Invalid;
            return null;
        }

        if (!string.Equals(run.ReviewToken, tokenStr, StringComparison.Ordinal))
        {
            _logger.LogWarning("Evidence: token mismatch for RunId={RunId}", runId);
            State = EvidenceState.Invalid;
            return null;
        }

        if (run.ReviewTokenExpiresAt is null || run.ReviewTokenExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            _logger.LogWarning("Evidence: token expired for RunId={RunId} ExpiresAt={Exp}", runId, run.ReviewTokenExpiresAt);
            State = EvidenceState.Invalid;
            return null;
        }

        return run;
    }
}
