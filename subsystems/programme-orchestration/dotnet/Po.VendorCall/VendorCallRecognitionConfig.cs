using System.Text.Json.Nodes;

namespace Po.VendorCall;

/// <summary>
/// Thresholds and term lists that drive VendorCallRecognizer.
/// Loaded from catalogue/profiles/saas/vendor_call_recognition.saas.v1.json.
/// </summary>
public sealed record VendorCallRecognitionConfig(
    double                AutoRelevantThreshold,
    double                ReviewRelevantThreshold,
    IReadOnlyList<string> InternalDomains,
    IReadOnlyList<string> TitleTerms,
    IReadOnlyList<string> BodyTerms)
{
    /// <summary>Loads the config from the catalogue JSON file at <paramref name="path"/>.</summary>
    public static VendorCallRecognitionConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var node = JsonNode.Parse(json)
            ?? throw new InvalidOperationException($"Cannot parse vendor_call_recognition config at {path}");

        var thr = node["thresholds"]!;
        var rec = node["recognition"]!;

        return new VendorCallRecognitionConfig(
            AutoRelevantThreshold:   thr["auto_relevant"]!.GetValue<double>(),
            ReviewRelevantThreshold: thr["review_relevant"]!.GetValue<double>(),
            InternalDomains: node["internal_domains"]?.AsArray()
                .Select(n => n!.GetValue<string>())
                .ToList() ?? [],
            TitleTerms: rec["title_terms"]!.AsArray()
                .Select(n => n!.GetValue<string>())
                .ToList(),
            BodyTerms: rec["body_terms"]!.AsArray()
                .Select(n => n!.GetValue<string>())
                .ToList());
    }
}
