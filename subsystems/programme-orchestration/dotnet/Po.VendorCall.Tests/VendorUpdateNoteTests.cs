using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

/// <summary>
/// Tests for VendorUpdateNote, InMemoryVendorUpdateNoteStore, and the integration
/// of update notes into Q2FactAssembler output.
/// </summary>
public sealed class VendorUpdateNoteTests
{
    private static readonly Guid VendorA = Guid.Parse("aa000001-0000-0000-0000-000000000001");
    private static readonly Guid VendorB = Guid.Parse("bb000001-0000-0000-0000-000000000001");

    private static VendorUpdateNote MakeNote(
        Guid? vendorId = null, string text = "Test note",
        DateTimeOffset? at = null, Guid? runId = null) =>
        new VendorUpdateNote(
            Id:              Guid.NewGuid(),
            VendorId:        vendorId ?? VendorA,
            VendorCallRunId: runId,
            NoteText:        text,
            SubmittedByUpn:  "user@example.com",
            SubmittedAtUtc:  at ?? DateTimeOffset.UtcNow);

    // ── InMemoryVendorUpdateNoteStore ─────────────────────────────────────────

    [Fact]
    public async Task InMemory_SaveAndGet_ReturnsNote()
    {
        var store = new InMemoryVendorUpdateNoteStore();
        var note  = MakeNote(vendorId: VendorA, text: "Pricing agreed verbally.");
        await store.SaveAsync(note, default);
        var result = await store.GetForVendorAsync(VendorA, default);
        Assert.Single(result);
        Assert.Equal("Pricing agreed verbally.", result[0].NoteText);
    }

    [Fact]
    public async Task InMemory_GetForVendor_FiltersCorrectly()
    {
        var store = new InMemoryVendorUpdateNoteStore();
        await store.SaveAsync(MakeNote(vendorId: VendorA, text: "Note A"), default);
        await store.SaveAsync(MakeNote(vendorId: VendorB, text: "Note B"), default);

        var forA = await store.GetForVendorAsync(VendorA, default);
        Assert.Single(forA);
        Assert.Equal("Note A", forA[0].NoteText);
    }

    [Fact]
    public async Task InMemory_GetForVendor_OrdersBySubmittedAt()
    {
        var store = new InMemoryVendorUpdateNoteStore();
        var later   = DateTimeOffset.UtcNow;
        var earlier = later.AddMinutes(-30);

        await store.SaveAsync(MakeNote(vendorId: VendorA, text: "Later",   at: later), default);
        await store.SaveAsync(MakeNote(vendorId: VendorA, text: "Earlier", at: earlier), default);

        var result = await store.GetForVendorAsync(VendorA, default);
        Assert.Equal(2, result.Count);
        Assert.Equal("Earlier", result[0].NoteText);
        Assert.Equal("Later",   result[1].NoteText);
    }

    [Fact]
    public async Task InMemory_GetForVendor_NoNotes_ReturnsEmpty()
    {
        var store  = new InMemoryVendorUpdateNoteStore();
        var result = await store.GetForVendorAsync(VendorA, default);
        Assert.Empty(result);
    }

    [Fact]
    public async Task InMemory_MultipleNotes_SameVendor_ReturnAll()
    {
        var store = new InMemoryVendorUpdateNoteStore();
        await store.SaveAsync(MakeNote(vendorId: VendorA, text: "Note 1"), default);
        await store.SaveAsync(MakeNote(vendorId: VendorA, text: "Note 2"), default);
        await store.SaveAsync(MakeNote(vendorId: VendorA, text: "Note 3"), default);

        var result = await store.GetForVendorAsync(VendorA, default);
        Assert.Equal(3, result.Count);
    }

    // ── VendorUpdateNote record ───────────────────────────────────────────────

    [Fact]
    public void Note_Properties_RoundTrip()
    {
        var id    = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var at    = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        var note  = new VendorUpdateNote(id, VendorA, runId, "Test note", "user@example.com", at);

        Assert.Equal(id,              note.Id);
        Assert.Equal(VendorA,         note.VendorId);
        Assert.Equal(runId,           note.VendorCallRunId);
        Assert.Equal("Test note",     note.NoteText);
        Assert.Equal("user@example.com", note.SubmittedByUpn);
        Assert.Equal(at,              note.SubmittedAtUtc);
    }

    [Fact]
    public void Note_NullRunId_IsAccepted()
    {
        var note = new VendorUpdateNote(Guid.NewGuid(), VendorA, null, "No run", "u@x.com", DateTimeOffset.UtcNow);
        Assert.Null(note.VendorCallRunId);
    }

    // ── Q2FactAssembler integration ───────────────────────────────────────────

    [Fact]
    public void Q2Assembler_WithUpdateNotes_IncludesNoteInPacket()
    {
        var assembler = new Q2FactAssembler();
        var bundle    = MakeEmptyBundle();
        var note      = MakeNote(vendorId: VendorA, text: "Verbally agreed 5% cap");
        var at        = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
        var noteWithAt = note with { SubmittedAtUtc = at };

        var packet = assembler.Assemble(bundle, [], null, DateTimeOffset.UtcNow, [noteWithAt]);

        Assert.Single(packet.UpdateNotes);
        Assert.Contains("2026-07-10", packet.UpdateNotes[0]);
        Assert.Contains("Verbally agreed 5% cap", packet.UpdateNotes[0]);
    }

    [Fact]
    public void Q2Assembler_NoUpdateNotes_UpdateNotesEmpty()
    {
        var assembler = new Q2FactAssembler();
        var bundle    = MakeEmptyBundle();
        var packet    = assembler.Assemble(bundle, [], null, DateTimeOffset.UtcNow);
        Assert.Empty(packet.UpdateNotes);
    }

    [Fact]
    public void Q2Assembler_MultipleUpdateNotes_AllIncluded()
    {
        var assembler = new Q2FactAssembler();
        var bundle    = MakeEmptyBundle();
        var notes     = new[]
        {
            MakeNote(text: "Note 1") with { SubmittedAtUtc = DateTimeOffset.UtcNow.AddDays(-2) },
            MakeNote(text: "Note 2") with { SubmittedAtUtc = DateTimeOffset.UtcNow.AddDays(-1) },
        };

        var packet = assembler.Assemble(bundle, [], null, DateTimeOffset.UtcNow, notes);
        Assert.Equal(2, packet.UpdateNotes.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static VendorCallEvidenceBundle MakeEmptyBundle() =>
        new VendorCallEvidenceBundle(
            RecentEmails:      [],
            FilteredNoiseEmails: [],
            Contracts:         [],
            PriorMeetingNotes: [],
            OpenCommitments:   [],
            CommercialSignals: [],
            EvidenceGaps:      []);
}
