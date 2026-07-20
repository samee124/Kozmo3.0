using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Po.VendorCall;

namespace Kozmo.Api.Pages;

/// <summary>
/// Razor Page served at /vendor-calls/{runId}/review?token={token}.
///
/// GET:  Validates the review token. Renders the 3-step review form (summary,
///       corrections, evidence decision) or an error state.
///
/// POST: Re-validates the token. Parses form data, saves PostMeetingReviewSubmission,
///       invalidates the token (one-use), and advances status:
///         PromoteToEvidence=true  → AwaitingUserReview
///         PromoteToEvidence=false → Closed
///
/// [IgnoreAntiforgeryToken]: the review token provides CSRF protection — it is
/// unforgeable without the server secret and is invalidated after first use.
/// </summary>
[IgnoreAntiforgeryToken]
public sealed class VendorCallReviewModel : PageModel
{
    private readonly SqliteVendorCallRunStore     _runStore;
    private readonly SqlitePostMeetingReviewStore _reviewStore;

    public enum ReviewState { Pending, Submitted, Invalid }

    // ── View state ─────────────────────────────────────────────────────────

    public ReviewState         State              { get; private set; } = ReviewState.Invalid;
    public PostMeetingSummary? Summary            { get; private set; }
    public bool                PromotedToEvidence { get; private set; }

    // ── Route + query binding (SupportsGet binds from both GET and POST) ───

    /// <summary>From route segment {runId} and from hidden POST field "RunId".</summary>
    [BindProperty(SupportsGet = true)]
    public string? RunId { get; set; }

    /// <summary>From query string ?token=... and from hidden POST field "Token".</summary>
    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    // ── POST-only form fields ──────────────────────────────────────────────

    [BindProperty] public string? SummaryAccurate  { get; set; }
    [BindProperty] public string? RevisionsJson    { get; set; }
    [BindProperty] public string? Additions        { get; set; }
    [BindProperty] public string? PromoteToEvidence { get; set; }

    public VendorCallReviewModel(
        SqliteVendorCallRunStore     runStore,
        SqlitePostMeetingReviewStore reviewStore)
    {
        _runStore    = runStore;
        _reviewStore = reviewStore;
    }

    // ── GET ────────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnGetAsync()
    {
        var run = await ResolveAndValidateAsync(RunId, Token);
        if (run is null) return Page();

        Summary = TryDeserializeSummary(run.SummaryJson);
        if (Summary is null)
        {
            State = ReviewState.Invalid;
            return Page();
        }

        State = ReviewState.Pending;
        return Page();
    }

    // ── POST ───────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostAsync()
    {
        var run = await ResolveAndValidateAsync(RunId, Token);
        if (run is null) return Page();

        var accurate    = !string.Equals(SummaryAccurate, "false", StringComparison.OrdinalIgnoreCase);
        var promote     = string.Equals(PromoteToEvidence, "true",  StringComparison.OrdinalIgnoreCase);
        var corrections = ParseCorrections(RevisionsJson);

        var submission = new PostMeetingReviewSubmission(
            RunId:             run.Id,
            SubmittedAt:       DateTimeOffset.UtcNow,
            SummaryAccurate:   accurate,
            Corrections:       corrections,
            Additions:         string.IsNullOrWhiteSpace(Additions) ? null : Additions.Trim(),
            PromoteToEvidence: promote);

        await _reviewStore.SaveAsync(submission, HttpContext.RequestAborted);

        // Invalidate one-use token; advance lifecycle status
        run.ReviewToken          = null;
        run.ReviewTokenExpiresAt = null;
        run.Status               = promote ? VendorCallStatus.AwaitingUserReview : VendorCallStatus.Closed;
        run.UpdatedAt            = DateTimeOffset.UtcNow;
        await _runStore.SaveAsync(run, HttpContext.RequestAborted);

        PromotedToEvidence = promote;
        State              = ReviewState.Submitted;
        return Page();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<VendorCallRun?> ResolveAndValidateAsync(string? runIdStr, string? tokenStr)
    {
        if (string.IsNullOrWhiteSpace(runIdStr) ||
            string.IsNullOrWhiteSpace(tokenStr) ||
            !Guid.TryParse(runIdStr, out var runId))
        {
            State = ReviewState.Invalid;
            return null;
        }

        var run = await _runStore.GetByIdAsync(runId, HttpContext.RequestAborted);
        if (run is null || string.IsNullOrEmpty(run.ReviewToken))
        {
            State = ReviewState.Invalid;
            return null;
        }

        if (!string.Equals(run.ReviewToken, tokenStr, StringComparison.Ordinal) ||
            run.ReviewTokenExpiresAt is null ||
            run.ReviewTokenExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            State = ReviewState.Invalid;
            return null;
        }

        return run;
    }

    private static PostMeetingSummary? TryDeserializeSummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<PostMeetingSummary>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    private static IReadOnlyList<ItemCorrection> ParseCorrections(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return [];
        try
        {
            return JsonSerializer.Deserialize<List<ItemCorrection>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch { return []; }
    }
}
