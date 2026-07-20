using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Po.VendorCall;

namespace Kozmo.Api.Pages;

/// <summary>
/// Razor Page served at /vendor-calls/{runId}/update?token={token}.
///
/// GET:  Validates the review token. Renders a form for the user to post a
///       free-text context note before the meeting (e.g. "pricing already agreed verbally").
///
/// POST: Re-validates the token. Saves a VendorUpdateNote for future Q2 composition.
///       Token is NOT invalidated on POST — multiple notes may be submitted before the meeting.
///
/// [IgnoreAntiforgeryToken]: the review token provides CSRF protection.
/// </summary>
[IgnoreAntiforgeryToken]
public sealed class VendorCallUpdateModel : PageModel
{
    private readonly SqliteVendorCallRunStore  _runStore;
    private readonly SqliteVendorUpdateNoteStore _noteStore;

    public enum UpdateState { Form, Saved, Invalid }

    public UpdateState State      { get; private set; } = UpdateState.Invalid;
    public VendorCallRun? Run     { get; private set; }
    public string? SavedNoteText  { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? RunId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    [BindProperty]
    public string? NoteText { get; set; }

    public VendorCallUpdateModel(
        SqliteVendorCallRunStore   runStore,
        SqliteVendorUpdateNoteStore noteStore)
    {
        _runStore  = runStore;
        _noteStore = noteStore;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var run = await ResolveAndValidateAsync(RunId, Token);
        if (run is null) return Page();

        Run   = run;
        State = UpdateState.Form;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var run = await ResolveAndValidateAsync(RunId, Token);
        if (run is null) return Page();

        Run = run;

        var text = NoteText?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            State = UpdateState.Form;
            return Page();
        }

        var note = new VendorUpdateNote(
            Id:              Guid.NewGuid(),
            VendorId:        run.VendorId,
            VendorCallRunId: run.Id,
            NoteText:        text,
            SubmittedByUpn:  run.SignedInUserPrincipalId,
            SubmittedAtUtc:  DateTimeOffset.UtcNow);

        await _noteStore.SaveAsync(note, HttpContext.RequestAborted);

        SavedNoteText = text;
        State         = UpdateState.Saved;
        return Page();
    }

    private async Task<VendorCallRun?> ResolveAndValidateAsync(string? runIdStr, string? tokenStr)
    {
        if (string.IsNullOrWhiteSpace(runIdStr) ||
            string.IsNullOrWhiteSpace(tokenStr) ||
            !Guid.TryParse(runIdStr, out var runId))
        {
            State = UpdateState.Invalid;
            return null;
        }

        var run = await _runStore.GetByIdAsync(runId, HttpContext.RequestAborted);
        if (run is null || string.IsNullOrEmpty(run.ReviewToken))
        {
            State = UpdateState.Invalid;
            return null;
        }

        if (!string.Equals(run.ReviewToken, tokenStr, StringComparison.Ordinal) ||
            run.ReviewTokenExpiresAt is null ||
            run.ReviewTokenExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            State = UpdateState.Invalid;
            return null;
        }

        return run;
    }
}
