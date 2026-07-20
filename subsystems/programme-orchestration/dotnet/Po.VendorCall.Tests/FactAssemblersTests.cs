using Kozmo.Contracts;
using If.Contracts;
using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

/// <summary>
/// Unit tests for all five fact-assemblers (Q1–Q5) in a single file,
/// matching the FactAssemblers.cs consolidation pattern.
/// Northstar scenario values: VendorId=dd000001, renewal_date=2026-09-28 (unix 1790553600),
/// notice_period=60 days → notice deadline=2026-07-30, ACV=£285,000.
/// </summary>
public static class FactAssemblersTestFixtures
{
    public static readonly Guid NorthstarVendorId =
        Guid.Parse("dd000001-0000-0000-0000-000000000001");

    // Today: 2026-07-16 → 14 days until notice deadline (2026-07-30)
    public static readonly DateTimeOffset Now =
        new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    // renewal_date = 2026-09-28 as Unix timestamp
    public static readonly long RenewalDateUnix = 1790553600L;

    public static Belief MakeBelief(string claimKey, double value) =>
        new Belief(
            Id:            Guid.NewGuid(),
            EntityId:      NorthstarVendorId,
            Dimension:     null,
            Criterion:     claimKey,
            Value:         value,
            SourceTier:    SourceTier.Verified,
            Confidence:    0.90,
            Freshness:     1.0,
            Derivation:    "rule",
            SourceSignals: [],
            Version:       1,
            SupersededBy:  null,
            CreatedAt:     Now.AddDays(-30),
            TraceId:       Guid.NewGuid())
        {
            ClaimKey = claimKey,
        };

    public static IReadOnlyList<Belief> NorthstarBeliefs() =>
    [
        MakeBelief("renewal_date",   RenewalDateUnix),  // 2026-09-28
        MakeBelief("notice_period",  60),               // 60-day clause
        MakeBelief("annual_value",   285000),           // £285,000
        MakeBelief("renewal_intent", 0.65),             // below 0.70 threshold
    ];

    public static Evidence MakeEvidence(
        Guid? id = null,
        DocType docType = DocType.OwnerNote,
        DateTimeOffset? ingestedAt = null,
        string? refPath = null) =>
        new Evidence(
            EvidenceId: id ?? Guid.NewGuid(),
            VendorId:   NorthstarVendorId,
            DocType:    docType,
            SourceTier: SourceTier.Reported,
            Ref:        refPath ?? "notes/northstar-commitment-sla-report-request-2026-07-10.txt",
            DocVersion: 1,
            IngestedAt: ingestedAt ?? Now.AddDays(-10));

    public static VendorCallEvidenceBundle NorthstarBundle()
    {
        var contract   = MakeEvidence(docType: DocType.SignedContract,
                                       refPath: "contracts/northstar-msa-2024-09-28.pdf",
                                       ingestedAt: Now.AddDays(-300));
        // Overdue commitment: ingested 10 days ago (> 4-day stale threshold)
        var commitment = MakeEvidence(docType: DocType.OwnerNote,
                                       refPath: "notes/northstar-commitment-sla-report-request-2026-07-06.txt",
                                       ingestedAt: Now.AddDays(-10));
        // Commercial signal: pricing uplift email
        var signal     = MakeEvidence(docType: DocType.Email,
                                       refPath: "emails/northstar-pricing-uplift-2026-06-15.eml",
                                       ingestedAt: Now.AddDays(-31));

        var overdueGap = $"Overdue open commitment: {commitment.Ref} (age 10 days, threshold 4 days).";

        return new VendorCallEvidenceBundle(
            RecentEmails:        [],
            FilteredNoiseEmails: [],
            Contracts:           [contract],
            PriorMeetingNotes:   [],
            OpenCommitments:     [commitment],
            CommercialSignals:   [signal],
            EvidenceGaps:        [overdueGap]);
    }

    public static VendorCallEvidenceBundle EmptyBundle() =>
        new([], [], [], [], [], [], []);

    public static ReviewCheckpoint MakePreviousCheckpoint(
        int openCount = 3, int overdueCount = 2) =>
        new ReviewCheckpoint(
            Id:                     Guid.NewGuid(),
            VendorId:               NorthstarVendorId,
            VendorCallRunId:        null,
            Kind:                   CheckpointKind.PreMeeting,
            CreatedAtUtc:           Now.AddDays(-7),
            Status:                 ReviewStatus.Amber,
            Movement:               ReviewMovement.Stable,
            Confidence:             ReviewConfidence.Medium,
            Q1Answer:               "Previous Q1 answer from last review.",
            Q2Answer:               "Previous Q2 answer.",
            Q3Answer:               "Previous Q3 answer.",
            Q4Answer:               "Previous Q4 answer.",
            Q5Answer:               "Previous Q5 answer.",
            OpenCommitmentCount:    openCount,
            OverdueCommitmentCount: overdueCount,
            UnresolvedSignalCount:  1,
            SourceReferenceIds:     []);
}

// ==========================================================
// Q1 — What are we trying to accomplish?
// ==========================================================

public sealed class Q1FactAssemblerTests
{
    private static readonly IQ1FactAssembler Assembler = new Q1FactAssembler();

    [Fact]
    public void Q1_Northstar_PopulatesRenewalDeadlineAndPhrase()
    {
        var packet = Assembler.Assemble(
            bundle:             FactAssemblersTestFixtures.NorthstarBundle(),
            beliefs:            FactAssemblersTestFixtures.NorthstarBeliefs(),
            previousCheckpoint: null,
            vendorName:         "Northstar Software",
            eventTypeCode:      "vendor_review",
            today:              FactAssemblersTestFixtures.Now);

        Assert.Equal("Northstar Software",     packet.VendorName);
        Assert.Equal("quarterly vendor review", packet.MeetingTypePhrase);
        Assert.Equal("2026-09-28",             packet.RenewalDate);
        Assert.Equal("2026-07-30",             packet.NoticeDeadline);
        Assert.Equal(13,                        packet.DaysUntilDeadline);
        Assert.Null(packet.PreviousAnswer);
        Assert.True(packet.IsFirstReview);
        Assert.NotEmpty(packet.SourceReferenceIds);
    }

    [Fact]
    public void Q1_PreviousCheckpoint_SetsPreviousAnswerAndNotFirstReview()
    {
        var prev   = FactAssemblersTestFixtures.MakePreviousCheckpoint();
        var packet = Assembler.Assemble(
            bundle:             FactAssemblersTestFixtures.NorthstarBundle(),
            beliefs:            FactAssemblersTestFixtures.NorthstarBeliefs(),
            previousCheckpoint: prev,
            vendorName:         "Northstar Software",
            eventTypeCode:      "renewal_discussion",
            today:              FactAssemblersTestFixtures.Now);

        Assert.Equal("renewal discussion", packet.MeetingTypePhrase);
        Assert.Equal("Previous Q1 answer from last review.", packet.PreviousAnswer);
        Assert.False(packet.IsFirstReview);
    }

    [Fact]
    public void Q1_EmptyBundle_NoBeliefs_NullDeadlineFields()
    {
        var packet = Assembler.Assemble(
            bundle:             FactAssemblersTestFixtures.EmptyBundle(),
            beliefs:            [],
            previousCheckpoint: null,
            vendorName:         "Unknown Vendor",
            eventTypeCode:      "unknown_type",
            today:              FactAssemblersTestFixtures.Now);

        Assert.Null(packet.RenewalDate);
        Assert.Null(packet.NoticeDeadline);
        Assert.Null(packet.DaysUntilDeadline);
        Assert.Equal("vendor meeting", packet.MeetingTypePhrase);
        Assert.True(packet.IsFirstReview);
        Assert.Empty(packet.SourceReferenceIds);
    }
}

// ==========================================================
// Q2 — What is our current / contemplated position?
// ==========================================================

public sealed class Q2FactAssemblerTests
{
    private static readonly IQ2FactAssembler Assembler = new Q2FactAssembler();

    [Fact]
    public void Q2_Northstar_PopulatesContractAndCommitmentAndSignal()
    {
        var packet = Assembler.Assemble(
            bundle:             FactAssemblersTestFixtures.NorthstarBundle(),
            beliefs:            FactAssemblersTestFixtures.NorthstarBeliefs(),
            previousCheckpoint: null,
            now:                FactAssemblersTestFixtures.Now);

        // Contract
        Assert.Single(packet.Contracts);
        var contract = packet.Contracts[0];
        Assert.Equal("SignedContract",          contract.Type);
        Assert.Equal(285000m,                   contract.AnnualValue);
        Assert.Equal("GBP",                     contract.Currency);
        Assert.Equal("2026-09-28",              contract.RenewalDate);
        Assert.Equal(60,                        contract.NoticePeriodDays);

        // Commitment — 10 days ingested, overdue (threshold 4)
        Assert.Single(packet.OpenCommitments);
        var commitment = packet.OpenCommitments[0];
        Assert.True(commitment.IsOverdue);
        Assert.Equal(6, commitment.OverdueDays); // 10 - 4 = 6

        // Signal
        Assert.Single(packet.CommercialSignals);

        // First review
        Assert.True(packet.IsFirstReview);
        Assert.NotEmpty(packet.SourceReferenceIds);
    }

    [Fact]
    public void Q2_EmptyBundle_ReturnsEmptyLists_IsFirstReview()
    {
        var packet = Assembler.Assemble(
            bundle:             FactAssemblersTestFixtures.EmptyBundle(),
            beliefs:            [],
            previousCheckpoint: null,
            now:                FactAssemblersTestFixtures.Now);

        Assert.Empty(packet.Contracts);
        Assert.Empty(packet.OpenCommitments);
        Assert.Empty(packet.CommercialSignals);
        Assert.True(packet.IsFirstReview);
        Assert.Empty(packet.SourceReferenceIds);
    }

    [Fact]
    public void Q2_WithPreviousCheckpoint_SetsNotFirstReview()
    {
        var prev   = FactAssemblersTestFixtures.MakePreviousCheckpoint();
        var packet = Assembler.Assemble(
            bundle:             FactAssemblersTestFixtures.EmptyBundle(),
            beliefs:            [],
            previousCheckpoint: prev,
            now:                FactAssemblersTestFixtures.Now);

        Assert.False(packet.IsFirstReview);
        Assert.Equal("Previous Q2 answer.", packet.PreviousAnswer);
    }
}

// ==========================================================
// Q3 — What is helping, preventing, or changing progress?
// ==========================================================

public sealed class Q3FactAssemblerTests
{
    private static readonly IQ3FactAssembler Assembler = new Q3FactAssembler();

    [Fact]
    public void Q3_Northstar_HasPreventingFacts()
    {
        var packet = Assembler.Assemble(
            bundle:             FactAssemblersTestFixtures.NorthstarBundle(),
            beliefs:            FactAssemblersTestFixtures.NorthstarBeliefs(),
            previousCheckpoint: null,
            now:                FactAssemblersTestFixtures.Now);

        // Overdue commitment → preventing
        Assert.NotEmpty(packet.PreventingFacts);
        Assert.Contains(packet.PreventingFacts, f => f.Contains("past stale threshold"));

        // Notice deadline ≤ 30 days → preventing
        Assert.Contains(packet.PreventingFacts, f => f.Contains("notice deadline") && f.Contains("13 day"));

        // Commercial signal → preventing
        Assert.Contains(packet.PreventingFacts, f => f.Contains("Unaddressed commercial signal"));
    }

    [Fact]
    public void Q3_Northstar_WithPreviousCheckpoint_HasChangingFacts()
    {
        // Previous: 3 open, 2 overdue. Current: 1 open, 1 overdue → counts changed
        var prev   = FactAssemblersTestFixtures.MakePreviousCheckpoint(openCount: 3, overdueCount: 2);
        var packet = Assembler.Assemble(
            bundle:             FactAssemblersTestFixtures.NorthstarBundle(), // 1 open, 1 overdue
            beliefs:            FactAssemblersTestFixtures.NorthstarBeliefs(),
            previousCheckpoint: prev,
            now:                FactAssemblersTestFixtures.Now);

        Assert.Contains(packet.HelpingFacts, f => f.Contains("resolved since last review"));
        Assert.NotEmpty(packet.ChangingFacts);
    }

    [Fact]
    public void Q3_EmptyBundle_NoIssues_HasHelpingFact()
    {
        var packet = Assembler.Assemble(
            bundle:             FactAssemblersTestFixtures.EmptyBundle(),
            beliefs:            [],
            previousCheckpoint: null,
            now:                FactAssemblersTestFixtures.Now);

        Assert.Contains(packet.HelpingFacts,
            f => f.Contains("No open or overdue commitments"));
        Assert.Empty(packet.PreventingFacts);
        Assert.Empty(packet.ChangingFacts);
    }
}

// ==========================================================
// Q4 — What matters most now?
// ==========================================================

public sealed class Q4FactAssemblerTests
{
    private static readonly IQ4FactAssembler Assembler = new Q4FactAssembler();

    [Fact]
    public void Q4_Northstar_RanksOverdueThenSignalThenDeadline()
    {
        var packet = Assembler.Assemble(
            bundle:       FactAssemblersTestFixtures.NorthstarBundle(),
            beliefs:      FactAssemblersTestFixtures.NorthstarBeliefs(),
            now:          FactAssemblersTestFixtures.Now,
            maxPriorities: 3);

        Assert.Equal(3, packet.TopPriorities.Count);

        // Overdue commitment is rank 1
        Assert.Contains("Resolve overdue commitment", packet.TopPriorities[0].Description);

        // Commercial signal is rank 2
        Assert.Contains("Respond to commercial signal", packet.TopPriorities[1].Description);

        // Renewal deadline rank 3 (DaysUntilDeadline=14 ≤ 30)
        Assert.Contains("Confirm renewal position", packet.TopPriorities[2].Description);
        Assert.NotEmpty(packet.SourceReferenceIds);
    }

    [Fact]
    public void Q4_RespectsMaxPriorities()
    {
        var packet = Assembler.Assemble(
            bundle:        FactAssemblersTestFixtures.NorthstarBundle(),
            beliefs:       FactAssemblersTestFixtures.NorthstarBeliefs(),
            now:           FactAssemblersTestFixtures.Now,
            maxPriorities: 1);

        Assert.Single(packet.TopPriorities);
    }

    [Fact]
    public void Q4_EmptyBundle_NoBeliefs_EmptyPriorities()
    {
        var packet = Assembler.Assemble(
            bundle:        FactAssemblersTestFixtures.EmptyBundle(),
            beliefs:       [],
            now:           FactAssemblersTestFixtures.Now,
            maxPriorities: 3);

        Assert.Empty(packet.TopPriorities);
    }

    [Fact]
    public void Q4_DeadlineBeyond30Days_NotIncluded()
    {
        // Shift today to 35 days before notice deadline (2026-06-25)
        var earlyNow = FactAssemblersTestFixtures.Now.AddDays(-21); // 35 days before 2026-07-30
        var packet   = Assembler.Assemble(
            bundle:        FactAssemblersTestFixtures.EmptyBundle(),
            beliefs:       FactAssemblersTestFixtures.NorthstarBeliefs(),
            now:           earlyNow,
            maxPriorities: 3);

        Assert.DoesNotContain(packet.TopPriorities,
            p => p.Description.Contains("renewal position"));
    }
}

// ==========================================================
// Q5 — What should happen next?
// ==========================================================

public sealed class Q5FactAssemblerTests
{
    private static readonly IQ5FactAssembler Assembler = new Q5FactAssembler();

    [Fact]
    public void Q5_Northstar_ProducesRenewalActionWithOwner()
    {
        var q4 = new Q4FactAssembler().Assemble(
            bundle:        FactAssemblersTestFixtures.NorthstarBundle(),
            beliefs:       FactAssemblersTestFixtures.NorthstarBeliefs(),
            now:           FactAssemblersTestFixtures.Now,
            maxPriorities: 3);

        var packet = Assembler.Assemble(
            bundle:   FactAssemblersTestFixtures.NorthstarBundle(),
            q4Packet: q4,
            ownerUpn: "rishi@econtracts.onmicrosoft.com",
            beliefs:  FactAssemblersTestFixtures.NorthstarBeliefs(),
            now:      FactAssemblersTestFixtures.Now);

        Assert.NotEmpty(packet.RecommendedActions);

        // Renewal action — the single allowed synthetic action
        var renewalAction = packet.RecommendedActions
            .FirstOrDefault(a => a.Action.Contains("Confirm renewal"));
        Assert.NotNull(renewalAction);
        Assert.Equal("rishi@econtracts.onmicrosoft.com", renewalAction.Owner);
        Assert.Equal("2026-07-30", renewalAction.DueDate);
        Assert.Contains("auto-renewal", renewalAction.Effect);
    }

    [Fact]
    public void Q5_EmptyQ4_ProducesNoActions()
    {
        var emptyQ4 = new Q4FactPacket([], []);
        var packet  = Assembler.Assemble(
            bundle:   FactAssemblersTestFixtures.EmptyBundle(),
            q4Packet: emptyQ4,
            ownerUpn: "user@test.com",
            beliefs:  [],
            now:      FactAssemblersTestFixtures.Now);

        Assert.Empty(packet.RecommendedActions);
        Assert.Empty(packet.SourceReferenceIds);
    }

    [Fact]
    public void Q5_CommitmentPriority_ProducesChasAction()
    {
        // Build a Q4 packet with only an overdue commitment priority (no renewal deadline)
        var bundle      = FactAssemblersTestFixtures.NorthstarBundle();
        var commitment  = bundle.OpenCommitments[0];
        var commitmentQ4 = new Q4FactPacket(
            TopPriorities:
            [
                new RankedPriority(
                    Description:   $"Resolve overdue commitment: sla report request",
                    UrgencyReason: "6 days past threshold.",
                    SourceId:      commitment.EvidenceId.ToString())
            ],
            SourceReferenceIds: [commitment.EvidenceId.ToString()]);

        var packet = Assembler.Assemble(
            bundle:   bundle,
            q4Packet: commitmentQ4,
            ownerUpn: "user@test.com",
            beliefs:  [],
            now:      FactAssemblersTestFixtures.Now);

        Assert.Single(packet.RecommendedActions);
        Assert.Contains("Chase resolution", packet.RecommendedActions[0].Action);
    }
}
