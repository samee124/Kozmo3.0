using System.Text.Json;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Ii.Intake;

/// <summary>
/// PDF intake lane — replay mode only.
/// In demo runtime, no PDF library or network is touched; the fixture JSON produced
/// offline by seed-prep replays deterministically (same fixture → same ExtractedClaims).
/// Locators: "page:N §A.B", "page:N", "annex:A".
/// </summary>
public sealed class PdfIntakeLane
{
    private readonly SaasProfile _profile;

    public PdfIntakeLane(SaasProfile profile)
    {
        _profile = profile;
    }

    /// <summary>
    /// Replay a pre-recorded PDF fixture.
    /// The fixture was produced offline by extracting claims from the PDF with an LLM extractor
    /// and serialising to the standard evidence fixture format.
    /// </summary>
    public IReadOnlyList<ExtractedClaim> Replay(
        Evidence        evidence,
        string          fixtureJson,
        DateTimeOffset  observedAt)
    {
        var doc = JsonDocument.Parse(fixtureJson);
        var results = new List<ExtractedClaim>();

        if (!doc.RootElement.TryGetProperty("claims", out var claims)) return results;

        foreach (var item in claims.EnumerateArray())
        {
            var claimKey = item.GetProperty("claim_key").GetString() ?? "";
            if (string.IsNullOrEmpty(claimKey)) continue;

            if (!_profile.ClaimKeyCatalogue.TryGetValue(claimKey, out var ckDef)) continue;

            var rawValue      = item.GetProperty("raw_value").GetDouble();
            var extractorConf = item.TryGetProperty("extractor_confidence", out var ec)
                                    ? ec.GetDouble() : 0.95; // PDF extraction slightly uncertain
            var locator       = item.TryGetProperty("locator", out var loc)
                                    ? loc.GetString() ?? "page:1" : "page:1";

            if (!Enum.TryParse<SourceTier>(evidence.SourceTier.ToString(), out var tier))
                tier = evidence.SourceTier;

            // Dimension parse — structural claims get Operational as placeholder, scoring is gated by ckDef.ClaimClass
            Enum.TryParse<Dimension>(ckDef.Dimension, ignoreCase: true, out var dimension);

            var normValue = ckDef.ClaimClass == "structural" ? rawValue : Math.Clamp(rawValue, 0.0, 1.0);

            results.Add(new ExtractedClaim(
                ClaimKey:            claimKey,
                Dimension:           dimension,
                Criterion:           claimKey,
                NormalisedValue:     normValue,
                Tier:                tier,
                ExtractorConfidence: extractorConf,
                ObservedAt:          observedAt,
                Locator:             locator,
                EvidenceId:          evidence.EvidenceId));
        }

        return results;
    }
}
