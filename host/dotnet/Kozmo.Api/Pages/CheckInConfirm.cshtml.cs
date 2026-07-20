using Ig.Contracts;
using Ig.Resolution;
using Ii.Contracts;
using Km.Store;
using Kozmo.Contracts.Config;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Wc.CheckIn;
using Wc.Contracts;

namespace Kozmo.Api.Pages;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Razor Page served at /check-ins/{id}/confirm?token={capability-token}.
///
/// GET:  Validates the token (HMACSHA256 + expiry). Never calls AnswerCheckInService.
///       Renders a confirm form, "already recorded" banner, or error state.
///
/// POST: Re-validates the token (full re-check, not relying on GET state).
///       Race-safe: if already ANSWERED/PROCESSED, returns "already recorded".
///       On success: calls AnswerCheckInService then ProcessCheckInService and renders
///       "Recorded. Thanks." without a redirect — avoids a second GET that could be
///       intercepted by security scanners.
///
/// [IgnoreAntiforgeryToken]: the signed capability token provides CSRF protection —
/// it is unforgeable without the server secret and is single-use in intent (same checkInId
/// + value can only answer an OPEN check-in once).
///
/// INVARIANT: OnGetAsync NEVER calls AnswerCheckInService under any input.
/// </summary>
[IgnoreAntiforgeryToken]
public sealed class CheckInConfirmModel : PageModel
{
    private readonly ICheckInStore       _checkInStore;
    private readonly CheckInTokenOptions _tokenOptions;
    private readonly SqliteEntityStore   _store;
    private readonly SaasProfile         _profile;
    private readonly IIiFacade           _facade;

    // ── View state ─────────────────────────────────────────────────────────

    /// <summary>State rendered by the Razor template.</summary>
    public ConfirmState State { get; private set; } = ConfirmState.Error;

    public enum ConfirmState
    {
        /// <summary>Token valid, check-in OPEN — show confirm form.</summary>
        PendingConfirm,
        /// <summary>Check-in already ANSWERED or PROCESSED.</summary>
        AlreadyRecorded,
        /// <summary>Token invalid, expired, or check-in not found.</summary>
        Error,
        /// <summary>POST succeeded — answer recorded and processed.</summary>
        Recorded
    }

    public string Question     { get; private set; } = "";
    public string DisplayValue { get; private set; } = "";
    public string ErrorMessage { get; private set; } = "";

    // Bound from route / query
    [BindProperty(SupportsGet = true)]
    public string?  Id    { get; set; }
    [BindProperty(SupportsGet = true)]
    public string?  Token { get; set; }

    public CheckInConfirmModel(
        ICheckInStore        checkInStore,
        CheckInTokenOptions  tokenOptions,
        SqliteEntityStore    store,
        SaasProfile          profile,
        IIiFacade            facade)
    {
        _checkInStore = checkInStore;
        _tokenOptions = tokenOptions;
        _store        = store;
        _profile      = profile;
        _facade       = facade;
    }

    // ── GET — token validation only, NEVER mutates state ──────────────────

    public async Task<IActionResult> OnGetAsync()
    {
        // INVARIANT: this method NEVER calls AnswerCheckInService.

        if (!TryParseAndValidateToken(out var checkInId, out var value))
        {
            State = ConfirmState.Error;
            ErrorMessage = "This link is invalid or has expired.";
            return Page();
        }

        var ci = await _checkInStore.GetAsync(checkInId);
        if (ci is null)
        {
            State = ConfirmState.Error;
            ErrorMessage = "Check-in not found.";
            return Page();
        }

        if (ci.Status != PendingStatus.OPEN)
        {
            State    = ConfirmState.AlreadyRecorded;
            Question = ci.Question;
            return Page();
        }

        State        = ConfirmState.PendingConfirm;
        Question     = ci.Question;
        DisplayValue = ToDisplayValue(value);
        return Page();
    }

    // ── POST — re-validate + record ────────────────────────────────────────

    public async Task<IActionResult> OnPostAsync()
    {
        if (!TryParseAndValidateToken(out var checkInId, out var value))
        {
            State        = ConfirmState.Error;
            ErrorMessage = "This link is invalid or has expired.";
            return Page();
        }

        // Map token value to the format AnswerCheckInService expects.
        var responseValue = MapTokenValue(value);
        if (responseValue is null)
        {
            // UNKNOWN — cannot be recorded via YES_NO validator; redirect to pending queue.
            var pendingUrl = $"{_tokenOptions.UiBaseUrl}/pending?highlight={checkInId}";
            return Redirect(pendingUrl);
        }

        var answerSvc = new AnswerCheckInService();
        var result    = await answerSvc.ProcessAnswerAsync(
            checkInId, responseValue, DateTimeOffset.UtcNow,
            _checkInStore,
            new VendorFileWriteService(_store, _profile),
            _profile, _facade,
            new IdentityRegistry(_store));

        if (result.Outcome == AnswerOutcome.AlreadyAnswered)
        {
            var ci2 = await _checkInStore.GetAsync(checkInId);
            State    = ConfirmState.AlreadyRecorded;
            Question = ci2?.Question ?? "";
            return Page();
        }

        if (result.Outcome != AnswerOutcome.Ok)
        {
            State        = ConfirmState.Error;
            ErrorMessage = result.Outcome == AnswerOutcome.NotFound
                ? "Check-in not found."
                : "Answer could not be recorded (shape mismatch).";
            return Page();
        }

        var ci3 = result.Updated!;
        State        = ConfirmState.Recorded;
        Question     = ci3.Question;
        DisplayValue = ToDisplayValue(value);
        return Page();
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private bool TryParseAndValidateToken(out Guid checkInId, out string value)
    {
        checkInId = Guid.Empty;
        value     = string.Empty;

        if (string.IsNullOrWhiteSpace(Id)    ||
            string.IsNullOrWhiteSpace(Token) ||
            !Guid.TryParse(Id, out var idGuid))
            return false;

        if (!CheckInLinkToken.TryValidate(Token, _tokenOptions.Secret, DateTimeOffset.UtcNow,
                out var tokenId, out value))
            return false;

        // Route ID must match the ID embedded in the token.
        if (tokenId != idGuid) return false;

        checkInId = idGuid;
        return true;
    }

    private static string? MapTokenValue(string value) => value switch
    {
        "YES"     => "true",
        "NO"      => "false",
        "UNKNOWN" => null,   // redirect caller handles this
        // Evidence-option values (e.g. "99.5", "Declining") are passed through verbatim.
        // The signed token already validates the value was generated by our code; the shape
        // validator in AnswerCheckInService enforces correctness before any write.
        _         => value
    };

    private static string ToDisplayValue(string value) => value switch
    {
        "YES"     => "Yes",
        "NO"      => "No",
        "UNKNOWN" => "Unsure",
        _         => value
    };
}
