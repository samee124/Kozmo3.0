using Ii.Spine;
using Kozmo.Contracts;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Class E — Light entity resolution (B3 STEP 2).
/// Tests body-text alias scanning in EntityRegistry.Resolve.
///
/// E1  "body" text mentions "Corvus"    → resolves to Corvus GUID
///     RED until body-text scan added to EntityRegistry (STEP 5)
/// E2  "body" text mentions "Meridian"  → resolves to Meridian GUID
///     RED until body-text scan added to EntityRegistry (STEP 5)
/// E3  "body" text with unknown vendor  → graceful fallback to signal entity_id
/// </summary>
public sealed class EntityResolutionB3Tests
{
    private static readonly Guid CorId = Guid.Parse("eeeeeeee-0002-0000-0000-000000000001");
    private static readonly Guid MerId = Guid.Parse("eeeeeeee-0003-0000-0000-000000000001");

    private static EntityRegistry MakeRegistry()
    {
        var r = new EntityRegistry();
        r.Register(Guid.Parse("eeeeeeee-0001-0000-0000-000000000001"), "Cloudwave Systems Inc.", null);
        r.Register(CorId,  "Corvus Infrastructure Ltd.",  null);
        r.Register(MerId,  "Meridian IT Services Ltd.",   null);
        return r;
    }

    // ── E1: body mentions "Corvus" → Corvus GUID ─────────────────────────────

    [Fact]
    [Trait("Class", "E")]
    public void E1_BodyTextMention_Corvus_ResolvesToCorvusEntity()
    {
        var registry = MakeRegistry();
        var profile  = TestHelpers.LoadProfile();

        // Signal entity_id is deliberately a random GUID to prove resolution, not fallback
        var signalEntityId = Guid.NewGuid();
        var payload = new Dictionary<string, object?>
        {
            ["body"] = "Support ticket response times from Corvus have been consistently above 48 hours."
        };

        var resolved = registry.Resolve(signalEntityId, payload, profile);
        Assert.Equal(CorId, resolved);
    }

    // ── E2: body mentions "Meridian" → Meridian GUID ─────────────────────────

    [Fact]
    [Trait("Class", "E")]
    public void E2_BodyTextMention_Meridian_ResolvesToMeridianEntity()
    {
        var registry = MakeRegistry();
        var profile  = TestHelpers.LoadProfile();

        var signalEntityId = Guid.NewGuid();
        var payload = new Dictionary<string, object?>
        {
            ["body"] = "Quarterly business review with Meridian went very well."
        };

        var resolved = registry.Resolve(signalEntityId, payload, profile);
        Assert.Equal(MerId, resolved);
    }

    // ── E3: body with unknown vendor → graceful fallback ─────────────────────

    [Fact]
    [Trait("Class", "E")]
    public void E3_BodyTextMention_UnknownVendor_ReturnsFallbackEntityId()
    {
        var registry = MakeRegistry();
        var profile  = TestHelpers.LoadProfile();

        var signalEntityId = Guid.NewGuid();
        var payload = new Dictionary<string, object?>
        {
            ["body"] = "A note about VendorXYZ that we have not heard of before."
        };

        var resolved = registry.Resolve(signalEntityId, payload, profile);
        Assert.Equal(signalEntityId, resolved);
    }
}
