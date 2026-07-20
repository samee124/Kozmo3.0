using Kozmo.Contracts;
using If.Contracts;
using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

public sealed class ReviewStatusClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid VendorId = Guid.Parse("dd000001-0000-0000-0000-000000000001");

    private static readonly IReviewStatusClassifier Classifier = new ReviewStatusClassifier();

    // ── ClassifyStatus helpers ─────────────────────────────────────────────────

    private static Q1FactPacket MakeQ1(int? daysUntilDeadline = null) =>
        new Q1FactPacket(
            VendorName:          "Test Vendor",
            MeetingTypePhrase:   "vendor meeting",
            RenewalDate:         daysUntilDeadline.HasValue ? "2026-09-28" : null,
            NoticeDeadline:      daysUntilDeadline.HasValue
                                     ? Now.AddDays(daysUntilDeadline.Value).ToString("yyyy-MM-dd")
                                     : null,
            DaysUntilDeadline:   daysUntilDeadline,
            PreviousAnswer:      null,
            IsFirstReview:       true,
            SourceReferenceIds:  []);

    private static VendorCallEvidenceBundle EmptyBundle() =>
        new([], [], [], [], [], [], []);

    private static VendorCallEvidenceBundle BundleWithOverdueCommitment(int overdueDaysAboveThreshold)
    {
        var ageTotal   = 4 + overdueDaysAboveThreshold; // StaleDays=4 + extra
        var ingestedAt = Now.AddDays(-ageTotal);
        var ev         = MakeEvidence(ingestedAt);
        var gapMsg     = $"Overdue open commitment: ref/commitment.txt (age {ageTotal} days, threshold 4 days).";
        return new VendorCallEvidenceBundle([], [], [], [], [ev], [], [gapMsg]);
    }

    private static Evidence MakeEvidence(DateTimeOffset ingestedAt, DocType docType = DocType.OwnerNote) =>
        new Evidence(
            EvidenceId: Guid.NewGuid(),
            VendorId:   VendorId,
            DocType:    docType,
            SourceTier: SourceTier.Reported,
            Ref:        "notes/commitment-test.txt",
            DocVersion: 1,
            IngestedAt: ingestedAt);

    // ── ClassifyStatus — Red ──────────────────────────────────────────────────

    [Fact]
    public void ClassifyStatus_OverdueGe10Days_ReturnsRed()
    {
        var bundle = BundleWithOverdueCommitment(10);
        var status = Classifier.ClassifyStatus(bundle, MakeQ1());
        Assert.Equal(ReviewStatus.Red, status);
    }

    [Fact]
    public void ClassifyStatus_DaysUntilDeadline5_NoContract_ReturnsRed()
    {
        var status = Classifier.ClassifyStatus(EmptyBundle(), MakeQ1(daysUntilDeadline: 5));
        Assert.Equal(ReviewStatus.Red, status);
    }

    // ── ClassifyStatus — Amber ─────────────────────────────────────────────────

    [Fact]
    public void ClassifyStatus_OverdueLt10Days_NoOtherIssues_ReturnsAmber()
    {
        var bundle = BundleWithOverdueCommitment(3); // 3 days past threshold = 7 total
        var status = Classifier.ClassifyStatus(bundle, MakeQ1());
        Assert.Equal(ReviewStatus.Amber, status);
    }

    [Fact]
    public void ClassifyStatus_UnresolvedSignal_ReturnsAmber()
    {
        var signal = MakeEvidence(Now.AddDays(-1), DocType.Email);
        var bundle = new VendorCallEvidenceBundle([], [], [], [], [], [signal], []);
        var status = Classifier.ClassifyStatus(bundle, MakeQ1());
        Assert.Equal(ReviewStatus.Amber, status);
    }

    [Fact]
    public void ClassifyStatus_DaysUntilDeadline20_ReturnsAmber()
    {
        var status = Classifier.ClassifyStatus(EmptyBundle(), MakeQ1(daysUntilDeadline: 20));
        Assert.Equal(ReviewStatus.Amber, status);
    }

    // ── ClassifyStatus — Green ────────────────────────────────────────────────

    [Fact]
    public void ClassifyStatus_NoIssues_ReturnsGreen()
    {
        var status = Classifier.ClassifyStatus(EmptyBundle(), MakeQ1());
        Assert.Equal(ReviewStatus.Green, status);
    }

    // ── ClassifyMovement ──────────────────────────────────────────────────────

    private static ReviewCheckpoint MakePreviousCheckpoint(int openCount, int overdueCount) =>
        new ReviewCheckpoint(
            Id: Guid.NewGuid(), VendorId: VendorId, VendorCallRunId: null,
            Kind: CheckpointKind.PreMeeting, CreatedAtUtc: Now.AddDays(-7),
            Status: ReviewStatus.Amber, Movement: ReviewMovement.Stable,
            Confidence: ReviewConfidence.Medium,
            Q1Answer: "prev", Q2Answer: "", Q3Answer: "", Q4Answer: "", Q5Answer: "",
            OpenCommitmentCount:    openCount,
            OverdueCommitmentCount: overdueCount,
            UnresolvedSignalCount:  0,
            SourceReferenceIds:     []);

    [Fact]
    public void ClassifyMovement_FirstReview_AlwaysStable()
    {
        var movement = Classifier.ClassifyMovement(3, 2, previousCheckpoint: null);
        Assert.Equal(ReviewMovement.Stable, movement);
    }

    [Fact]
    public void ClassifyMovement_OverdueDecreased_OpenSame_ReturnsImproving()
    {
        var prev     = MakePreviousCheckpoint(openCount: 3, overdueCount: 2);
        var movement = Classifier.ClassifyMovement(currentOpenCount: 3, currentOverdueCount: 1, prev);
        Assert.Equal(ReviewMovement.Improving, movement);
    }

    [Fact]
    public void ClassifyMovement_OverdueIncreased_ReturnsWeakening()
    {
        var prev     = MakePreviousCheckpoint(openCount: 2, overdueCount: 1);
        var movement = Classifier.ClassifyMovement(currentOpenCount: 2, currentOverdueCount: 2, prev);
        Assert.Equal(ReviewMovement.Weakening, movement);
    }

    [Fact]
    public void ClassifyMovement_OpenIncreased_ReturnsWeakening()
    {
        var prev     = MakePreviousCheckpoint(openCount: 2, overdueCount: 1);
        var movement = Classifier.ClassifyMovement(currentOpenCount: 3, currentOverdueCount: 1, prev);
        Assert.Equal(ReviewMovement.Weakening, movement);
    }

    [Fact]
    public void ClassifyMovement_SameCounts_ReturnsStable()
    {
        var prev     = MakePreviousCheckpoint(openCount: 2, overdueCount: 1);
        var movement = Classifier.ClassifyMovement(currentOpenCount: 2, currentOverdueCount: 1, prev);
        Assert.Equal(ReviewMovement.Stable, movement);
    }

    // ── ClassifyConfidence ────────────────────────────────────────────────────

    [Fact]
    public void ClassifyConfidence_ContractAndDirectEvidence_ReturnsHigh()
    {
        var contract = MakeEvidence(Now.AddDays(-30), DocType.SignedContract);
        var signal   = MakeEvidence(Now.AddDays(-5),  DocType.Email);
        var bundle   = new VendorCallEvidenceBundle([], [], [contract], [], [], [signal], []);
        Assert.Equal(ReviewConfidence.High, Classifier.ClassifyConfidence(bundle));
    }

    [Fact]
    public void ClassifyConfidence_ContractOnly_ReturnsMedium()
    {
        var contract = MakeEvidence(Now.AddDays(-30), DocType.SignedContract);
        var bundle   = new VendorCallEvidenceBundle([], [], [contract], [], [], [], []);
        Assert.Equal(ReviewConfidence.Medium, Classifier.ClassifyConfidence(bundle));
    }

    [Fact]
    public void ClassifyConfidence_NoContract_ReturnsLow()
    {
        Assert.Equal(ReviewConfidence.Low, Classifier.ClassifyConfidence(EmptyBundle()));
    }
}
