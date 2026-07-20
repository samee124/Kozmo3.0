#pragma warning disable CS0618 // obsolete pre-Review-pipeline composers kept for reference
using System.Globalization;
using System.Text.Json;
using If.Contracts;
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
/// Offline harness command: seeds the Northstar scenario into the local SQLite DB,
/// runs the evidence collector against the fixture emails + entity store, then raises
/// a pre-meeting check-in via VendorCallCheckInPlanner.
///
/// No live Microsoft Graph calls are made after the initial seed step.
/// Transport is null (check-in persisted to DB only, visible via the API).
/// </summary>
public static class SeedAndPrepareNorthstarCommand
{
    // ── Northstar constants (kept in sync with NorthstarSeeder) ───────────────

    private static readonly Guid VendorId = NorthstarSeeder.VendorId;

    // Synthetic stable event ID used when the calendar event is not accessible
    // from the harness (no live Graph read). Must be stable across re-runs to
    // satisfy the idempotency guard.
    private const string SyntheticEventId    = "northstar-renewal-2026-07-22";
    private const string OwnerEmail          = "rishi@econtracts.onmicrosoft.com";
    private const string FixtureEmailsFile   = "fixtures/northstar_emails.json";
    private const string VendorEvidenceFile  = "fixtures/vendor-file/northstar.evidence.json";

    public static async Task RunAsync(
        string accessToken, string userObjectId,
        If.MicrosoftGraph.MicrosoftGraphTokenProvider tokenProvider,
        string tenantId,
        IConfiguration config,
        CancellationToken ct = default)
    {
        Console.WriteLine();
        Console.WriteLine(new string('─', 64));
        Console.WriteLine("Northstar Software — seed + prepare (Phase 7a)");
        Console.WriteLine(new string('─', 64));

        // ── 1. Seed vendor record + calendar event ────────────────────────────
        await NorthstarSeeder.RunAsync(accessToken, userObjectId, tokenProvider, tenantId, ct);

        // ── 1b. Seed vendor-file evidence + beliefs ───────────────────────────
        var dbPath              = ResolveDbPath();
        var vendorEvidencePath  = ResolveLocalPath(VendorEvidenceFile);
        var catalogueDirPath    = ResolveCatalogueDir();
        var saasProfile         = new Catalogue().Load(catalogueDirPath);

        Console.WriteLine();
        Console.WriteLine("Seeding vendor-file evidence + beliefs...");
        using (var seedStore = new SqliteEntityStore($"Data Source={dbPath}"))
        {
            await SeedEvidenceAndBeliefsAsync(
                seedStore, saasProfile, vendorEvidencePath, DateTimeOffset.UtcNow, ct);
        }

        // ── 2. Locate artefacts ───────────────────────────────────────────────
        var fixturePath     = ResolveLocalPath(FixtureEmailsFile);
        var recipePath      = ResolveCataloguePath("vendor_call_recipe.saas.v1.json");
        var questionBankPath= ResolveCataloguePath("vendor_call_questions.saas.v1.json");

        Console.WriteLine();
        Console.WriteLine("Resolving artefacts:");
        Console.WriteLine($"  DB              : {dbPath}");
        Console.WriteLine($"  Vendor evidence : {vendorEvidencePath}");
        Console.WriteLine($"  Fixture emails  : {fixturePath}");
        Console.WriteLine($"  Recipe          : {recipePath}");
        Console.WriteLine($"  Question bank   : {questionBankPath}");

        // ── 3. Load recipe + question bank ────────────────────────────────────
        var recipe       = VendorCallRecipe.Load(recipePath);
        var questionBank = VendorCallQuestionBank.Load(questionBankPath);

        Console.WriteLine();
        Console.WriteLine($"Recipe loaded       : {recipe.RecipeId} v{recipe.Version}");
        Console.WriteLine($"Question bank       : {questionBank.Count} questions");

        // ── 4. Build VendorCallContext ─────────────────────────────────────────
        var now = DateTimeOffset.UtcNow;

        // Synthetic calendar artifact for the offline harness
        var meeting = new CalendarArtifact(
            ArtifactId:        Guid.NewGuid(),
            SourceSystem:      "harness",
            SourceType:        "synthetic_event",
            TenantId:          tenantId,
            SourcePrincipalId: userObjectId,
            ExternalId:        SyntheticEventId,
            ICalUid:           SyntheticEventId,
            Subject:           "Northstar Software — annual renewal review",
            StartUtc:          new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero),
            EndUtc:            new DateTimeOffset(2026, 7, 22, 11, 0, 0, TimeSpan.Zero),
            Organizer:         OwnerEmail,
            Attendees:         [OwnerEmail, "alex.hamilton@northstarsoftware.com"],
            BodyPreview:       "Annual renewal review — Northstar Software",
            CapturedAtUtc:     now);

        var match = new VendorEntityMatchResult(
            VendorId:   VendorId,
            VendorName: NorthstarSeeder.VendorName,
            MatchType:  VendorMatchType.DomainExact,
            MatchScore: 0.95);

        var context = new VendorCallContext(
            Meeting:                 meeting,
            Match:                   match,
            VendorDomains:           [NorthstarSeeder.VendorDomain],
            SignedInUserPrincipalId: userObjectId,
            Recipe:                  recipe);

        // ── 5. Collect evidence ───────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("Collecting evidence...");

        using var entityStore = new SqliteEntityStore($"Data Source={dbPath}");
        var mailSource        = new FixtureMailSource(fixturePath);
        var collector         = new VendorCallEvidenceCollector(mailSource, entityStore);

        var bundle = await collector.CollectAsync(context, now, ct);

        PrintBundle(bundle);

        // ── 6. Raise pre-meeting check-in ─────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("Planning pre-meeting check-in...");

        var checkInStore  = new DirectCheckInStore(entityStore);
        var nullTransport = new NullCheckInTransport();
        var planner       = new VendorCallCheckInPlanner(OwnerEmail);

        var dispatchResult = await planner.PlanPreMeetingAsync(
            context, bundle, questionBank, checkInStore, nullTransport, now, ct);

        switch (dispatchResult.Status)
        {
            case CheckInDispatchStatus.Dispatched:
                Console.WriteLine($"Check-in            : created (ID {dispatchResult.CheckIn!.CheckInId})");
                Console.WriteLine($"  TargetField       : {dispatchResult.CheckIn.TargetField}");
                Console.WriteLine($"  Question          : {dispatchResult.CheckIn.Question[..Math.Min(80, dispatchResult.CheckIn.Question.Length)]}...");
                Console.WriteLine($"  ExpiresAt         : {dispatchResult.CheckIn.ExpiresAt:yyyy-MM-dd}");
                Console.WriteLine($"  Owner             : {dispatchResult.CheckIn.Owner}");
                break;

            case CheckInDispatchStatus.AlreadyDispatched:
                Console.WriteLine("Check-in            : already dispatched (idempotency guard)");
                break;

            case CheckInDispatchStatus.NoQuestionsAvailable:
                Console.WriteLine("Check-in            : no pre_meeting questions found in bank");
                break;
        }

        // ── 7. Compose pre-meeting brief (Mode A — deterministic) ────────────
        Console.WriteLine();
        Console.WriteLine("Composing pre-meeting brief (Mode A — deterministic)...");

        var beliefs = await entityStore.GetCurrentBeliefsAsync(VendorId, ct);

        var briefingContext = new VendorCallBriefingContext(
            Meeting:                 meeting,
            VendorName:              NorthstarSeeder.VendorName,
            VendorId:                VendorId,
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

        var briefingComposer = new VendorCallBriefingComposer();   // Mode A (no LLM in harness)
        var briefing         = await briefingComposer.ComposeAsync(briefingContext, ct);
        var rendered         = BriefingTextRenderer.Render(briefing);

        Console.WriteLine();
        Console.WriteLine(rendered);

        // ── 8. Compose LLM-enhanced email version ──────────────────────────────
        Console.WriteLine();
        Console.WriteLine(new string('─', 64));
        Console.WriteLine("Composing email version (Mode A — no LLM configured in harness)...");
        Console.WriteLine(new string('─', 64));

        // Pass llm: null — the harness CachingLlmClient would throw LlmCacheMissException
        // for a prompt it has never seen. In production the API wires up a warm cache.
        var emailComposer = new BriefingEmailComposer(llm: null);
        var email         = await emailComposer.ComposeEmailAsync(briefing, rendered, ct);

        Console.WriteLine();
        Console.WriteLine($"Subject : {email.Subject}");
        Console.WriteLine($"Mode    : {(email.LlmEnhanced ? "Mode B (LLM-enhanced)" : "Mode A (deterministic fallback)")}");
        Console.WriteLine();
        Console.WriteLine(email.PlainTextBody);

        // ── 9. Send via Brevo SMTP (or null transport if credentials absent) ───
        Console.WriteLine();
        Console.WriteLine(new string('─', 64));
        await SendBriefingEmailAsync(email, OwnerEmail, config, ct);

        // ── 10. Create / update VendorCallRun (idempotent) ────────────────────
        Console.WriteLine();
        Console.WriteLine("Upserting VendorCallRun...");
        using var runStore = new SqliteVendorCallRunStore($"Data Source={dbPath}");

        var existing = await runStore.GetByEventIdAsync(SyntheticEventId, ct);
        if (existing is not null)
        {
            Console.WriteLine($"  VendorCallRun     : already exists (ID {existing.Id}) — skipping create");
        }
        else
        {
            var run = new VendorCallRun
            {
                Id                      = Guid.NewGuid(),
                EventId                 = SyntheticEventId,
                ICalUid                 = SyntheticEventId,
                JoinWebUrl              = meeting.JoinWebUrl,
                VendorId                = VendorId,
                VendorName              = NorthstarSeeder.VendorName,
                MeetingSubject          = meeting.Subject,
                StartUtc                = meeting.StartUtc,
                EndUtc                  = meeting.EndUtc,
                SignedInUserPrincipalId = userObjectId,
                Status                  = VendorCallStatus.BriefingSent,
                BriefingSentAt          = now,
                CreatedAt               = now,
                UpdatedAt               = now,
            };
            await runStore.SaveAsync(run, ct);
            Console.WriteLine($"  VendorCallRun     : created (ID {run.Id})");
            Console.WriteLine($"  Status            : {run.Status}");
            Console.WriteLine($"  JoinWebUrl        : {run.JoinWebUrl ?? "(none — synthetic event)"}");
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
            Console.WriteLine("[null transport] Briefing email not sent — Brevo not configured.");
            Console.WriteLine($"  To: {recipientEmail}");
            Console.WriteLine($"  Subject: {email.Subject}");
            Console.WriteLine($"  Body: {email.PlainTextBody.Length} chars");
            Console.WriteLine(new string('─', 64));
            Console.WriteLine("To enable sending, set Brevo:SmtpKey and Brevo:SenderEmail via user secrets:");
            Console.WriteLine("  dotnet user-secrets set \"Brevo:SmtpKey\" \"<key>\" --project tools/GraphAuthHarness");
            Console.WriteLine("  dotnet user-secrets set \"Brevo:SenderEmail\" \"<email>\" --project tools/GraphAuthHarness");
            Console.WriteLine(new string('─', 64));
            return;
        }

        var smtpHost    = Environment.GetEnvironmentVariable("BREVO_SMTP_HOST")  ?? "smtp-relay.brevo.com";
        var smtpPort    = int.TryParse(Environment.GetEnvironmentVariable("BREVO_SMTP_PORT"), out var p) ? p : 587;
        var smtpLogin   = config["Brevo:SmtpUser"]
                          ?? Environment.GetEnvironmentVariable("BREVO_SMTP_LOGIN")
                          ?? "9f924d001@smtp-brevo.com";
        var senderName  = Environment.GetEnvironmentVariable("BREVO_SENDER_NAME") ?? "Kozmo Briefings";

        Console.WriteLine($"Sending briefing email via Brevo SMTP...");
        Console.WriteLine($"  To      : {recipientEmail}");
        Console.WriteLine($"  Subject : {email.Subject}");

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(senderName, senderEmail));
        msg.To.Add(MailboxAddress.Parse(recipientEmail));
        msg.Subject = email.Subject;

        var bodyBuilder = new BodyBuilder
        {
            HtmlBody = email.HtmlBody,
            TextBody = email.PlainTextBody
        };
        msg.Body = bodyBuilder.ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(smtpHost, smtpPort, SecureSocketOptions.StartTls, ct);
        await smtp.AuthenticateAsync(smtpLogin, smtpKey, ct);
        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(quit: true, ct);

        Console.WriteLine($"  Sent    : OK ({email.PlainTextBody.Length} chars, LlmEnhanced={email.LlmEnhanced})");
        Console.WriteLine(new string('─', 64));
    }

    // ── Printing ──────────────────────────────────────────────────────────────

    private static void PrintBundle(VendorCallEvidenceBundle bundle)
    {
        Console.WriteLine();
        Console.WriteLine("Evidence bundle:");
        Console.WriteLine($"  Contracts           : {bundle.Contracts.Count}");
        Console.WriteLine($"  Prior meeting notes : {bundle.PriorMeetingNotes.Count}");
        Console.WriteLine($"  Open commitments    : {bundle.OpenCommitments.Count}");
        Console.WriteLine($"  Commercial signals  : {bundle.CommercialSignals.Count}");
        Console.WriteLine($"  Recent emails       : {bundle.RecentEmails.Count} (commercial)");
        Console.WriteLine($"  Noise emails        : {bundle.FilteredNoiseEmails.Count} (filtered)");
        Console.WriteLine($"  Evidence gaps       : {bundle.EvidenceGaps.Count}");
    }

    // ── Evidence + belief seeder ──────────────────────────────────────────────

    private static async Task SeedEvidenceAndBeliefsAsync(
        SqliteEntityStore store,
        SaasProfile       profile,
        string            fixtureFilePath,
        DateTimeOffset    asOf,
        CancellationToken ct)
    {
        var writeService = new VendorFileWriteService(store, profile);
        var fixtureJson  = await File.ReadAllTextAsync(fixtureFilePath, ct);
        var docs         = JsonDocument.Parse(fixtureJson).RootElement;

        var evidenceCount = 0;
        var beliefCount   = 0;

        foreach (var docEl in docs.EnumerateArray())
        {
            var ev = ParseEvidenceFromDoc(docEl);
            var observedAt = docEl.TryGetProperty("observed_at", out var oaProp)
                ? DateTimeOffset.Parse(oaProp.GetString()!, CultureInfo.InvariantCulture)
                : asOf;

            await store.AppendEvidenceAsync(ev, ct);
            evidenceCount++;

            if (!docEl.TryGetProperty("claims", out var claims)) continue;

            foreach (var claim in claims.EnumerateArray())
            {
                var claimKey = claim.GetProperty("claim_key").GetString() ?? "";
                if (string.IsNullOrEmpty(claimKey)) continue;
                if (!profile.ClaimKeyCatalogue.TryGetValue(claimKey, out var ckDef)) continue;

                var rawValue     = claim.GetProperty("raw_value").GetDouble();
                var normValue    = ckDef.ClaimClass == "structural"
                    ? rawValue
                    : Math.Clamp(rawValue, 0.0, 1.0);
                var extConf      = claim.TryGetProperty("extractor_confidence", out var ec)
                    ? ec.GetDouble() : 1.0;
                var locator      = claim.TryGetProperty("locator", out var loc)
                    ? loc.GetString() ?? "field:unknown" : "field:unknown";

                Enum.TryParse<Dimension>(ckDef.Dimension, ignoreCase: true, out var dimension);

                await writeService.WriteBeliefAsync(
                    vendorId:            ev.VendorId,
                    claimKey:            claimKey,
                    dimension:           dimension,
                    criterion:           claimKey,
                    rawValue:            normValue,
                    tier:                ev.SourceTier,
                    extractorConfidence: extConf,
                    observedAt:          observedAt,
                    provenance:          new BeliefProvenance(ev.EvidenceId, locator),
                    ingestedAt:          asOf,
                    ct:                  ct);
                beliefCount++;
            }
        }

        Console.WriteLine($"  Evidence records    : {evidenceCount} (idempotent — duplicates ignored)");
        Console.WriteLine($"  Beliefs written     : {beliefCount}");
    }

    private static Evidence ParseEvidenceFromDoc(JsonElement docEl)
    {
        var ev = docEl.GetProperty("evidence");
        return new Evidence(
            EvidenceId:  Guid.Parse(ev.GetProperty("evidence_id").GetString()!),
            VendorId:    Guid.Parse(ev.GetProperty("vendor_id").GetString()!),
            DocType:     Enum.Parse<DocType>(ev.GetProperty("doc_type").GetString()!, ignoreCase: true),
            SourceTier:  Enum.Parse<SourceTier>(ev.GetProperty("source_tier").GetString()!, ignoreCase: true),
            Ref:         ev.GetProperty("ref").GetString()!,
            DocVersion:  ev.TryGetProperty("doc_version", out var dv) ? dv.GetInt32() : 1,
            IngestedAt:  DateTimeOffset.Parse(ev.GetProperty("ingested_at").GetString()!,
                             CultureInfo.InvariantCulture));
    }

    // ── Path resolution ───────────────────────────────────────────────────────

    private static string ResolveDbPath()
    {
        var envPath = Environment.GetEnvironmentVariable("KOZMO_DB_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
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
        // First: look alongside the executable (CopyToOutputDirectory artefacts)
        var alongside = Path.Combine(AppContext.BaseDirectory, relative);
        if (File.Exists(alongside)) return alongside;

        // Walk up from cwd
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

        throw new FileNotFoundException(
            $"Catalogue file '{fileName}' not found. Walk up from {AppContext.BaseDirectory}.");
    }

    private static string ResolveCatalogueDir()
    {
        const string relativePart = "catalogue/profiles/saas";

        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, relativePart);
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        throw new DirectoryNotFoundException(
            $"Catalogue directory '{relativePart}' not found. Walk up from {AppContext.BaseDirectory}.");
    }
}

// ── Internal adapters ─────────────────────────────────────────────────────────

/// <summary>
/// Adapts ICheckInRowStore (Km.Store — BCL types) to ICheckInStore (Wc.Contracts — enum types).
/// Used in the harness to avoid pulling in Wc.CheckIn (which carries MailKit/Brevo).
/// </summary>
internal sealed class DirectCheckInStore : ICheckInStore
{
    private readonly ICheckInRowStore _rows;

    public DirectCheckInStore(ICheckInRowStore rows) => _rows = rows;

    public async Task SaveAsync(CheckIn c, CancellationToken ct = default)
        => await _rows.SaveCheckInAsync(ToRow(c), ct);

    public async Task<IReadOnlyList<CheckIn>> GetOpenAsync(CancellationToken ct = default)
    {
        var rows = await _rows.GetOpenCheckInsAsync(ct);
        return rows.Select(ToCheckIn).ToList();
    }

    public async Task<CheckIn?> GetAsync(Guid checkInId, CancellationToken ct = default)
    {
        var row = await _rows.GetCheckInAsync(checkInId, ct);
        return row is null ? null : ToCheckIn(row);
    }

    public async Task<IReadOnlyList<CheckIn>> GetResolvedForVendorAsync(Guid vendorId, CancellationToken ct = default)
    {
        var rows = await _rows.GetResolvedCheckInsForVendorAsync(vendorId, ct);
        return rows.Select(ToCheckIn).ToList();
    }

    private static CheckInRow ToRow(CheckIn c) => new(
        CheckInId:     c.CheckInId,
        VendorId:      c.VendorId,
        ProgramRunId:  c.ProgramRunId,
        Kind:          c.Kind.ToString(),
        Question:      c.Question,
        ResponseShape: c.ResponseShape.ToString(),
        TargetField:   c.TargetField,
        Owner:         c.Owner,
        Status:        c.Status.ToString(),
        RaisedAt:      c.RaisedAt,
        AnsweredAt:    c.AnsweredAt,
        ExpiresAt:     c.ExpiresAt,
        ResponseValue: c.ResponseValue,
        PairedVendorId: c.PairedVendorId);

    private static CheckIn ToCheckIn(CheckInRow r) => new(
        CheckInId:     r.CheckInId,
        VendorId:      r.VendorId,
        ProgramRunId:  r.ProgramRunId,
        Kind:          Enum.Parse<CheckInKind>(r.Kind),
        Question:      r.Question,
        ResponseShape: Enum.Parse<ResponseShape>(r.ResponseShape),
        TargetField:   r.TargetField,
        Owner:         r.Owner,
        Status:        Enum.Parse<PendingStatus>(r.Status),
        RaisedAt:      r.RaisedAt,
        AnsweredAt:    r.AnsweredAt,
        ExpiresAt:     r.ExpiresAt,
        ResponseValue: r.ResponseValue,
        PairedVendorId: r.PairedVendorId);
}

/// <summary>ICheckInTransport that does nothing — check-ins are persisted to DB only.</summary>
internal sealed class NullCheckInTransport : ICheckInTransport
{
    public Task SendAsync(IReadOnlyList<CheckIn> checkIns, CancellationToken ct = default)
        => Task.CompletedTask;
}
