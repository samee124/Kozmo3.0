#pragma warning disable CS0618 // obsolete pre-Review-pipeline composers kept for reference
using Kozmo.Contracts;
using If.Contracts;
using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

/// <summary>
/// Tests for VendorCallBriefingComposer (Mode A deterministic).
/// Evidence mirrors what Phase 7a produces for the Northstar scenario.
/// </summary>
public sealed class BriefingComposerTests
{
    // ── Shared constants matching northstar.evidence.json ─────────────────────

    private static readonly Guid NorthstarVendorId =
        Guid.Parse("dd000001-0000-0000-0000-000000000001");

    private static readonly Guid ContractCurrentId =
        Guid.Parse("dd000001-0001-0000-0000-000000000001");

    private static readonly Guid ContractPriorId =
        Guid.Parse("dd000001-0002-0000-0000-000000000001");

    private static readonly Guid CommitmentSlaReportId =
        Guid.Parse("dd000001-0006-0000-0000-000000000001");

    private static readonly Guid CommitmentCounterProposalId =
        Guid.Parse("dd000001-0007-0000-0000-000000000001");

    private static readonly Guid CommitmentNoticeDeadlineId =
        Guid.Parse("dd000001-0008-0000-0000-000000000001");

    private static readonly Guid CommercialSignalId =
        Guid.Parse("dd000001-0005-0000-0000-000000000001");

    // now = 2026-07-15; renewal = 2026-09-28; notice = 60d → deadline = 2026-07-29 (14d)
    private static readonly DateTimeOffset Now =
        new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    // Unix: 1790553600 = 2026-09-28 00:00:00 UTC
    private const long RenewalDateUnix = 1790553600L;

    // ── Fixture factories ─────────────────────────────────────────────────────

    private static VendorCallRecipe DefaultRecipe() => new(
        RecipeId: "vendor-call-v1", Version: "1.0", MeetingType: "vendor_call",
        CalendarWindowDays: 14, EmailLookbackDays: 90,
        PreMeetingCheckInHours: 24, BriefingOffsetMinutes: 10,
        PostMeetingCheckInMinutes: 15,
        Sections: ["contract_position", "recent_developments", "open_commitments"],
        Limits: new RecipeLimits(MaximumEmails: 30, MaximumCheckInsPerMeeting: 2, MaximumQuestionsInBriefing: 6));

    private static CalendarArtifact DefaultMeeting() => new(
        ArtifactId:        Guid.NewGuid(),
        SourceSystem:      "graph",
        SourceType:        "calendar_event",
        TenantId:          "test",
        SourcePrincipalId: "user-1",
        ExternalId:        "evt-northstar-renewal-001",
        ICalUid:           "ical-001",
        Subject:           "Northstar Software — annual renewal review",
        StartUtc:          Now.AddDays(7),
        EndUtc:            Now.AddDays(7).AddHours(1),
        Organizer:         "rishi@econtracts.onmicrosoft.com",
        Attendees:         ["rishi@econtracts.onmicrosoft.com", "alex.hamilton@northstarsoftware.com"],
        BodyPreview:       "Annual renewal review",
        CapturedAtUtc:     Now);

    private static Evidence ContractCurrent() => new(
        EvidenceId: ContractCurrentId, VendorId: NorthstarVendorId,
        DocType: DocType.SignedContract, SourceTier: SourceTier.Primary,
        Ref: "contracts/northstar-msa-2026.pdf", DocVersion: 1,
        IngestedAt: new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero));

    private static Evidence ContractPrior() => new(
        EvidenceId: ContractPriorId, VendorId: NorthstarVendorId,
        DocType: DocType.SignedContract, SourceTier: SourceTier.Primary,
        Ref: "contracts/northstar-msa-2025.pdf", DocVersion: 1,
        IngestedAt: new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero));

    private static Evidence CommitmentSlaReport() => new(
        EvidenceId: CommitmentSlaReportId, VendorId: NorthstarVendorId,
        DocType: DocType.OwnerNote, SourceTier: SourceTier.Reported,
        Ref: "notes/northstar-commitment-sla-report-request-2026-07-10.txt", DocVersion: 1,
        IngestedAt: new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));   // 5 days → overdue

    private static Evidence CommitmentCounterProposal() => new(
        EvidenceId: CommitmentCounterProposalId, VendorId: NorthstarVendorId,
        DocType: DocType.OwnerNote, SourceTier: SourceTier.Reported,
        Ref: "notes/northstar-commitment-counter-proposal-draft-2026-07-12.txt", DocVersion: 1,
        IngestedAt: new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));   // 3 days → not overdue

    private static Evidence CommitmentNoticeDeadline() => new(
        EvidenceId: CommitmentNoticeDeadlineId, VendorId: NorthstarVendorId,
        DocType: DocType.OwnerNote, SourceTier: SourceTier.Reported,
        Ref: "notes/northstar-commitment-notice-deadline-alert-2026-07-14.txt", DocVersion: 1,
        IngestedAt: new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));   // 1 day → not overdue

    private static Evidence CommercialSignalEmail() => new(
        EvidenceId: CommercialSignalId, VendorId: NorthstarVendorId,
        DocType: DocType.Email, SourceTier: SourceTier.Correspondence,
        Ref: "email/northstar-pricing-uplift-proposal-2026-07-08.eml", DocVersion: 1,
        IngestedAt: new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero));

    private static IReadOnlyList<Belief> DefaultBeliefs() =>
    [
        MakeBelief("annual_value",  285000.0,        ClaimKey: "annual_value"),
        MakeBelief("renewal_date",  RenewalDateUnix, ClaimKey: "renewal_date"),
        MakeBelief("notice_period", 60.0,            ClaimKey: "notice_period"),
        MakeBelief("auto_renewal",  1.0,             ClaimKey: "auto_renewal"),
        MakeBelief("sla_uptime",    0.995,           ClaimKey: "sla_uptime"),
        MakeBelief("renewal_intent",0.65,            ClaimKey: "renewal_intent"),
    ];

    private static Belief MakeBelief(string criterion, double value, string ClaimKey) =>
        new(Id: Guid.NewGuid(), EntityId: NorthstarVendorId,
            Dimension: null, Criterion: criterion,
            Value: value, SourceTier: SourceTier.Primary,
            Confidence: 0.0, Freshness: 1.0,
            Derivation: $"vendor-file:{ClaimKey}",
            SourceSignals: [], Version: 1, SupersededBy: null,
            CreatedAt: Now.AddDays(-180), TraceId: Guid.NewGuid())
        {
            ClaimKey = ClaimKey
        };

    private static MailArtifact MakeEmail(string id, string convId, string subject,
        string sender, DateTimeOffset sent) =>
        new(ArtifactId: Guid.NewGuid(), SourceSystem: "fixture", SourceType: "fixture_email",
            TenantId: "fixture", SourcePrincipalId: "user-1",
            ExternalId: id, ConversationId: convId, Subject: subject,
            Sender: sender, Recipients: ["pm@econtracts.onmicrosoft.com"],
            BodyPreview: $"Body of {subject}",
            SentAtUtc: sent, CapturedAtUtc: sent);

    private static VendorCallBriefingContext MakeContext(
        IReadOnlyList<Evidence>?    contracts         = null,
        IReadOnlyList<Evidence>?    openCommitments   = null,
        IReadOnlyList<Evidence>?    commercialSignals = null,
        IReadOnlyList<Evidence>?    priorNotes        = null,
        IReadOnlyList<MailArtifact>?emails            = null,
        IReadOnlyList<Belief>?      beliefs           = null,
        IReadOnlyList<string>?      gaps              = null) =>
        new(
            Meeting:                 DefaultMeeting(),
            VendorName:              "Northstar Software",
            VendorId:                NorthstarVendorId,
            Now:                     Now,
            Recipe:                  DefaultRecipe(),
            RecentEmails:            emails            ?? [],
            Contracts:               contracts         ?? [ContractCurrent()],
            PriorMeetingNotes:       priorNotes        ?? [],
            OpenCommitments:         openCommitments   ?? [],
            CommercialSignals:       commercialSignals ?? [],
            EvidenceGaps:            gaps              ?? [],
            CurrentBeliefs:          beliefs           ?? DefaultBeliefs(),
            PreMeetingCheckInResult: null);

    private static VendorCallBriefingComposer Composer() => new();

    // ── Full Northstar bundle ─────────────────────────────────────────────────

    [Fact]
    public async Task Compose_NorthstarBundle_AllSectionsPopulated()
    {
        var ctx = MakeContext(
            contracts:         [ContractCurrent(), ContractPrior()],
            openCommitments:   [CommitmentSlaReport(), CommitmentCounterProposal(), CommitmentNoticeDeadline()],
            commercialSignals: [CommercialSignalEmail()],
            priorNotes:        [],
            emails:            [MakeEmail("e1", "c1", "Renewal pricing", "alex@northstarsoftware.com", Now.AddDays(-7))]);

        var briefing = await Composer().ComposeAsync(ctx);

        Assert.Equal("Northstar Software", briefing.VendorName);
        Assert.NotEmpty(briefing.MeetingObjective.Content);
        Assert.NotEmpty(briefing.ContractPosition.Content);
        Assert.NotEmpty(briefing.RecentDevelopments.Content);
        Assert.NotEmpty(briefing.OpenCommitments.Content);
        Assert.NotEmpty(briefing.RisksAndOpportunities.Content);
        Assert.NotEmpty(briefing.EvidenceGaps.Content);
        Assert.NotEmpty(briefing.RecommendedQuestions.Content);
        Assert.NotEmpty(briefing.SafestNextAction.Content);
    }

    // ── ContractPosition ──────────────────────────────────────────────────────

    [Fact]
    public async Task ContractPosition_IncludesRenewalDateAndAnnualValue()
    {
        var briefing = await Composer().ComposeAsync(MakeContext());

        Assert.Contains("285,000", briefing.ContractPosition.Content);
        Assert.Contains("2026-09-28", briefing.ContractPosition.Content);
    }

    [Fact]
    public async Task ContractPosition_NoticeDeadlineWarning_WhenWithin30Days()
    {
        // Renewal 2026-09-28, notice 60d → deadline 2026-07-30 (14d from now)
        var briefing = await Composer().ComposeAsync(MakeContext());

        Assert.Contains("⚠", briefing.ContractPosition.Content);
        Assert.Contains("2026-07-30", briefing.ContractPosition.Content);
    }

    [Fact]
    public async Task ContractPosition_NoWarning_WhenDeadlineFarAway()
    {
        // Renewal > 90 days → deadline > 30 days → no warning
        var farBeliefs = new List<Belief>
        {
            MakeBelief("annual_value",  285000.0, "annual_value"),
            MakeBelief("renewal_date",
                ((DateTimeOffset)new DateTimeOffset(2027, 5, 1, 0, 0, 0, TimeSpan.Zero)).ToUnixTimeSeconds(),
                "renewal_date"),
            MakeBelief("notice_period", 60.0, "notice_period"),
        };
        var briefing = await Composer().ComposeAsync(MakeContext(beliefs: farBeliefs));

        Assert.DoesNotContain("⚠", briefing.ContractPosition.Content);
    }

    [Fact]
    public async Task ContractPosition_NoContracts_SaysNoneOnFile()
    {
        var briefing = await Composer().ComposeAsync(MakeContext(contracts: []));

        Assert.Contains("No signed contract", briefing.ContractPosition.Content);
    }

    // ── RecentDevelopments ────────────────────────────────────────────────────

    [Fact]
    public async Task RecentDevelopments_GroupsEmailsByConversationId()
    {
        var emails = new[]
        {
            MakeEmail("e1", "conv-pricing", "Renewal pricing",  "alex@northstarsoftware.com", Now.AddDays(-7)),
            MakeEmail("e2", "conv-pricing", "RE: Pricing",      "pm@econtracts.onmicrosoft.com", Now.AddDays(-6)),
            MakeEmail("e3", "conv-sla",     "SLA report",       "alex@northstarsoftware.com", Now.AddDays(-5)),
        };

        var briefing = await Composer().ComposeAsync(MakeContext(emails: emails));
        var content  = briefing.RecentDevelopments.Content;

        // Two threads: pricing (2 messages) and sla (1 message)
        Assert.Contains("2 message(s)", content);
        Assert.Contains("1 message(s)", content);
    }

    [Fact]
    public async Task RecentDevelopments_NoEmails_SaysNoneFound()
    {
        var briefing = await Composer().ComposeAsync(MakeContext(emails: []));

        Assert.Contains("No recent commercial emails", briefing.RecentDevelopments.Content);
    }

    // ── OpenCommitments ───────────────────────────────────────────────────────

    [Fact]
    public async Task OpenCommitments_OverdueItems_FlaggedWithWarning()
    {
        var briefing = await Composer().ComposeAsync(MakeContext(
            openCommitments: [CommitmentSlaReport()]));  // 5 days old → overdue

        Assert.Contains("⚠", briefing.OpenCommitments.Content);
        Assert.Contains("OVERDUE", briefing.OpenCommitments.Content);
    }

    [Fact]
    public async Task OpenCommitments_RecentItems_NotFlagged()
    {
        var briefing = await Composer().ComposeAsync(MakeContext(
            openCommitments: [CommitmentCounterProposal()]));  // 3 days → not overdue

        Assert.DoesNotContain("⚠", briefing.OpenCommitments.Content);
        Assert.DoesNotContain("OVERDUE", briefing.OpenCommitments.Content);
    }

    [Fact]
    public async Task OpenCommitments_NoCommitments_SaysNoneRecorded()
    {
        var briefing = await Composer().ComposeAsync(MakeContext(openCommitments: []));

        Assert.Contains("No open commitments", briefing.OpenCommitments.Content);
    }

    // ── RisksAndOpportunities ─────────────────────────────────────────────────

    [Fact]
    public async Task RisksOpps_PricingSignal_GeneratesRisk()
    {
        var briefing = await Composer().ComposeAsync(MakeContext(
            commercialSignals: [CommercialSignalEmail()]));

        Assert.Contains("Risk", briefing.RisksAndOpportunities.Content);
    }

    [Fact]
    public async Task RisksOpps_OverdueCommitment_GeneratesRisk()
    {
        var briefing = await Composer().ComposeAsync(MakeContext(
            openCommitments: [CommitmentSlaReport()]));

        Assert.Contains("Risk: overdue commitment", briefing.RisksAndOpportunities.Content);
    }

    [Fact]
    public async Task RisksOpps_NoticeDeadlineWithin30Days_GeneratesRisk()
    {
        var briefing = await Composer().ComposeAsync(MakeContext());

        Assert.Contains("renewal window narrowing", briefing.RisksAndOpportunities.Content);
    }

    // ── RecommendedQuestions ──────────────────────────────────────────────────

    [Fact]
    public async Task RecommendedQuestions_RespectsMaximumCap()
    {
        var ctx = MakeContext(
            commercialSignals: [CommercialSignalEmail()],
            openCommitments: [
                CommitmentSlaReport(), CommitmentCounterProposal(), CommitmentNoticeDeadline()
            ]);

        var briefing = await Composer().ComposeAsync(ctx);

        // Questions should be numbered 1..N where N <= cap (6)
        var lines = briefing.RecommendedQuestions.Content.Split('\n')
            .Where(l => l.TrimStart().StartsWith("1.") || l.TrimStart().StartsWith("2.") ||
                        l.TrimStart().StartsWith("3.") || l.TrimStart().StartsWith("4.") ||
                        l.TrimStart().StartsWith("5.") || l.TrimStart().StartsWith("6.") ||
                        l.TrimStart().StartsWith("7."))
            .ToList();

        Assert.True(lines.Count <= 6, $"Expected ≤ 6 questions, got {lines.Count}");
    }

    // ── SafestNextAction ──────────────────────────────────────────────────────

    [Fact]
    public async Task SafestNextAction_NoticeDeadlineImminent_ReturnsConfirmRenewal()
    {
        // Notice deadline = 2026-07-30 → 14 days → highest priority
        var briefing = await Composer().ComposeAsync(MakeContext());

        Assert.Contains("2026-07-30", briefing.SafestNextAction.Content);
        Assert.Contains("notice deadline", briefing.SafestNextAction.Content,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SafestNextAction_NoDeadlineNoCommitment_ReturnsDefault()
    {
        var briefing = await Composer().ComposeAsync(MakeContext(
            contracts:  [],
            beliefs:    []));

        Assert.Contains("open items", briefing.SafestNextAction.Content,
            StringComparison.OrdinalIgnoreCase);
    }

    // ── Source references ─────────────────────────────────────────────────────

    [Fact]
    public async Task AllSections_HaveAtLeastOneSourceReference()
    {
        var ctx = MakeContext(
            contracts:         [ContractCurrent()],
            openCommitments:   [CommitmentSlaReport()],
            commercialSignals: [CommercialSignalEmail()]);

        var briefing = await Composer().ComposeAsync(ctx);

        var sections = new[]
        {
            briefing.MeetingObjective,
            briefing.ContractPosition,
            briefing.RecentDevelopments,
            briefing.OpenCommitments,
            briefing.RisksAndOpportunities,
            briefing.EvidenceGaps,
            briefing.RecommendedQuestions,
            briefing.SafestNextAction,
        };

        foreach (var section in sections)
            Assert.True(section.SourceReferences.Count > 0,
                $"Section '{section.Heading}' has no source references");
    }

    [Fact]
    public async Task Citations_AreDeduplicatedAndNumberedSequentially()
    {
        var briefing = await Composer().ComposeAsync(MakeContext(
            contracts: [ContractCurrent()]));

        Assert.True(briefing.Citations.Count > 0);
        var indices = briefing.Citations.Select(c => c.Index).ToList();
        Assert.Equal(Enumerable.Range(1, briefing.Citations.Count), indices);
    }

    [Fact]
    public async Task Citations_NoDuplicateSourceIds()
    {
        var briefing = await Composer().ComposeAsync(MakeContext(
            contracts:         [ContractCurrent()],
            openCommitments:   [CommitmentSlaReport()],
            commercialSignals: [CommercialSignalEmail()]));

        var sourceIds = briefing.Citations.Select(c => c.SourceId).ToList();
        Assert.Equal(sourceIds.Distinct().Count(), sourceIds.Count);
    }

    // ── Empty bundle ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyBundle_ProducesBriefingWithoutErrors()
    {
        var ctx = MakeContext(
            contracts:         [],
            openCommitments:   [],
            commercialSignals: [],
            priorNotes:        [],
            emails:            [],
            beliefs:           [],
            gaps:              []);

        var exception = await Record.ExceptionAsync(() => Composer().ComposeAsync(ctx));
        Assert.Null(exception);
    }

    [Fact]
    public async Task EmptyBundle_GapFocusedContent_NoContracts()
    {
        var ctx = MakeContext(contracts: [], beliefs: []);

        var briefing = await Composer().ComposeAsync(ctx);
        Assert.Contains("No signed contract", briefing.ContractPosition.Content);
    }

    // ── LLM fallback ──────────────────────────────────────────────────────────

    [Fact]
    public async Task LlmFallback_WhenNullLlm_ProducesModeAOutput()
    {
        var composer = new VendorCallBriefingComposer(llm: null);
        var briefing = await composer.ComposeAsync(MakeContext());

        // Should still produce a full briefing without errors
        Assert.NotEmpty(briefing.ContractPosition.Content);
        Assert.NotEmpty(briefing.Citations);
    }

    [Fact]
    public async Task LlmFallback_WhenLlmThrows_FallsBackToModeA()
    {
        var throwingLlm = new ThrowingLlm();
        var composer    = new VendorCallBriefingComposer(llm: throwingLlm);
        var briefing    = await composer.ComposeAsync(MakeContext());

        // Should still produce a complete briefing
        Assert.NotEmpty(briefing.ContractPosition.Content);
        Assert.Contains("285,000", briefing.ContractPosition.Content);
    }
}

// ── Test double ───────────────────────────────────────────────────────────────

internal sealed class ThrowingLlm : Kozmo.Llm.IKozmoLlm
{
    public Task<Kozmo.Llm.LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
        => throw new Kozmo.Llm.LlmCacheMissException("test-key");
}
