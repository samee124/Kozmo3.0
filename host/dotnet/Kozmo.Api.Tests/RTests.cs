using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kozmo.Api.Tests;

/// <summary>
/// Class R — Vendor name resolution (identity upsert).
///
/// R1: POST /vendors/resolve-name with an exact existing name → returns that vendor's
///     known GUID, isNew=false. Exact OrdinalIgnoreCase match.
///
/// R2: POST /vendors/resolve-name with a name that doesn't exist → returns a fresh
///     GUID, isNew=true. The new vendor appears in GET /vendors.
///
/// R3: POST /vendors/resolve-name with a name that is CLOSE but not exact
///     (e.g. "Cloudwave Systems" vs "Cloudwave Systems Inc.") → creates a SEPARATE
///     vendor (never merged with the near-match). Proves exact-only semantics.
/// </summary>
[Collection("ApiTests")]
[Trait("Class", "R")]
public class RTests
{
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string CloudwaveId = "eeeeeeee-0001-0000-0000-000000000001";

    public RTests(ApiFixture fixture) => _client = fixture.CreateClient();

    // ── R1: exact match returns existing vendor ───────────────────────────────

    [Fact]
    public async Task R1_ExactExistingName_ReturnsKnownIdIsNewFalse()
    {
        var resp = await _client.PostAsJsonAsync(
            "/vendors/resolve-name",
            new { vendorName = "Cloudwave Systems Inc." });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<NameResolveResponse>(JsonOpts);
        Assert.NotNull(body);
        Assert.Equal(CloudwaveId,             body!.VendorId, StringComparer.OrdinalIgnoreCase);
        Assert.False(body.IsNew);
        Assert.Equal("Cloudwave Systems Inc.", body.CanonicalName);
    }

    // ── R1b: case-insensitive exact match also resolves ───────────────────────

    [Fact]
    public async Task R1b_CaseInsensitiveExactMatch_ReturnsKnownId()
    {
        var resp = await _client.PostAsJsonAsync(
            "/vendors/resolve-name",
            new { vendorName = "cloudwave systems inc." });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<NameResolveResponse>(JsonOpts);
        Assert.NotNull(body);
        Assert.Equal(CloudwaveId, body!.VendorId, StringComparer.OrdinalIgnoreCase);
        Assert.False(body.IsNew);
    }

    // ── R2: new name creates a fresh vendor visible in the rail ───────────────

    [Fact]
    public async Task R2_NewName_CreatesVendorAndAppearsInRail()
    {
        var newName = $"R2 Test Corp {Guid.NewGuid():N}";  // unique so parallel runs don't collide

        var resp = await _client.PostAsJsonAsync(
            "/vendors/resolve-name",
            new { vendorName = newName });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<NameResolveResponse>(JsonOpts);
        Assert.NotNull(body);
        Assert.True(body!.IsNew);
        Assert.False(string.IsNullOrEmpty(body.VendorId));
        Assert.Equal(newName, body.CanonicalName);

        // Calling again with the SAME name must return the SAME id, isNew=false
        var resp2 = await _client.PostAsJsonAsync(
            "/vendors/resolve-name",
            new { vendorName = newName });
        var body2 = await resp2.Content.ReadFromJsonAsync<NameResolveResponse>(JsonOpts);
        Assert.NotNull(body2);
        Assert.Equal(body.VendorId, body2!.VendorId, StringComparer.OrdinalIgnoreCase);
        Assert.False(body2.IsNew);

        // New vendor must appear in the vendor rail
        var summaries = await _client.GetFromJsonAsync<VendorSummaryDto[]>("/vendors", JsonOpts);
        Assert.NotNull(summaries);
        var created = summaries!.SingleOrDefault(v =>
            string.Equals(v.Name, newName, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(created);
        Assert.Equal(body.VendorId, created!.EntityId, StringComparer.OrdinalIgnoreCase);
    }

    // ── R3: near-but-not-exact name creates a SEPARATE vendor ─────────────────

    [Fact]
    public async Task R3_NearButNotExactName_CreatesSeparateVendor()
    {
        // "Cloudwave Systems" is close to "Cloudwave Systems Inc." but is NOT an exact match.
        // It must create a brand-new vendor, never merge with the existing one.
        var nearName = "Cloudwave Systems";   // missing " Inc."

        var resp = await _client.PostAsJsonAsync(
            "/vendors/resolve-name",
            new { vendorName = nearName });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<NameResolveResponse>(JsonOpts);
        Assert.NotNull(body);

        // Must be a NEW vendor — never the existing Cloudwave
        Assert.True(body!.IsNew, "Near-match should create a new vendor, not reuse the existing one.");
        Assert.NotEqual(CloudwaveId, body.VendorId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(nearName, body.CanonicalName);
    }

    // ── R4: empty name is rejected ────────────────────────────────────────────

    [Fact]
    public async Task R4_EmptyName_ReturnsBadRequest()
    {
        var resp = await _client.PostAsJsonAsync(
            "/vendors/resolve-name",
            new { vendorName = "" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
