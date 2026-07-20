using Kozmo.Contracts;
using Kozmo.Contracts.Interfaces;
using If.Contracts;
using Po.VendorCall;
using Wc.Contracts;
using Xunit;

namespace Po.VendorCall.Tests;

public sealed class EvidenceCollectorTests
{
    // ── Shared fixtures ───────────────────────────────────────────────────────

    private static readonly Guid VendorId   = Guid.Parse("dd000001-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private static VendorCallContext MakeContext(
        IReadOnlyList<string>? vendorDomains = null,
        int emailLookbackDays = 90,
        int maxEmails = 30)
    {
        var recipe = new VendorCallRecipe(
            RecipeId: "test", Version: "1.0", MeetingType: "vendor_call",
            CalendarWindowDays: 14, EmailLookbackDays: emailLookbackDays,
            PreMeetingCheckInHours: 24, BriefingOffsetMinutes: 10,
            PostMeetingCheckInMinutes: 15,
            Sections: [],
            Limits: new RecipeLimits(
                MaximumEmails: maxEmails,
                MaximumCheckInsPerMeeting: 2,
                MaximumQuestionsInBriefing: 6));

        var meeting = new CalendarArtifact(
            ArtifactId:        Guid.NewGuid(),
            SourceSystem:      "graph",
            SourceType:        "calendar_event",
            TenantId:          "test-tenant",
            SourcePrincipalId: "user-1",
            ExternalId:        "evt-northstar-001",
            ICalUid:           "ical-001",
            Subject:           "Northstar renewal review",
            StartUtc:          Now.AddDays(1),
            EndUtc:            Now.AddDays(1).AddHours(1),
            Organizer:         "rishi@econtracts.onmicrosoft.com",
            Attendees:         ["rishi@econtracts.onmicrosoft.com", "alex.hamilton@northstarsoftware.com"],
            BodyPreview:       "Annual renewal review",
            CapturedAtUtc:     Now);

        var match = new VendorEntityMatchResult(
            VendorId:    VendorId,
            VendorName:  "Northstar Software",
            MatchType:   VendorMatchType.DomainExact,
            MatchScore:  0.95);

        return new VendorCallContext(
            Meeting:                meeting,
            Match:                  match,
            VendorDomains:          vendorDomains ?? ["northstarsoftware.com"],
            SignedInUserPrincipalId: "user-1",
            Recipe:                 recipe);
    }

    private static VendorCallEvidenceCollector MakeCollector(
        IMailSource? mailSource = null,
        IEntityStore? entityStore = null)
        => new(mailSource ?? new StubMailSource([]), entityStore ?? new StubEntityStore());

    // ── Contracts ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Collect_NoEvidence_ContractGapReported()
    {
        var collector = MakeCollector();
        var bundle = await collector.CollectAsync(MakeContext(), Now);

        Assert.Empty(bundle.Contracts);
        Assert.Contains(bundle.EvidenceGaps, g => g.Contains("No signed contract"));
    }

    [Fact]
    public async Task Collect_SignedContract_ClassifiedAsContract()
    {
        var evidenceId = Guid.NewGuid();
        var evidence = new Evidence(
            EvidenceId: evidenceId, VendorId: VendorId,
            DocType: DocType.SignedContract, SourceTier: SourceTier.Primary,
            Ref: "contract-2025.pdf", DocVersion: 1,
            IngestedAt: Now.AddDays(-180));

        var store = new StubEntityStore(evidence: [evidence]);
        var collector = MakeCollector(entityStore: store);
        var bundle = await collector.CollectAsync(MakeContext(), Now);

        var contract = Assert.Single(bundle.Contracts);
        Assert.Equal(evidenceId, contract.EvidenceId);
        Assert.DoesNotContain(bundle.EvidenceGaps, g => g.Contains("No signed contract"));
    }

    // ── OwnerNote classification ───────────────────────────────────────────────

    [Fact]
    public async Task Collect_OwnerNoteWithBelief_ClassifiedAsPriorMeetingNote()
    {
        var evidenceId = Guid.NewGuid();
        var evidence = new Evidence(
            EvidenceId: evidenceId, VendorId: VendorId,
            DocType: DocType.OwnerNote, SourceTier: SourceTier.Reported,
            Ref: "meeting-note-apr-2026.txt", DocVersion: 1,
            IngestedAt: Now.AddDays(-90));

        var belief = new Belief(
            Id: Guid.NewGuid(), EntityId: VendorId,
            Dimension: null, Criterion: "sla_uptime",
            Value: 0.99, SourceTier: SourceTier.Reported,
            Confidence: 0.5, Freshness: 1.0, Derivation: "owner_note",
            SourceSignals: [], Version: 1, SupersededBy: null,
            CreatedAt: Now.AddDays(-90), TraceId: Guid.NewGuid())
        {
            Provenance = new BeliefProvenance(EvidenceId: evidenceId, Locator: "note:1")
        };

        var store = new StubEntityStore(evidence: [evidence], beliefs: [belief]);
        var collector = MakeCollector(entityStore: store);
        var bundle = await collector.CollectAsync(MakeContext(), Now);

        var note = Assert.Single(bundle.PriorMeetingNotes);
        Assert.Equal(evidenceId, note.EvidenceId);
        Assert.Empty(bundle.OpenCommitments);
    }

    [Fact]
    public async Task Collect_OwnerNoteWithoutBelief_ClassifiedAsOpenCommitment()
    {
        var evidenceId = Guid.NewGuid();
        var evidence = new Evidence(
            EvidenceId: evidenceId, VendorId: VendorId,
            DocType: DocType.OwnerNote, SourceTier: SourceTier.Reported,
            Ref: "commitment-q2-sla.txt", DocVersion: 1,
            IngestedAt: Now.AddDays(-1));

        var store = new StubEntityStore(evidence: [evidence]);
        var collector = MakeCollector(entityStore: store);
        var bundle = await collector.CollectAsync(MakeContext(), Now);

        var commitment = Assert.Single(bundle.OpenCommitments);
        Assert.Equal(evidenceId, commitment.EvidenceId);
        Assert.Empty(bundle.PriorMeetingNotes);
    }

    // ── CommercialSignals ─────────────────────────────────────────────────────

    [Fact]
    public async Task Collect_EmailEvidence_ClassifiedAsCommercialSignal()
    {
        var evidenceId = Guid.NewGuid();
        var evidence = new Evidence(
            EvidenceId: evidenceId, VendorId: VendorId,
            DocType: DocType.Email, SourceTier: SourceTier.Correspondence,
            Ref: "pricing-uplift-email.eml", DocVersion: 1,
            IngestedAt: Now.AddDays(-7));

        var store = new StubEntityStore(evidence: [evidence]);
        var collector = MakeCollector(entityStore: store);
        var bundle = await collector.CollectAsync(MakeContext(), Now);

        var signal = Assert.Single(bundle.CommercialSignals);
        Assert.Equal(evidenceId, signal.EvidenceId);
    }

    // ── Email noise partitioning ──────────────────────────────────────────────

    [Fact]
    public async Task Collect_NoisySenders_PartitionedToFilteredNoiseEmails()
    {
        var emails = new List<MailArtifact>
        {
            MakeEmail("northstar-commercial-001", "alex.hamilton@northstarsoftware.com"),
            MakeEmail("northstar-noise-events",   "events@northstarsoftware.com"),
            MakeEmail("northstar-noise-nl",        "newsletter@northstarsoftware.com"),
            MakeEmail("northstar-noise-noreply",   "noreply@northstarsoftware.com"),
        };

        var mailSource = new StubMailSource(emails);
        var collector  = MakeCollector(mailSource: mailSource);
        var bundle     = await collector.CollectAsync(MakeContext(), Now);

        Assert.Single(bundle.RecentEmails);
        Assert.Equal(3, bundle.FilteredNoiseEmails.Count);
        Assert.Equal("alex.hamilton@northstarsoftware.com", bundle.RecentEmails[0].Sender);
    }

    [Fact]
    public async Task Collect_CommercialSenders_AllInRecentEmails()
    {
        var emails = new List<MailArtifact>
        {
            MakeEmail("email-001", "alex.hamilton@northstarsoftware.com"),
            MakeEmail("email-002", "contracts@northstarsoftware.com"),
            MakeEmail("email-003", "daniel@northstarsoftware.com"),
        };

        var mailSource = new StubMailSource(emails);
        var collector  = MakeCollector(mailSource: mailSource);
        var bundle     = await collector.CollectAsync(MakeContext(), Now);

        Assert.Equal(3, bundle.RecentEmails.Count);
        Assert.Empty(bundle.FilteredNoiseEmails);
    }

    // ── Overdue commitment gap ────────────────────────────────────────────────

    [Fact]
    public async Task Collect_OverdueCommitment_AddsGapMessage()
    {
        // IngestedAt 5 days ago → age 5 > StaleDays 4 → overdue
        var evidence = new Evidence(
            EvidenceId: Guid.NewGuid(), VendorId: VendorId,
            DocType: DocType.OwnerNote, SourceTier: SourceTier.Reported,
            Ref: "q2-sla-report-commitment.txt", DocVersion: 1,
            IngestedAt: Now.AddDays(-5));

        var store = new StubEntityStore(evidence: [evidence]);
        var collector = MakeCollector(entityStore: store);
        var bundle = await collector.CollectAsync(MakeContext(), Now);

        Assert.Contains(bundle.EvidenceGaps, g =>
            g.Contains("Overdue open commitment") && g.Contains("q2-sla-report-commitment.txt"));
    }

    [Fact]
    public async Task Collect_RecentCommitment_NoOverdueGap()
    {
        // IngestedAt 2 days ago → age 2 <= StaleDays 4 → not overdue
        var evidence = new Evidence(
            EvidenceId: Guid.NewGuid(), VendorId: VendorId,
            DocType: DocType.OwnerNote, SourceTier: SourceTier.Reported,
            Ref: "recent-commitment.txt", DocVersion: 1,
            IngestedAt: Now.AddDays(-2));

        var store = new StubEntityStore(evidence: [evidence]);
        var collector = MakeCollector(entityStore: store);
        var bundle = await collector.CollectAsync(MakeContext(), Now);

        Assert.DoesNotContain(bundle.EvidenceGaps, g => g.Contains("Overdue open commitment"));
    }

    // ── Renewal-intent gap ────────────────────────────────────────────────────

    [Fact]
    public async Task Collect_NoRenewalIntentBelief_AddsGap()
    {
        var collector = MakeCollector();
        var bundle = await collector.CollectAsync(MakeContext(), Now);

        Assert.Contains(bundle.EvidenceGaps, g => g.Contains("renewal intent"));
    }

    [Fact]
    public async Task Collect_RenewalIntentBeliefPresent_NoGap()
    {
        var belief = new Belief(
            Id: Guid.NewGuid(), EntityId: VendorId,
            Dimension: null, Criterion: "renewal_intent",
            Value: 0.65, SourceTier: SourceTier.Reported,
            Confidence: 0.5, Freshness: 1.0, Derivation: "owner_note",
            SourceSignals: [], Version: 1, SupersededBy: null,
            CreatedAt: Now.AddDays(-30), TraceId: Guid.NewGuid())
        {
            ClaimKey = "renewal_intent"
        };

        var store = new StubEntityStore(beliefs: [belief]);
        var collector = MakeCollector(entityStore: store);
        var bundle = await collector.CollectAsync(MakeContext(), Now);

        Assert.DoesNotContain(bundle.EvidenceGaps, g => g.Contains("renewal intent"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MailArtifact MakeEmail(string messageId, string sender) =>
        new(ArtifactId:        Guid.NewGuid(),
            SourceSystem:      "fixture",
            SourceType:        "fixture_email",
            TenantId:          "fixture",
            SourcePrincipalId: "user-1",
            ExternalId:        messageId,
            ConversationId:    "conv-1",
            Subject:           "Test email",
            Sender:            sender,
            Recipients:        ["procurement@econtracts.onmicrosoft.com"],
            BodyPreview:       "Test body",
            SentAtUtc:         Now.AddDays(-7),
            CapturedAtUtc:     Now.AddDays(-7));
}

// ── Test doubles ──────────────────────────────────────────────────────────────

internal sealed class StubMailSource : IMailSource
{
    private readonly IReadOnlyList<MailArtifact> _emails;

    public StubMailSource(IReadOnlyList<MailArtifact> emails) => _emails = emails;

    public Task<IReadOnlyList<MailArtifact>> FindRelevantMessagesAsync(
        string signedInUserPrincipalId, MailSearchCriteria criteria, CancellationToken ct)
        => Task.FromResult(_emails);
}

internal sealed class StubEntityStore : IEntityStore
{
    private readonly List<Evidence> _evidence;
    private readonly List<Belief>   _beliefs;

    public StubEntityStore(
        IReadOnlyList<Evidence>? evidence = null,
        IReadOnlyList<Belief>?   beliefs  = null)
    {
        _evidence = new List<Evidence>(evidence ?? []);
        _beliefs  = new List<Belief>(beliefs ?? []);
    }

    public Task<IReadOnlyList<Evidence>> GetEvidenceForVendorAsync(Guid vendorId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Evidence>>(_evidence);

    public Task<IReadOnlyList<Belief>> GetCurrentBeliefsAsync(Guid entityId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Belief>>(_beliefs);

    // ── Unsupported members (not needed by collector) ─────────────────────────
    public Task AppendBeliefAsync(Belief belief, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Belief>> GetBeliefHistoryAsync(Guid entityId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SaveIndexAsync(EntityIndex index, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<EntityIndex?> GetIndexAsync(Guid entityId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<EntityIndex>> GetIndexHistoryAsync(Guid entityId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AppendPostureAsync(PostureAssignment posture, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<PostureAssignment?> GetCurrentPostureAsync(Guid entityId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AppendSignalAsync(Signal signal, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Signal?> GetSignalAsync(Guid signalId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<PostureAssignment>> GetPostureHistoryAsync(Guid entityId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Signal>> GetSignalsForEntityAsync(Guid entityId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AppendEvidenceAsync(Evidence evidence, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Evidence?> GetEvidenceAsync(Guid evidenceId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ResetAsync(CancellationToken ct = default) => throw new NotSupportedException();
}
