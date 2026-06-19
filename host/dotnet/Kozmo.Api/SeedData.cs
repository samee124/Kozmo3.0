using System.Text.Json;
using Kozmo.Contracts;

namespace Kozmo.Api;

/// <summary>
/// Demo vendor IDs and the 10 seed signals loaded from fixtures/signals.json.
/// Replay ingests these in order (received_at ascending) to reproduce the deterministic state.
/// </summary>
internal static class SeedData
{
    internal static readonly Guid CloudwaveId = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");
    internal static readonly Guid CorvusId    = Guid.Parse("eeeeeeee-0002-0000-0000-000000000001");
    internal static readonly Guid MeridianId = Guid.Parse("eeeeeeee-0003-0000-0000-000000000001");
    internal static readonly Guid HelixId = Guid.Parse("eeeeeeee-0004-0000-0000-000000000001");
    internal static readonly Guid CustomerId  = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    internal static readonly Guid[] VendorIds = [CloudwaveId, CorvusId, MeridianId, HelixId];

    internal static readonly Signal[] AllSignals = LoadSignals();

    private static Signal[] LoadSignals()
    {
        var path = FindFixturesFile();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray()
            .Select(ParseSignal)
            .ToArray();
    }

    private static string FindFixturesFile()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "fixtures", "signals.json");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Cannot locate fixtures/signals.json. Run from the repo root or a subdirectory.");
    }

    private static Signal ParseSignal(JsonElement e) =>
        new Signal(
            Id:           Guid.Parse(e.GetProperty("id").GetString()!),
            EntityId:     Guid.Parse(e.GetProperty("entity_id").GetString()!),
            CustomerId:   Guid.Parse(e.GetProperty("customer_id").GetString()!),
            SourceSystem: Enum.Parse<SourceSystem>(e.GetProperty("source_system").GetString()!, ignoreCase: true),
            ExternalId:   e.GetProperty("external_id").GetString()!,
            Payload:      ParsePayload(e.GetProperty("payload")),
            ObservedAt:   DateTimeOffset.Parse(e.GetProperty("observed_at").GetString()!),
            ReceivedAt:   DateTimeOffset.Parse(e.GetProperty("received_at").GetString()!),
            TraceId:      Guid.Parse(e.GetProperty("trace_id").GetString()!));

    private static IReadOnlyDictionary<string, object?> ParsePayload(JsonElement payload)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in payload.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.String => (object?)prop.Value.GetString(),
                JsonValueKind.True   => (object?)true,
                JsonValueKind.False  => (object?)false,
                _                   => null
            };
        }
        return dict;
    }
}
