using Ii.Completeness;
using Kozmo.Contracts;
using Kyv.ProgramRunner;
using Xunit;

namespace Kyv.ProgramRunner.Tests;

/// <summary>
/// Pins the completed Salesforce demo scenario (Path C — see KYV_KNOWN_GAPS.md): Scenario 05's
/// real documents (SOW, Amendment 2) both defer fees and payment terms to an Order Form
/// referenced by name but never present in the corpus. A faithful, completing-not-inventing
/// Order Form (annual_value $214,500, payment_terms Net 30, term "coterminous with the
/// Agreement" — no calendar date) was authored and dropped into the workspace to close that gap.
///
/// Mirrors <see cref="RealDocumentCompletenessProofTests"/>'s IIVS proof, for Salesforce.
/// Guards against silent regression of the demo's headline result: Financial coverage 0% -> 100%.
/// </summary>
public sealed class SalesforceOrderFormCompletenessProofTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid RealSalesforceVendorId = Guid.Parse("d0000000-0000-0000-0000-000000000002");

    private static readonly string Workspace = @"D:\June\Kozmo Workspace";
    private static readonly string RepoRoot  = FindRepoRoot();

    [SkippableFact]
    public async Task RealSalesforceBeliefs_OrderFormClosesFinancialGap_RenewalStaysSingleSource()
    {
        Skip.If(!Directory.Exists(Workspace), $"Workspace absent: '{Workspace}'.");

        var candidateCassette  = Path.Combine(RepoRoot, "fixtures", "kyv", "candidate-extraction.cassette.json");
        var beliefCassette     = Path.Combine(RepoRoot, "fixtures", "kyv", "belief-extraction.cassette.json");
        var answeringCassette  = Path.Combine(RepoRoot, "fixtures", "completeness", "answering.cassette.json");
        Skip.If(!File.Exists(answeringCassette), "Completeness answering cassette not recorded yet.");

        var profile = CatalogueTestHelper.LoadProfile();
        var beliefs = await RealVendorBeliefFixture.BuildAsync(
            Workspace, candidateCassette, beliefCassette, profile, "Salesforce", RealSalesforceVendorId, Now);

        Assert.NotNull(beliefs);
        Assert.NotEmpty(beliefs!);

        // ── The Order Form's structural facts persisted, correlated to Salesforce ──────────────
        var annualValue = Assert.Single(beliefs!, b => b.Criterion.Equals("annual_value", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(214_500, annualValue.Value);
        Assert.Contains("OrderForm_01_EducationCloud", annualValue.Derivation);

        var paymentTerms = Assert.Single(beliefs!, b => b.Criterion.Equals("payment_terms", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(30, paymentTerms.Value);
        Assert.Contains("OrderForm_01_EducationCloud", paymentTerms.Derivation);

        // ── Renewal stays single-source: Amendment 2 only, no supersession-bug duplicate ────────
        // The Order Form's term is "coterminous with the Agreement" — no calendar date — so the
        // extractor must abstain on renewal_date rather than emit a second, competing belief.
        var renewalDate = Assert.Single(beliefs!, b => b.Criterion.Equals("renewal_date", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Amendment_02_Term_Extension", renewalDate.Derivation);
        Assert.Equal(new DateTimeOffset(2028, 6, 30, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), (long)renewalDate.Value);

        // ── Operational/Experiential stay honest gaps — no invented SLA/CSAT ────────────────────
        Assert.DoesNotContain(beliefs!, b => b.Criterion.Equals("sla_uptime", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(beliefs!, b => b.Criterion.Equals("csat", StringComparison.OrdinalIgnoreCase));

        // ── Financial coverage grounds on the real Order Form facts ─────────────────────────────
        var answeringLlm = new Kozmo.Llm.CachingLlmClient(answeringCassette, recordMode: false);
        var stage        = new QuestionAnsweringStage(answeringLlm, profile);
        var questions    = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1);

        var answers          = await stage.AnswerAsync(RealSalesforceVendorId, questions, beliefs!, Now);
        var coverageProfile  = CompletenessRubric.Compute(questions, answers);

        var operational  = coverageProfile.DimensionCoverages.Single(d => d.Dimension == Dimension.Operational);
        var experiential = coverageProfile.DimensionCoverages.Single(d => d.Dimension == Dimension.Experiential);
        var financial    = coverageProfile.DimensionCoverages.Single(d => d.Dimension == Dimension.Financial);

        Assert.Equal(0, operational.AnsweredCount);
        Assert.Equal(0, experiential.AnsweredCount);
        Assert.Equal(2, financial.AnsweredCount);
        Assert.Equal(1.0, financial.Coverage);

        var contractExistence = answers.Single(a => a.QuestionId == "saas.fin.l1.1");
        Assert.Equal("YES", contractExistence.Value, ignoreCase: true);
        // Previously a known gap (KYV_KNOWN_GAPS.md): the model cited malformed GUIDs here that
        // failed Guid.TryParse and were silently dropped. Fixed as a side effect of the belief-id
        // ordinal stability fix — citations are now small integers ("1", "2"), far less prone to
        // being mangled than a 36-character GUID, and this answer now resolves both real ids.
        Assert.Contains(annualValue.Id, contractExistence.CitedBeliefIds);
        Assert.Contains(paymentTerms.Id, contractExistence.CitedBeliefIds);

        var annualValueAnswer = answers.Single(a => a.QuestionId == "saas.fin.l1.2");
        Assert.Equal("214500", annualValueAnswer.Value);
        Assert.Contains(annualValue.Id, annualValueAnswer.CitedBeliefIds);
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
