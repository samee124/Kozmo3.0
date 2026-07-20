using System.Net.Http.Headers;
using System.Text.Json;
using GraphAuthHarness;
using If.Contracts;
using If.MicrosoftGraph;
using Microsoft.Extensions.Configuration;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";

// ── Configuration ────────────────────────────────────────────────────────────
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets<Program>()
    .Build();

var options = config.GetSection("MicrosoftGraph").Get<MicrosoftGraphOptions>()
    ?? throw new InvalidOperationException(
        "MicrosoftGraph configuration section is missing in appsettings.json.");

if (string.IsNullOrWhiteSpace(options.ClientSecret))
    throw new InvalidOperationException(
        "\nMicrosoftGraph:ClientSecret is not set.\n" +
        "Set it via user secrets:\n" +
        "  dotnet user-secrets set \"MicrosoftGraph:ClientSecret\" \"<value>\" " +
        "--project tools/GraphAuthHarness\n");

// ── Interactive sign-in ───────────────────────────────────────────────────────
Console.WriteLine("Opening browser for sign-in (delegated auth-code + PKCE)...");
Console.WriteLine($"Scopes requested: {string.Join(", ", options.Scopes)}");

var tokenProvider = new MicrosoftGraphTokenProvider(options);
var result        = await tokenProvider.AcquireInteractiveAsync();

Console.WriteLine();
Console.WriteLine($"Signed-in user      : {result.UserUpn}");
Console.WriteLine($"Object ID           : {result.UserObjectId[..8]}...");
Console.WriteLine($"Delegated scopes    : {string.Join(", ", result.GrantedScopes)}");
Console.WriteLine($"Token expiry (UTC)  : {result.ExpiresOn:u}");

// ── Prove token works: call /me ───────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("Calling /me to verify token end-to-end...");
using var http = new HttpClient();
http.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", result.AccessToken);
var meJson = await http.GetStringAsync("https://graph.microsoft.com/v1.0/me");
using var meDoc = JsonDocument.Parse(meJson);
var displayName = meDoc.RootElement.TryGetProperty("displayName", out var dn)
    ? dn.GetString() : "(unknown)";
Console.WriteLine($"Display name        : {displayName}");

// ── Silent refresh test ───────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("Testing silent token refresh (uses cached refresh token)...");
var result2 = await tokenProvider.AcquireSilentAsync(result.UserObjectId);
Console.WriteLine($"Silent refresh test : OK (expiry {result2.ExpiresOn:u})");

// ── Dispatch: seed-northstar ──────────────────────────────────────────────────
if (command == "seed-northstar")
{
    await NorthstarSeeder.RunAsync(
        result.AccessToken,
        result.UserObjectId,
        tokenProvider,
        options.TenantId);
    return;
}

// ── Dispatch: seed-and-prepare-northstar ──────────────────────────────────────
if (command == "seed-and-prepare-northstar")
{
    await SeedAndPrepareNorthstarCommand.RunAsync(
        result.AccessToken,
        result.UserObjectId,
        tokenProvider,
        options.TenantId,
        config);
    return;
}

// ── Dispatch: prepare-from-calendar ──────────────────────────────────────────
if (command == "prepare-from-calendar")
{
    await PrepareFromCalendarCommand.RunAsync(
        result.AccessToken,
        result.UserObjectId,
        result.UserUpn,
        tokenProvider,
        options.TenantId,
        config);
    return;
}

// ── Dispatch: post-meeting-transcript ────────────────────────────────────────
if (command == "post-meeting-transcript")
{
    await PostMeetingTranscriptCommand.RunAsync(
        result.AccessToken,
        result.UserObjectId,
        tokenProvider,
        options.TenantId,
        config);
    return;
}

// ── Calendar read ─────────────────────────────────────────────────────────────
Console.WriteLine();
Console.Write("Enter number of days to read (default 14): ");
var dayInput = Console.ReadLine();
var days     = int.TryParse(dayInput, out var d) && d > 0 ? d : 14;

var window = new CalendarWindow(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(days));
Console.WriteLine($"Reading calendar    : {window.FromUtc:u} → {window.ToUtc:u}");

var calendarSource = new MicrosoftGraphCalendarSource(
    result.AccessToken,
    result.UserObjectId,
    tokenProvider,
    options.TenantId);

var events = await calendarSource.GetEventsAsync(result.UserUpn, window, CancellationToken.None);

Console.WriteLine();
Console.WriteLine(new string('─', 64));
int nullFieldCount = 0;
foreach (var ev in events)
{
    var start   = ev.StartUtc.ToString("yyyy-MM-dd HH:mm");
    var end     = ev.EndUtc.ToString("HH:mm");
    var subject = string.IsNullOrEmpty(ev.Subject) ? "(no subject)" : ev.Subject;
    Console.WriteLine($"[{start} - {end} UTC]  {subject}");
    Console.WriteLine($"  organizer   : {ev.Organizer}");
    Console.WriteLine($"  attendees   : [count: {ev.Attendees.Count}]");
    Console.WriteLine($"  externalId  : {ev.ExternalId}");
    if (!string.IsNullOrEmpty(ev.BodyPreview))
    {
        var preview = ev.BodyPreview.Length > 80
            ? ev.BodyPreview[..80] + "..."
            : ev.BodyPreview;
        Console.WriteLine($"  bodyPreview : {preview}");
    }

    if (string.IsNullOrEmpty(ev.Subject))   nullFieldCount++;
    if (string.IsNullOrEmpty(ev.Organizer)) nullFieldCount++;
}
Console.WriteLine(new string('─', 64));
Console.WriteLine($"Total events        : {events.Count}");
if (nullFieldCount > 0)
    Console.WriteLine($"Defaulted fields    : {nullFieldCount} (check mapping)");

// ── Mail read ─────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.Write("Enter vendor domain to search (e.g. northstarsoftware.com): ");
var domain = Console.ReadLine()?.Trim() ?? "";

Console.Write("Enter lookback days (default 90): ");
var lookbackInput = Console.ReadLine();
var lookback      = int.TryParse(lookbackInput, out var lb) && lb > 0 ? lb : 90;

Console.WriteLine($"Searching mail      : last {lookback} days, domain: {(string.IsNullOrEmpty(domain) ? "(all)" : domain)}");

var mailCriteria = new MailSearchCriteria(
    VendorId:            Guid.Empty,
    VendorDomains:       string.IsNullOrEmpty(domain) ? [] : [domain],
    MeetingParticipants: [],
    FromUtc:             DateTimeOffset.UtcNow.AddDays(-lookback),
    ToUtc:               DateTimeOffset.UtcNow,
    CommercialTerms:     [],
    MaximumMessages:     30);

var mailSource = new MicrosoftGraphMailSource(
    result.AccessToken,
    result.UserObjectId,
    tokenProvider,
    options.TenantId);

var messages = await mailSource.FindRelevantMessagesAsync(result.UserUpn, mailCriteria, CancellationToken.None);

Console.WriteLine();
Console.WriteLine(new string('─', 64));
foreach (var msg in messages)
{
    var sent     = msg.SentAtUtc.ToString("yyyy-MM-dd HH:mm");
    var subject  = string.IsNullOrEmpty(msg.Subject) ? "(no subject)" : msg.Subject;
    var convPrev = msg.ConversationId.Length > 12 ? msg.ConversationId[..12] + "..." : msg.ConversationId;
    Console.WriteLine($"[{sent} UTC]  {subject}");
    Console.WriteLine($"  from    : {msg.Sender}");
    Console.WriteLine($"  to/cc   : [count: {msg.Recipients.Count}]");
    Console.WriteLine($"  convId  : {convPrev}");
    if (!string.IsNullOrEmpty(msg.BodyPreview))
    {
        var preview = msg.BodyPreview.Length > 80 ? msg.BodyPreview[..80] + "..." : msg.BodyPreview;
        Console.WriteLine($"  preview : {preview}");
    }
}
Console.WriteLine(new string('─', 64));
Console.WriteLine($"Total messages      : {messages.Count} (from domain: {(string.IsNullOrEmpty(domain) ? "all" : domain)})");
