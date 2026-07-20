using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ig.Contracts;
using Ig.Resolution;
using If.MicrosoftGraph;
using Km.Store;

namespace GraphAuthHarness;

/// <summary>
/// Seeds the Northstar Software scenario into kozmo-demo.db and Outlook calendar.
///
/// What this does:
///   1. Locates kozmo-demo.db (env KOZMO_DB_PATH, or walks up from the tool's directory).
///   2. Upserts the Northstar vendor in the identity registry (KnownDomains + Aliases).
///   3. Registers Northstar in the vendors table so the API loads it on next restart.
///   4. Creates one upcoming Outlook calendar event via the Graph API.
///   5. Prints next-step instructions (evidence ingest must be run via the API).
///
/// The evidence fixture lives at: fixtures/vendor-file/northstar.evidence.json
/// After running this seeder, start the API and call:
///   POST /vendors/dd000001-0000-0000-0000-000000000001/vendor-file/ingest
///   { "fixturePath": "&lt;absolute-path-to&gt;/fixtures/vendor-file/northstar.evidence.json" }
/// </summary>
public static class NorthstarSeeder
{
    public static readonly Guid   VendorId       = Guid.Parse("dd000001-0000-0000-0000-000000000001");
    public const           string VendorName     = "Northstar Software";
    public const           string VendorDomain   = "northstarsoftware.com";

    // Renewal date: 2026-09-28 (75 days from 2026-07-15)
    private static readonly DateTimeOffset RenewalDate =
        new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync(
        string                       accessToken,
        string                       userObjectId,
        MicrosoftGraphTokenProvider  tokenProvider,
        string                       tenantId,
        CancellationToken            ct = default)
    {
        Console.WriteLine();
        Console.WriteLine(new string('─', 64));
        Console.WriteLine("Northstar Software scenario seeder");
        Console.WriteLine(new string('─', 64));

        // ── 1. Locate DB ─────────────────────────────────────────────────────
        var dbPath = ResolveDbPath();
        Console.WriteLine($"DB path             : {dbPath}");

        // ── 2+3. Seed identity registry + vendors table ───────────────────────
        await SeedDatabaseAsync(dbPath, ct);

        // ── 4. Create Outlook calendar event ─────────────────────────────────
        await CreateCalendarEventAsync(accessToken, ct);

        // ── 5. Summary ───────────────────────────────────────────────────────
        PrintNextSteps(dbPath);
    }

    // ── Database seeding ─────────────────────────────────────────────────────

    public static async Task SeedDatabaseAsync(string dbPath, CancellationToken ct)
    {
        using var store    = new SqliteEntityStore($"Data Source={dbPath}");
        var       registry = new IdentityRegistry(store);

        // Check if vendor already exists in identity registry
        var existing = await registry.GetAllAsync(ct);
        var northstar = existing.FirstOrDefault(v =>
            v.VendorId == VendorId ||
            v.KnownDomains.Any(d => d.Equals(VendorDomain, StringComparison.OrdinalIgnoreCase)));

        // Register in the vendors table FIRST — SaveVendorAsync uses INSERT OR REPLACE with only
        // basic columns, which would clobber domains_json if run after the registry upsert.
        await store.SaveVendorAsync(VendorId, VendorName, RenewalDate, DateTimeOffset.UtcNow);
        Console.WriteLine($"Vendors table       : registered with renewal {RenewalDate:yyyy-MM-dd}");

        if (northstar is not null && northstar.VendorId != VendorId)
        {
            Console.WriteLine($"WARNING: Northstar found under different ID {northstar.VendorId} — skipping identity registry upsert.");
        }
        else
        {
            var vendor = new CanonicalVendor(
                VendorId:             VendorId,
                CanonicalName:        VendorName,
                Aliases:
                [
                    new VendorAlias(
                        AliasId:         Guid.Parse("dd000001-0010-0000-0000-000000000001"),
                        VendorId:        VendorId,
                        RawName:         "Northstar",
                        ProvenanceDocId: null,
                        ProvenanceSpan:  null),
                    new VendorAlias(
                        AliasId:         Guid.Parse("dd000001-0011-0000-0000-000000000001"),
                        VendorId:        VendorId,
                        RawName:         "NStar",
                        ProvenanceDocId: null,
                        ProvenanceSpan:  null)
                ],
                ComparisonKey:        "northstarsoftware",
                EntityType:           EntityType.Company,
                Confidence:           0.90,
                Flags:                [],
                Status:               RegistryStatus.Confirmed,
                RebrandMapRef:        null,
                AcquisitionMapRef:    null,
                CreatedAt:            DateTimeOffset.UtcNow,
                AbsorbedIntoVendorId: null,
                EntityRole:           "saas_vendor")
            {
                KnownDomains = [VendorDomain, "northstarsoftwares.in"]
            };

            await registry.SaveAsync(vendor, ct);
            Console.WriteLine($"Identity registry   : upserted {VendorName} ({VendorId})");
        }
    }

    // ── Calendar event ────────────────────────────────────────────────────────

    private static async Task CreateCalendarEventAsync(string accessToken, CancellationToken ct)
    {
        // Schedule for the next working Tuesday — one week from today
        var meetingDate = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);

        var body = new
        {
            subject = "Northstar Software — annual renewal review",
            start   = new { dateTime = meetingDate.ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
            end     = new { dateTime = meetingDate.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
            body    = new
            {
                contentType = "HTML",
                content = """
                    <p>Agenda for annual renewal review — Northstar Software</p>
                    <ol>
                      <li><strong>Pricing uplift</strong>: vendor has proposed 7% uplift (£285k → £304,950). Counter-position to be presented.</li>
                      <li><strong>Outstanding SLA report</strong>: Q2 2026 SLA report not yet received (overdue since 30 June). Resolution required before renewal is executed.</li>
                      <li><strong>Renewal window</strong>: contract renews 28 September 2026. 60-day non-renewal notice deadline is 29 July — <em>14 days from today</em>. Decision required urgently.</li>
                    </ol>
                    <p>Vendor contact: Alex Hamilton (alex.hamilton@northstarsoftware.com)</p>
                    """
            },
            attendees = new[]
            {
                new
                {
                    emailAddress = new { address = "alex.hamilton@northstarsoftware.com", name = "Alex Hamilton" },
                    type = "required"
                }
            },
            isOnlineMeeting     = false,
            showAs              = "busy",
            importance          = "high",
            reminderMinutesBeforeStart = 60,
            isReminderOn        = true
        };

        using var http    = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        var json    = JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = null });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await http.PostAsync("https://graph.microsoft.com/v1.0/me/events", content, ct);

        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            using var doc    = JsonDocument.Parse(responseBody);
            var eventId      = doc.RootElement.TryGetProperty("id", out var idProp)
                ? idProp.GetString() ?? "(unknown)"
                : "(unknown)";
            Console.WriteLine($"Calendar event      : created — '{body.subject}'");
            Console.WriteLine($"  Date/time         : {meetingDate:yyyy-MM-dd HH:mm} UTC");
            Console.WriteLine($"  Graph event ID    : {(eventId.Length > 24 ? eventId[..24] + "..." : eventId)}");
        }
        else
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"Calendar event      : FAILED ({(int)response.StatusCode}) — {error[..Math.Min(200, error.Length)]}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ResolveDbPath()
    {
        // Env var override — use the path even if the file doesn't exist yet (SQLite creates it)
        var envPath = Environment.GetEnvironmentVariable("KOZMO_DB_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return envPath;

        // Walk from the tool's directory up to the repo root, then down to the API's typical output
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            // Look for the DB directly in this directory (API output)
            var candidate = Path.Combine(dir, "kozmo-demo.db");
            if (File.Exists(candidate)) return candidate;

            // Walk up
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        throw new FileNotFoundException(
            "kozmo-demo.db not found. Set KOZMO_DB_PATH environment variable or run the Kozmo.Api " +
            "at least once to create the database, then re-run this command.");
    }

    private static void PrintNextSteps(string dbPath)
    {
        var fixtureHint = dbPath.Contains("Kozmo.Api")
            ? Path.Combine(Path.GetDirectoryName(dbPath)!, "..", "..", "..", "..", "..",
                           "fixtures", "vendor-file", "northstar.evidence.json")
            : "<repo-root>/fixtures/vendor-file/northstar.evidence.json";

        Console.WriteLine();
        Console.WriteLine(new string('─', 64));
        Console.WriteLine("Seeding complete. Next steps:");
        Console.WriteLine();
        Console.WriteLine("  1. Start Kozmo.Api (if not already running).");
        Console.WriteLine();
        Console.WriteLine("  2. Run the evidence ingest to create beliefs:");
        Console.WriteLine($"       POST /vendors/{VendorId}/vendor-file/ingest");
        Console.WriteLine($"       {{ \"fixturePath\": \"{fixtureHint}\" }}");
        Console.WriteLine();
        Console.WriteLine("  3. Verify at:");
        Console.WriteLine($"       GET /vendors/{VendorId}/vendor-file");
        Console.WriteLine($"       GET /vendors/{VendorId}/vendor-file/markdown");
        Console.WriteLine();
        Console.WriteLine("  NOTE: Custom commitment claim keys (pricing_uplift, sla_report_overdue,");
        Console.WriteLine("        counter_proposal_status, notice_deadline) are not in the catalogue.");
        Console.WriteLine("        Evidence items 6-8 record the paper trail without belief extraction.");
        Console.WriteLine("        Add keys to claim_key_catalogue.saas.v1.json in Phase 7 if needed.");
        Console.WriteLine(new string('─', 64));
    }
}
