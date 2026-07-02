using System.Globalization;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Km.Store;

namespace Ii.Intake;

/// <summary>
/// Rules lane CSV intake (§6). Maps header names to claim_keys from §4, writes one belief
/// per mapped cell via VendorFileWriteService (which calls AppendBeliefAsync — the clamp
/// authority for §2 tier ceilings). observed_at comes from the data's date column;
/// falls back to evidence.IngestedAt if no date column is present.
/// </summary>
public sealed class VendorFileRulesExtractor
{
    private readonly VendorFileWriteService _writeService;
    private readonly SaasProfile           _profile;

    // Column-name aliases → catalogue claim_key (§4). Case-insensitive.
    private static readonly IReadOnlyDictionary<string, string> ColumnMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sla_uptime"]             = "sla_uptime",
            ["uptime%"]                = "sla_uptime",
            ["uptime"]                 = "sla_uptime",
            ["support_responsiveness"] = "support_responsiveness",
            ["support_resp"]           = "support_responsiveness",
            ["csat"]                   = "csat",
            ["usage_trend"]            = "usage_trend",
            ["usage_delta"]            = "usage_trend",
            ["usage delta"]            = "usage_trend",
            ["invoice_accuracy"]       = "invoice_accuracy",
            ["invoice accuracy"]       = "invoice_accuracy",
            ["roadmap_alignment"]      = "roadmap_alignment",
            ["renewal_intent"]         = "renewal_intent",
        };

    // Headers that carry the evidence date (observed_at source).
    private static readonly HashSet<string> DateColumns =
        new(StringComparer.OrdinalIgnoreCase)
        { "date", "observed_date", "period", "as_of", "report_date" };

    public VendorFileRulesExtractor(VendorFileWriteService writeService, SaasProfile profile)
    {
        _writeService = writeService;
        _profile      = profile;
    }

    /// <summary>
    /// Parse the CSV, emit one belief for each cell whose column header maps to a §4
    /// claim_key. Unmapped columns are silently skipped. Returns the written beliefs.
    /// </summary>
    public async Task<IReadOnlyList<Belief>> ExtractAndWriteAsync(
        Guid              vendorId,
        Evidence          evidence,
        string            csvContent,
        CancellationToken ct = default)
    {
        var lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return [];

        var headers    = ParseRow(lines[0]);
        var dateColIdx = FindDateColumn(headers);

        var tierCeiling = _profile.SourceTiers.TryGetValue(evidence.SourceTier.ToString(), out var tc)
            ? tc.Ceiling : 1.0;

        var results = new List<Belief>();

        for (var dataRow = 1; dataRow < lines.Length; dataRow++)
        {
            var cells = ParseRow(lines[dataRow]);
            if (cells.Length == 0) continue;

            var observedAt = ReadObservedAt(cells, dateColIdx, evidence.IngestedAt);
            var csvRowNum  = dataRow + 1; // 1-indexed; row 1 = header

            for (var colIdx = 0; colIdx < headers.Length && colIdx < cells.Length; colIdx++)
            {
                var header = headers[colIdx].Trim();
                if (!ColumnMap.TryGetValue(header, out var claimKey)) continue;
                if (!_profile.ClaimKeyCatalogue.TryGetValue(claimKey, out var ckDef)) continue;
                if (!Enum.TryParse<Dimension>(ckDef.Dimension, ignoreCase: true, out var dimension))
                    continue;
                if (!double.TryParse(cells[colIdx].Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var rawValue)) continue;

                var belief = await _writeService.WriteBeliefAsync(
                    vendorId:            vendorId,
                    claimKey:            claimKey,
                    dimension:           dimension,
                    criterion:           claimKey,
                    rawValue:            rawValue,
                    tier:                evidence.SourceTier,
                    extractorConfidence: tierCeiling,
                    observedAt:          observedAt,
                    provenance:          new BeliefProvenance(evidence.EvidenceId, $"row:{csvRowNum}"),
                    ingestedAt:          evidence.IngestedAt,
                    ct:                  ct);

                results.Add(belief);
            }
        }

        return results;
    }

    private static DateTimeOffset ReadObservedAt(
        string[] cells, int dateColIdx, DateTimeOffset fallback)
    {
        if (dateColIdx < 0 || dateColIdx >= cells.Length) return fallback;

        var raw = cells[dateColIdx].Trim();
        if (DateTimeOffset.TryParseExact(raw, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return dt;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out dt))
            return dt;

        return fallback;
    }

    private static int FindDateColumn(string[] headers)
    {
        for (var i = 0; i < headers.Length; i++)
            if (DateColumns.Contains(headers[i].Trim())) return i;
        return -1;
    }

    private static string[] ParseRow(string line)
    {
        var result   = new List<string>();
        var field    = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in line.TrimEnd('\r'))
        {
            if (ch == '"') { inQuotes = !inQuotes; }
            else if (ch == ',' && !inQuotes) { result.Add(field.ToString()); field.Clear(); }
            else { field.Append(ch); }
        }
        result.Add(field.ToString());
        return [.. result];
    }
}
