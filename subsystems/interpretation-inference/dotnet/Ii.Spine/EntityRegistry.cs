using Kozmo.Contracts.Config;

namespace Ii.Spine;

/// <summary>
/// In-memory registry of known entities + alias resolution.
/// For Phase 0 this is seeded from the fixture data.
/// </summary>
public sealed class EntityRegistry
{
    private readonly Dictionary<Guid, EntityRecord>   _byId    = [];
    private readonly Dictionary<string, Guid>         _byRef   = [];  // canonical name → id

    public void Register(Guid id, string canonicalName, DateTimeOffset? renewalDate = null)
    {
        var rec = new EntityRecord(id, canonicalName, renewalDate);
        _byId[id]              = rec;
        _byRef[canonicalName]  = id;
    }

    public Guid Resolve(Guid signalEntityId, IReadOnlyDictionary<string, object?> payload, SaasProfile profile)
    {
        // 1. Exact alias match on structured string values (e.g. "entity_name": "Cloudwave")
        foreach (var kv in payload)
        {
            if (kv.Value is string strVal)
            {
                if (profile.EntityResolution.AliasMap.TryGetValue(strVal, out var canonical)
                    && _byRef.TryGetValue(canonical, out var resolvedId))
                    return resolvedId;
            }
        }

        // 2. Substring scan on "body" text — first alias keyword found wins
        if (payload.TryGetValue("body", out var bodyObj) && bodyObj is string bodyText)
        {
            foreach (var alias in profile.EntityResolution.AliasMap.Keys)
            {
                if (bodyText.Contains(alias, StringComparison.OrdinalIgnoreCase)
                    && profile.EntityResolution.AliasMap.TryGetValue(alias, out var canonical)
                    && _byRef.TryGetValue(canonical, out var resolvedId))
                    return resolvedId;
            }
        }

        return signalEntityId;
    }

    public EntityRecord? GetEntity(Guid id) => _byId.TryGetValue(id, out var r) ? r : null;
}

public sealed record EntityRecord(Guid Id, string CanonicalName, DateTimeOffset? RenewalDate);
