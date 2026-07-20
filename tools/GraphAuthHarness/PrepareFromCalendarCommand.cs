#pragma warning disable CS0618 // obsolete pre-Review-pipeline composers kept for reference
using System.Text.Json;
using If.Contracts;
using If.MicrosoftGraph;
using Ig.Resolution;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using Po.VendorCall;
using Wc.Contracts;

namespace GraphAuthHarness;

/// <summary>
/// Reads the user's real Outlook calendar, runs VendorCallRecognizer on each event,
/// matches recognised meetings to seeded vendors in the identity registry, then
/// generates a pre-meeting brief using the existing seeded fixture emails and beliefs.
///
/// Real calendar → vendor match → stub evidence → briefing email.
/// </summary>
public static class PrepareFromCalendarCommand
{
    private const string FixtureEmailsFile  = "fixtures/northstar_emails.json";
    private const string OwnerEmailFallback = "rishi@econtracts.onmicrosoft.com";

    public static async Task RunAsync(
        string                      accessToken,
        string                      userObjectId,
        string                      userUpn,
        MicrosoftGraphTokenProvider tokenProvider,
        string                      tenantId,
        IConfiguration              config,
        CancellationToken           ct = default)
    {
        Console.WriteLine();
        Console.WriteLine(new string('─', 64));
        Console.WriteLine("Prepare from calendar — real meeting + stub evidence");
        Console.WriteLine(new string('─', 64));

        var ownerEmail = string.IsNullOrWhiteSpace(userUpn) ? OwnerEmailFallback : userUpn;

        // ── 0. Ensure vendor domains are up-to-date in the DB ─────────────────
        Console.WriteLine("Refreshing Northstar vendor domains in identity registry...");
        await NorthstarSeeder.SeedDatabaseAsync(ResolveDbPath(), ct);

        // ── 1. Read real calendar (next 14 days) ──────────────────────────────
        var now    = DateTimeOffset.UtcNow;
        var window = new CalendarWindow(now, now.AddDays(14));

        Console.WriteLine($"Reading calendar    : {window.FromUtc:u} → {window.ToUtc:u}");

        var calSource = new MicrosoftGraphCalendarSource(accessToken, userObjectId, tokenProvider, tenantId);
        var events    = await calSource.GetEventsAsync(userUpn, window, ct);

        Console.WriteLine($"Events found        : {events.Count}");

        // ── 2. Load recognition config + vendor lookup ────────────────────────
        var recognitionCfg = VendorCallRecognitionConfig.Load(
            ResolveCataloguePath("vendor_call_recognition.saas.v1.json"));
        var recognizer     = new VendorCallRecognizer(recognitionCfg);

        var dbPath       = ResolveDbPath();
        Console.WriteLine($"DB path             : {dbPath}");
        using var store  = new SqliteEntityStore($"Data Source={dbPath}");
        var registry     = new IdentityRegistry(store);
        var lookup       = new IgVendorLookup(registry);
        var matcher      = new VendorCallEntityMatcher(lookup);

        // Debug: show all vendors + domains in registry
        var allVendorsDebug = await registry.GetAllAsync(ct);
        foreach (var v in allVendorsDebug)
            Console.WriteLine($"  Registry vendor: {v.CanonicalName} | domains: [{string.Join(", ", v.KnownDomains)}]");

        // ── 3. Load recipe + question bank ────────────────────────────────────
        var recipe       = VendorCallRecipe.Load(ResolveCataloguePath("vendor_call_recipe.saas.v1.json"));
        var questionBank = VendorCallQuestionBank.Load(ResolveCataloguePath("vendor_call_questions.saas.v1.json"));

        Console.WriteLine($"Recipe              : {recipe.RecipeId} v{recipe.Version}");
        Console.WriteLine($"Question bank       : {questionBank.Count} question(s)");
        Console.WriteLine();

        // ── 4. Recognise + match each event ───────────────────────────────────
        int processed = 0;

        foreach (var ev in events)
        {
            Console.WriteLine(new string('─', 64));
            Console.WriteLine($"Event  : {ev.Subject}");
            Console.WriteLine($"Start  : {ev.StartUtc:yyyy-MM-dd HH:mm} UTC");
            Console.WriteLine($"Attendees: {ev.Attendees.Count}");

            var recognition = recognizer.Recognize(
                ev.Subject     ?? "",
                ev.BodyPreview ?? "",
                ev.Attendees);

            Console.WriteLine($"Recognised : {recognition.IsRelevant}  " +
                              $"(confidence {recognition.Confidence:P0}, " +
                              $"external attendees: {recognition.ExternalAttendees.Count})");

            if (!recognition.IsRelevant)
            {
                Console.WriteLine("→ Skipping (not a vendor call)");
                continue;
            }

            Console.WriteLine($"  External attendees : {string.Join(", ", recognition.ExternalAttendees)}");
            var matches = await matcher.MatchAsync(recognition.ExternalAttendees, ct);
            var vendorMatches = matches.Where(m => m.MatchType != VendorMatchType.Unmatched).ToList();

            if (vendorMatches.Count == 0)
            {
                Console.WriteLine("→ No vendor match found in identity registry — skipping");
                continue;
            }

            foreach (var match in vendorMatches)
            {
                Console.WriteLine($"→ Matched vendor : {match.VendorName} ({match.MatchType}, score {match.MatchScore:P0})");

                // Fetch vendor domains from registry
                var allVendors  = await registry.GetAllAsync(ct);
                var vendorEntry = allVendors.FirstOrDefault(v => v.VendorId == match.VendorId);
                var domains     = vendorEntry?.KnownDomains?.ToList() ?? (IReadOnlyList<string>)[];

                        await ProcessMeetingAsync(
                    ev, match, domains, ownerEmail,
                    recipe, questionBank,
                    store, dbPath, config, now, ct);

                processed++;
            }
        }

        Console.WriteLine(new string('─', 64));
        Console.WriteLine($"Done. Meetings processed: {processed}");
    }

    // ── Per-meeting pipeline ──────────────────────────────────────────────────

    private static async Task ProcessMeetingAsync(
        CalendarArtifact                  ev,
        VendorEntityMatchResult           match,
        IReadOnlyList<string>             vendorDomains,
        string                            ownerEmail,
        VendorCallRecipe                  recipe,
        IReadOnlyList<VendorCallQuestion> questionBank,
        SqliteEntityStore                 entityStore,
        string                  dbPath,
        IConfiguration          config,
        DateTimeOffset          now,
        CancellationToken       ct)
    {
        Console.WriteLine();
        Console.WriteLine($"Processing: {ev.Subject} → {match.VendorName}");

        var context = new VendorCallContext(
            Meeting:                 ev,
            Match:                   match,
            VendorDomains:           vendorDomains,
            SignedInUserPrincipalId: ownerEmail,
            Recipe:                  recipe);

        // ── Evidence (fixture emails + seeded beliefs) ────────────────────────
        var fixturePath = ResolveLocalPath(FixtureEmailsFile);
        var mailSource  = new FixtureMailSource(fixturePath);
        var collector   = new VendorCallEvidenceCollector(mailSource, entityStore);
        var bundle      = await collector.CollectAsync(context, now, ct);

        Console.WriteLine($"  Contracts          : {bundle.Contracts.Count}");
        Console.WriteLine($"  Recent emails      : {bundle.RecentEmails.Count}");
        Console.WriteLine($"  Open commitments   : {bundle.OpenCommitments.Count}");
        Console.WriteLine($"  Commercial signals : {bundle.CommercialSignals.Count}");
        Console.WriteLine($"  Evidence gaps      : {bundle.EvidenceGaps.Count}");

        // ── Pre-meeting check-in ──────────────────────────────────────────────
        var checkInStore   = new DirectCheckInStore(entityStore);
        var nullTransport  = new NullCheckInTransport();
        var planner        = new VendorCallCheckInPlanner(ownerEmail);
        var dispatchResult = await planner.PlanPreMeetingAsync(
            context, bundle, questionBank, checkInStore, nullTransport, now, ct);

        Console.WriteLine($"  Check-in status    : {dispatchResult.Status}");

        // ── Briefing ──────────────────────────────────────────────────────────
        var beliefs        = await entityStore.GetCurrentBeliefsAsync(match.VendorId, ct);
        var briefCtx       = new VendorCallBriefingContext(
            Meeting:                 ev,
            VendorName:              match.VendorName,
            VendorId:                match.VendorId,
            Now:                     now,
            Recipe:                  recipe,
            RecentEmails:            bundle.RecentEmails,
            Contracts:               bundle.Contracts,
            PriorMeetingNotes:       bundle.PriorMeetingNotes,
            OpenCommitments:         bundle.OpenCommitments,
            CommercialSignals:       bundle.CommercialSignals,
            EvidenceGaps:            bundle.EvidenceGaps,
            CurrentBeliefs:          beliefs,
            PreMeetingCheckInResult: dispatchResult);

        var briefing = await new VendorCallBriefingComposer().ComposeAsync(briefCtx, ct);
        var rendered = BriefingTextRenderer.Render(briefing);

        Console.WriteLine();
        Console.WriteLine(rendered);

        // ── Email ─────────────────────────────────────────────────────────────
        var emailContent = await new BriefingEmailComposer(llm: null).ComposeEmailAsync(briefing, rendered, ct);
        await SendBriefingEmailAsync(emailContent, ownerEmail, config, ct);

        // ── Save VendorCallRun (idempotent) ───────────────────────────────────
        using var runStore = new SqliteVendorCallRunStore($"Data Source={dbPath}");
        var existing = await runStore.GetByEventIdAsync(ev.ExternalId, ct);
        if (existing is not null)
        {
            Console.WriteLine($"  VendorCallRun      : already exists ({existing.Id}) — skipping");
        }
        else
        {
            var run = new VendorCallRun
            {
                Id                      = Guid.NewGuid(),
                EventId                 = ev.ExternalId,
                ICalUid                 = ev.ICalUid,
                JoinWebUrl              = ev.JoinWebUrl,
                VendorId                = match.VendorId,
                VendorName              = match.VendorName,
                MeetingSubject          = ev.Subject,
                StartUtc                = ev.StartUtc,
                EndUtc                  = ev.EndUtc,
                SignedInUserPrincipalId = ownerEmail,
                Status                  = VendorCallStatus.BriefingSent,
                BriefingSentAt          = now,
                CreatedAt               = now,
                UpdatedAt               = now,
            };
            await runStore.SaveAsync(run, ct);
            Console.WriteLine($"  VendorCallRun      : created ({run.Id})");
            Console.WriteLine($"  JoinWebUrl         : {run.JoinWebUrl ?? "(none)"}");
        }
    }

    // ── Brevo send ────────────────────────────────────────────────────────────

    private static async Task SendBriefingEmailAsync(
        BriefingEmailContent email,
        string               recipientEmail,
        IConfiguration       config,
        CancellationToken    ct)
    {
        var smtpKey     = config["Brevo:SmtpKey"]     ?? Environment.GetEnvironmentVariable("BREVO_SMTP_KEY");
        var senderEmail = config["Brevo:SenderEmail"] ?? Environment.GetEnvironmentVariable("BREVO_SENDER_EMAIL");

        if (string.IsNullOrWhiteSpace(smtpKey) || string.IsNullOrWhiteSpace(senderEmail))
        {
            Console.WriteLine("[null transport] Brevo not configured — email not sent.");
            Console.WriteLine($"  To      : {recipientEmail}");
            Console.WriteLine($"  Subject : {email.Subject}");
            return;
        }

        var smtpHost  = Environment.GetEnvironmentVariable("BREVO_SMTP_HOST") ?? "smtp-relay.brevo.com";
        var smtpPort  = int.TryParse(Environment.GetEnvironmentVariable("BREVO_SMTP_PORT"), out var p) ? p : 587;
        var smtpLogin = config["Brevo:SmtpUser"]
                        ?? Environment.GetEnvironmentVariable("BREVO_SMTP_LOGIN")
                        ?? "9f924d001@smtp-brevo.com";
        var senderName = Environment.GetEnvironmentVariable("BREVO_SENDER_NAME") ?? "Kozmo Briefings";

        Console.WriteLine($"Sending via Brevo SMTP → {recipientEmail}");

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(senderName, senderEmail));
        msg.To.Add(MailboxAddress.Parse(recipientEmail));
        msg.Subject = email.Subject;
        msg.Body    = new BodyBuilder { HtmlBody = email.HtmlBody, TextBody = email.PlainTextBody }
                          .ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(smtpHost, smtpPort, SecureSocketOptions.StartTls, ct);
        await smtp.AuthenticateAsync(smtpLogin, smtpKey, ct);
        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(quit: true, ct);

        Console.WriteLine($"  Sent OK ({email.PlainTextBody.Length} chars, LlmEnhanced={email.LlmEnhanced})");
    }

    // ── Path helpers (mirrors SeedAndPrepareNorthstarCommand) ────────────────

    private static string ResolveDbPath()
    {
        var envPath = Environment.GetEnvironmentVariable("KOZMO_DB_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return envPath;

        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "kozmo-demo.db");
            if (File.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        throw new FileNotFoundException(
            "kozmo-demo.db not found. Set KOZMO_DB_PATH or run Kozmo.Api first.");
    }

    private static string ResolveLocalPath(string relative)
    {
        var alongside = Path.Combine(AppContext.BaseDirectory, relative);
        if (File.Exists(alongside)) return alongside;

        var dir = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        throw new FileNotFoundException($"Fixture file not found: {relative}");
    }

    private static string ResolveCataloguePath(string fileName)
    {
        const string relativePart = "catalogue/profiles/saas";
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, relativePart, fileName);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException($"Catalogue file '{fileName}' not found.");
    }
}
