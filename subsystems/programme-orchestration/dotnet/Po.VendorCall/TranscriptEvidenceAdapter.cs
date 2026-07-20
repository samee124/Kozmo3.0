using Kozmo.Contracts;

namespace Po.VendorCall;

public static class TranscriptEvidenceAdapter
{
    /// <summary>
    /// Merges the extraction result into the bundle. Returns a new bundle instance;
    /// the original is not mutated.
    /// </summary>
    public static VendorCallEvidenceBundle Enrich(
        VendorCallEvidenceBundle   bundle,
        TranscriptExtractionResult extraction,
        Guid                       vendorId,
        DateTimeOffset             now)
    {
        var newCommitments  = new List<Evidence>(bundle.OpenCommitments);
        var newSignals      = new List<Evidence>(bundle.CommercialSignals);
        var newNotes        = new List<Evidence>(bundle.PriorMeetingNotes);
        var newGaps         = new List<string>(bundle.EvidenceGaps);

        foreach (var item in extraction.Items)
        {
            if (item.Type == TranscriptItemType.OpenQuestion)
            {
                newGaps.Add(item.Description);
                continue;
            }

            if (item.RequiresUserConfirmation)
            {
                newGaps.Add($"Requires confirmation: {item.Description}");
                continue;
            }

            switch (item.Type)
            {
                case TranscriptItemType.Commitment:
                case TranscriptItemType.NextStep:
                    newCommitments.Add(MakeEvidence(item, vendorId, DocType.OwnerNote, now));
                    break;

                case TranscriptItemType.PricingSignal:
                case TranscriptItemType.ServiceSignal:
                    newSignals.Add(MakeEvidence(item, vendorId, DocType.Communication, now));
                    break;

                case TranscriptItemType.Decision:
                    newNotes.Add(MakeEvidence(item, vendorId, DocType.OwnerNote, now));
                    break;
            }
        }

        foreach (var res in extraction.ResolvedPreBriefItems.Where(r => !r.AddressedInMeeting))
            newGaps.Add($"Not addressed in meeting: {res.PreBriefItem}");

        return bundle with
        {
            OpenCommitments   = newCommitments,
            CommercialSignals = newSignals,
            PriorMeetingNotes = newNotes,
            EvidenceGaps      = newGaps,
        };
    }

    private static Evidence MakeEvidence(
        TranscriptExtractedItem item,
        Guid                    vendorId,
        DocType                 docType,
        DateTimeOffset          now)
        => new(
            EvidenceId: Guid.NewGuid(),
            VendorId:   vendorId,
            DocType:    docType,
            SourceTier: SourceTier.Reported,
            Ref:        SanitizeRef(item.Description),
            DocVersion: 1,
            IngestedAt: now);

    // Replace chars that would confuse LabelFromRef (path ext strip + dash split).
    // With no dashes and no dots the description passes through verbatim.
    public static string SanitizeRef(string description)
        => description.Replace('.', ' ').Replace('-', ' ').Trim();
}
