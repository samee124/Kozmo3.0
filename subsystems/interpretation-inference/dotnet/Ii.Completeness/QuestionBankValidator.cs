using Kozmo.Contracts.Config;

namespace Ii.Completeness;

/// <summary>
/// E2.2a — boot-time coherence check: SaasQuestionBank's TargetClaimKey bindings vs the claim key
/// catalogue. Lives here, not in Km.Store.Catalogue, because Km.Store may reference Kozmo.Contracts
/// only — never Ii.* (CI-enforced pipeline-direction invariant) — so the E2.1 validator cannot see
/// SaasQuestionBank. Ii.Completeness already depends on Kozmo.Contracts.Config.SaasProfile (see
/// QuestionAnsweringStage's constructor), so the check lives on this side of the boundary instead.
///
/// Intended to run at the same "boot" moment Catalogue.Load() does — see Kozmo.Api's Program.cs,
/// which calls this immediately after loading the profile.
/// </summary>
public static class QuestionBankValidator
{
    /// <summary>Validates SaasQuestionBank.All — the real bank — against the given profile.</summary>
    public static void ValidateBindings(SaasProfile profile) => ValidateBindings(SaasQuestionBank.All, profile);

    /// <summary>
    /// Every bound TargetClaimKey (non-null) among <paramref name="questions"/> must resolve to a
    /// real claim key in <paramref name="profile"/>'s catalogue. A typo'd or stale binding should
    /// never ship silently — ProcessCheckInService.TryResolveBoundBelief falls back to legacy
    /// present-field behavior with no error when the catalogue lookup misses, which is the exact
    /// silent-failure surface this closes. Throws on the first bad binding found.
    /// </summary>
    public static void ValidateBindings(IReadOnlyList<Question> questions, SaasProfile profile)
    {
        foreach (var q in questions)
        {
            if (string.IsNullOrEmpty(q.TargetClaimKey)) continue;
            if (!profile.ClaimKeyCatalogue.ContainsKey(q.TargetClaimKey))
                throw new InvalidOperationException(
                    $"SaasQuestionBank coherence: question '{q.Id}' is bound to TargetClaimKey " +
                    $"'{q.TargetClaimKey}', which does not exist in claim_key_catalogue.saas.v1.json.");
        }
    }
}
