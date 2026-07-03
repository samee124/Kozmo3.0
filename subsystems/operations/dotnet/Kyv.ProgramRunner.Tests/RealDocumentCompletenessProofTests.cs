using Ii.Completeness;
using Kyv.ProgramRunner;
using Xunit;

namespace Kyv.ProgramRunner.Tests;

/// <summary>
/// Commit 3 — end-to-end proof: real workspace documents -> real persisted beliefs -> real
/// QuestionAnsweringStage coverage, via the SAME RealVendorBeliefFixture.BuildAsync that
/// tools/Kozmo.CompletenessRecorder used to record fixtures/completeness/answering.cassette.json.
///
/// Structural beliefs (payment_terms, annual_value, renewal_date) persist with Confidence=0 —
/// correct for RubricModule, which must never average them into a dimension score. But
/// AnsweringPrompt.System tells the completeness LLM "confidence reflects evidence weight ... no
/// relevant beliefs -> <= 0.30", and a literal 0.0 read as "no evidence" and suppressed grounded
/// answers even when the criterion/value/derivation were right there in the prompt. Fixed via
/// AnsweringPrompt.PresentationConfidence: a PRESENTATION-ONLY substitute (the belief's own
/// source-tier ceiling) shown to this prompt alone — the persisted Confidence and RubricModule's
/// scoring path are untouched. See KYV_KNOWN_GAPS.md for the two-concepts-in-one-field design
/// tension this papers over; a real evidence-weight/scoring-weight split is the proper long-term
/// fix, deferred for now.
///
/// Post extraction-bug fixes (Commits A/B/C): IIVS's real documents no longer produce a csat
/// belief at all — its only apparent CSAT evidence was "study quality scores averaged 4.6 out of
/// 5.0", a QA metric on lab study deliverables, which Commit C's tightened prompt + guard now
/// correctly rejects. That means IIVS genuinely has no real customer-satisfaction evidence in its
/// corpus — a true reflection of the source documents, not a regression. This test was updated to
/// assert the ABSENCE of that mis-extracted belief instead of its presence.
///
/// Result: saas.fin.l1.1 ("signed contract with defined payment terms") answers YES, grounded in
/// the real payment_terms belief — Financial coverage 1/2 (50%). Other dimensions and
/// saas.fin.l1.2 (a numeric "what is the total annual contract value" question) remain UNKNOWN.
/// </summary>
public sealed class RealDocumentCompletenessProofTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid RealIivsVendorId = Guid.Parse("d0000000-0000-0000-0000-000000000001");

    private static readonly string Workspace = @"D:\June\Kozmo Workspace";
    private static readonly string RepoRoot  = FindRepoRoot();

    [SkippableFact]
    public async Task RealIivsBeliefs_GroundFinancialCoverage_ViaPresentationConfidence()
    {
        Skip.If(!Directory.Exists(Workspace), $"Workspace absent: '{Workspace}'.");

        var candidateCassette = Path.Combine(RepoRoot, "fixtures", "kyv", "candidate-extraction.cassette.json");
        var beliefCassette    = Path.Combine(RepoRoot, "fixtures", "kyv", "belief-extraction.cassette.json");
        var answeringCassette = Path.Combine(RepoRoot, "fixtures", "completeness", "answering.cassette.json");
        Skip.If(!File.Exists(answeringCassette), "Completeness answering cassette not recorded yet.");

        var profile = CatalogueTestHelper.LoadProfile();
        var beliefs = await RealVendorBeliefFixture.BuildAsync(
            Workspace, candidateCassette, beliefCassette, profile, "Vitro", RealIivsVendorId, Now);

        Assert.NotNull(beliefs);
        Assert.NotEmpty(beliefs!); // the persistence bridge DOES carry real documents into real beliefs

        // Commit C regression guard: IIVS's only apparent CSAT evidence was a mis-extracted QA
        // metric ("study quality scores") — the fix means it must NOT appear as a csat belief.
        Assert.DoesNotContain(beliefs!, b => b.Criterion.Equals("csat", StringComparison.OrdinalIgnoreCase));

        // Structural belief present with Confidence=0 — UNCHANGED by the presentation-confidence
        // fix; only what QuestionAnsweringStage shows the LLM changes.
        Assert.Contains(beliefs!, b => b.Criterion.Equals("payment_terms", StringComparison.OrdinalIgnoreCase) && b.Confidence == 0.0);

        var answeringLlm = new Kozmo.Llm.CachingLlmClient(answeringCassette, recordMode: false);
        var stage        = new QuestionAnsweringStage(answeringLlm, profile);
        var questions    = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1);

        var answers = await stage.AnswerAsync(RealIivsVendorId, questions, beliefs!, Now);
        var coverageProfile = CompletenessRubric.Compute(questions, answers);

        Assert.True(coverageProfile.OverallCoverage > 0,
            "Real IIVS documents must produce SOME grounded completeness coverage — 0% would mean " +
            "the presentation-confidence fix regressed.");

        var financial = coverageProfile.DimensionCoverages.Single(d => d.Dimension == Kozmo.Contracts.Dimension.Financial);
        Assert.True(financial.Coverage > 0,
            $"Financial coverage must be > 0 — real payment_terms/annual_value evidence exists for " +
            $"IIVS. Got {financial.Coverage:P0} ({financial.AnsweredCount}/{financial.RequiredCount}).");

        // Pins the specific grounded answer this fix produces: existence of a signed contract with
        // payment terms, cited to the real payment_terms beliefs.
        var financialExistence = answers.Single(a => a.QuestionId == "saas.fin.l1.1");
        Assert.Equal("YES", financialExistence.Value, ignoreCase: true);
        Assert.NotEmpty(financialExistence.CitedBeliefIds);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Kozmo.sln"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Cannot locate repo root (Kozmo.sln not found).");
    }
}
