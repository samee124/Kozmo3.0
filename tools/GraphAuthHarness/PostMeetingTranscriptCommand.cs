#pragma warning disable CS0618 // obsolete pre-Review-pipeline composers kept for reference
using System.Text.Json;
using If.MicrosoftGraph;
using Kozmo.Llm;
using Kozmo.Llm.OpenAi;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using Po.VendorCall;

namespace GraphAuthHarness;

/// <summary>
/// Harness command: post-meeting-transcript
///
/// Flow:
///   1–8.  Find VendorCallRun → fetch transcript → parse → extract (Phases 9a/9b)
///   9.    Compose post-meeting summary (deterministic)
///   10.   Render to text and print to console
///   11.   Compose email version (LLM-enhanced if OPENAI_API_KEY is set)
///   12.   Generate review token and build review URL
///   13.   Send via Brevo (null-transport if credentials absent)
///   14.   Advance VendorCallRun status to PostSummarySent
/// </summary>
public static class PostMeetingTranscriptCommand
{
    private static readonly Guid VendorId = NorthstarSeeder.VendorId;
    private const string OwnerEmail = "rishi@econtracts.onmicrosoft.com";
    private const string ReviewBaseUrl = "http://localhost:5050";

    // Pre-meeting open items hard-coded for the Northstar demo scenario
    private static readonly string[] NorthstarOpenItems =
    [
        "7% pricing uplift",
        "SLA compliance report overdue",
        "License utilization review",
        "SOC 2 certificate overdue",
        "Renewal notice deadline Jul 30"
    ];

    public static async Task RunAsync(
        string                      accessToken,
        string                      userObjectId,
        MicrosoftGraphTokenProvider tokenProvider,
        string                      tenantId,
        IConfiguration              config,
        CancellationToken           ct = default)
    {
        Console.WriteLine();
        Console.WriteLine(new string('─', 64));
        Console.WriteLine("Post-meeting transcript fetch + extraction (Phase 9a/9b/9c)");
        Console.WriteLine(new string('─', 64));

        var dbPath = ResolveDbPath();
        Console.WriteLine($"DB path             : {dbPath}");

        // ── 1. Locate VendorCallRun ───────────────────────────────────────────
        using var runStore = new SqliteVendorCallRunStore($"Data Source={dbPath}");
        var runs = await runStore.GetByVendorIdAsync(VendorId, ct);

        if (runs.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("[ERROR] No VendorCallRun found for Northstar vendor.");
            Console.WriteLine("Run 'seed-and-prepare-northstar' first to create one.");
            return;
        }

        var run = runs.OrderByDescending(r => r.CreatedAt).First();

        Console.WriteLine($"VendorCallRun       : {run.Id}");
        Console.WriteLine($"Status              : {run.Status}");
        Console.WriteLine($"EventId             : {run.EventId}");
        Console.WriteLine($"JoinWebUrl          : {run.JoinWebUrl ?? "(none)"}");

        // ── 2. Validate JoinWebUrl ────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(run.JoinWebUrl))
        {
            Console.WriteLine();
            Console.WriteLine("[INFO] No Teams join URL on this run — it was created from a synthetic");
            Console.WriteLine("       calendar event (no live Graph read). Full transcript fetch requires");
            Console.WriteLine("       a real Teams meeting. Check your calendar for a completed meeting");
            Console.WriteLine("       and re-run seed-and-prepare-northstar with a live Graph event.");
            return;
        }

        // ── 3. Resolve online meeting ID ──────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("Resolving online meeting from Graph...");
        Console.WriteLine($"  JoinWebUrl: {run.JoinWebUrl[..Math.Min(80, run.JoinWebUrl.Length)]}...");

        var transcriptSource = new MicrosoftGraphTranscriptSource(
            accessToken, userObjectId, tokenProvider, tenantId);

        var resolution = await transcriptSource.ResolveMeetingAsync(run.JoinWebUrl, ct);

        if (!resolution.Resolved)
        {
            Console.WriteLine($"[ERROR] Could not resolve meeting: {resolution.FailureReason}");
            Console.WriteLine("        Ensure the meeting occurred and the account has OnlineMeetings.Read.");
            return;
        }

        var meetingId = resolution.MeetingId!;
        Console.WriteLine($"  Online meeting ID : {meetingId}");

        if (run.OnlineMeetingId != meetingId)
        {
            run.OnlineMeetingId = meetingId;
            await runStore.SaveAsync(run, ct);
        }

        // ── 4. Check transcript availability ──────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("Checking transcript availability...");

        var availability = await transcriptSource.CheckTranscriptAvailabilityAsync(meetingId, ct);

        if (!availability.Available)
        {
            Console.WriteLine($"[INFO] Transcript not available: {availability.FailureReason}");
            Console.WriteLine("       Teams typically takes 5-10 minutes after a meeting to process.");
            run.Status    = VendorCallStatus.TranscriptPending;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await runStore.SaveAsync(run, ct);
            return;
        }

        Console.WriteLine($"  Transcript ID     : {availability.TranscriptId}");
        Console.WriteLine($"  Created at        : {availability.CreatedAt:u}");

        // ── 5. Fetch transcript content ───────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("Fetching transcript content...");

        var content = await transcriptSource.FetchTranscriptAsync(meetingId, availability.TranscriptId!, ct);

        Console.WriteLine($"  Format            : {content.Format}");
        Console.WriteLine($"  Raw length        : {content.RawContent.Length:N0} chars");
        Console.WriteLine($"  Fetched at        : {content.FetchedAt:u}");

        // ── 6. Parse VTT ──────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("Parsing transcript...");

        var parser = new TranscriptParser();
        var parsed = parser.Parse(content.RawContent);

        Console.WriteLine($"  Segments          : {parsed.Segments.Count}");
        Console.WriteLine($"  Speakers          : {string.Join(", ", parsed.Speakers)}");
        Console.WriteLine($"  Total duration    : {parsed.TotalDuration:hh\\:mm\\:ss}");
        Console.WriteLine($"  Total words       : {parsed.TotalWordCount:N0}");

        // Print first 10 segments
        Console.WriteLine();
        Console.WriteLine(new string('─', 64));
        Console.WriteLine("First 10 transcript segments:");
        Console.WriteLine(new string('─', 64));

        foreach (var seg in parsed.Segments.Take(10))
        {
            var start = FormatTs(seg.StartTime);
            var end   = FormatTs(seg.EndTime);
            var text  = seg.Text.Length > 120 ? seg.Text[..120] + "..." : seg.Text;
            Console.WriteLine($"[{start} → {end}] {seg.Speaker}:");
            Console.WriteLine($"  {text}");
        }

        // Update run to TranscriptReady
        run.TranscriptId        = availability.TranscriptId;
        run.TranscriptFetchedAt = content.FetchedAt;
        run.Status              = VendorCallStatus.TranscriptReady;
        run.UpdatedAt           = DateTimeOffset.UtcNow;
        await runStore.SaveAsync(run, ct);

        // ── 7. LLM extraction (Phase 9b) ──────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine(new string('─', 64));

        var llm = BuildLlm();
        TranscriptExtractionResult extraction;

        if (llm is null)
        {
            Console.WriteLine("Transcript extraction: SKIPPED");
            Console.WriteLine("  Set OPENAI_API_KEY environment variable to enable LLM extraction.");
            Console.WriteLine($"  VendorCallRun status: {run.Status}");

            // Use empty extraction so we can still compose + send the summary
            extraction = new TranscriptExtractionResult(
                [],
                [],
                new TranscriptExtractionMetadata(parsed.Segments.Count, 0, 0, 0, 0, TimeSpan.Zero));
        }
        else
        {
            Console.WriteLine("Running transcript extraction...");
            Console.WriteLine($"  LLM              : {llm.GetType().Name}");

            var transcriptContext = new TranscriptComprehensionContext(
                Run:                run,
                ParsedTranscript:   parsed,
                PreMeetingBriefing: null,
                OpenItemsFromBrief: NorthstarOpenItems);

            var svc = new TranscriptComprehensionService(llm);
            extraction = await svc.ExtractAsync(transcriptContext, ct);

            PrintExtraction(extraction);

            run.TranscriptAnalyzedAt = DateTimeOffset.UtcNow;
            run.Status               = VendorCallStatus.TranscriptAnalyzed;
            run.UpdatedAt            = DateTimeOffset.UtcNow;
            await runStore.SaveAsync(run, ct);
        }

        // ── 8. Compose post-meeting summary (deterministic) ───────────────────
        Console.WriteLine();
        Console.WriteLine(new string('─', 64));
        Console.WriteLine("Composing post-meeting summary (Mode A — deterministic)...");

        var summaryContext = new PostMeetingSummaryContext(
            Run:               run,
            Extraction:        extraction,
            PreMeetingBriefing: null,
            EvidenceBundle:    null);

        var composer = new PostMeetingSummaryComposer();
        var summary  = composer.Compose(summaryContext);

        // Persist summary JSON so the review page can deserialise it
        run.SummaryJson = JsonSerializer.Serialize(summary);

        // ── 9. Render to text and print ───────────────────────────────────────
        var rendered = PostMeetingSummaryTextRenderer.Render(summary);
        Console.WriteLine();
        Console.WriteLine(rendered);

        // ── 10. Compose email version ─────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine(new string('─', 64));

        // Generate review token before composing email (token goes in URL)
        var reviewToken  = ReviewTokenGenerator.Generate();
        var tokenExpiry  = ReviewTokenGenerator.ExpiresAt(DateTimeOffset.UtcNow);
        var reviewUrl    = $"{ReviewBaseUrl}/vendor-calls/{run.Id}/review?token={reviewToken}";

        Console.WriteLine($"Review token        : {reviewToken[..8]}... (expires {tokenExpiry:u})");
        Console.WriteLine($"Review URL          : {reviewUrl}");

        var emailComposer = new PostMeetingEmailComposer(llm);   // llm may be null (Mode A)
        var email         = await emailComposer.ComposeEmailAsync(summary, rendered, reviewUrl, ct);

        Console.WriteLine();
        Console.WriteLine($"Subject : {email.Subject}");
        Console.WriteLine($"Mode    : {(email.LlmEnhanced ? "Mode B (LLM-enhanced)" : "Mode A (deterministic)")}");
        Console.WriteLine();
        Console.WriteLine(email.PlainTextBody);

        // ── 11. Send via Brevo ────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine(new string('─', 64));
        await SendPostMeetingEmailAsync(email, OwnerEmail, config, ct);

        // ── 12. Persist token + advance status ────────────────────────────────
        run.ReviewToken          = reviewToken;
        run.ReviewTokenExpiresAt = tokenExpiry;
        run.PostSummarySentAt    = DateTimeOffset.UtcNow;
        run.Status               = VendorCallStatus.PostSummarySent;
        run.UpdatedAt            = DateTimeOffset.UtcNow;
        await runStore.SaveAsync(run, ct);

        Console.WriteLine();
        Console.WriteLine($"Post-meeting summary sent to {OwnerEmail} ✓");
        Console.WriteLine($"Review link: {reviewUrl}");
        Console.WriteLine($"VendorCallRun updated: Status={run.Status}");
    }

    // ── Printing ──────────────────────────────────────────────────────────────

    private static void PrintExtraction(TranscriptExtractionResult result)
    {
        var m = result.Metadata;
        Console.WriteLine();
        Console.WriteLine(new string('─', 64));
        Console.WriteLine("TRANSCRIPT EXTRACTION");
        Console.WriteLine(new string('─', 64));
        Console.WriteLine($"Total items extracted : {m.TotalItemsExtracted}");
        Console.WriteLine($"High confidence (auto): {m.HighConfidenceCount}");
        Console.WriteLine($"Requires confirmation : {m.RequiresConfirmationCount}");
        Console.WriteLine($"Discarded             : {m.DiscardedLowConfidenceCount}");
        Console.WriteLine($"Processing duration   : {m.ProcessingDuration.TotalSeconds:F1}s");

        foreach (var typeGroup in result.Items
            .GroupBy(i => i.Type)
            .OrderBy(g => g.Key.ToString()))
        {
            Console.WriteLine();
            Console.WriteLine($"{typeGroup.Key.ToString().ToUpperInvariant().Replace("_", " ")}S:");
            foreach (var item in typeGroup.OrderByDescending(i => i.Confidence))
            {
                var ts      = FormatTs(item.TranscriptTimestamp);
                var confirm = item.RequiresUserConfirmation ? " ⚠ REQUIRES CONFIRMATION" : "";
                Console.WriteLine($"  [T:{ts}] {item.Description}");
                Console.WriteLine($"    Speaker: {item.Speaker}{(item.Owner is not null ? $" | Owner: {item.Owner}" : "")}{(item.DueDate is not null ? $" | Due: {item.DueDate}" : "")}");
                Console.WriteLine($"    Confidence: {item.Confidence:F2} | Claim: {item.ClaimKey}{confirm}");
                if (!string.IsNullOrEmpty(item.Quote))
                    Console.WriteLine($"    Quote: \"{item.Quote[..Math.Min(100, item.Quote.Length)]}{(item.Quote.Length > 100 ? "..." : "")}\"");
            }
        }

        if (result.ResolvedPreBriefItems.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(new string('─', 64));
            Console.WriteLine("PRE-BRIEF ITEM RESOLUTION:");
            foreach (var res in result.ResolvedPreBriefItems)
            {
                var status = res.AddressedInMeeting
                    ? $"✓ [{FormatTs(res.TranscriptTimestamp ?? TimeSpan.Zero)}] {res.TranscriptEvidence}"
                    : "✗ not addressed in transcript";
                Console.WriteLine($"  {res.PreBriefItem} — {status}");
            }
        }

        Console.WriteLine(new string('─', 64));
    }

    // ── Brevo send ────────────────────────────────────────────────────────────

    private static async Task SendPostMeetingEmailAsync(
        PostMeetingEmailContent email,
        string                  recipientEmail,
        IConfiguration          config,
        CancellationToken       ct)
    {
        var smtpKey     = config["Brevo:SmtpKey"]     ?? Environment.GetEnvironmentVariable("BREVO_SMTP_KEY");
        var senderEmail = config["Brevo:SenderEmail"] ?? Environment.GetEnvironmentVariable("BREVO_SENDER_EMAIL");

        if (string.IsNullOrWhiteSpace(smtpKey) || string.IsNullOrWhiteSpace(senderEmail))
        {
            Console.WriteLine("[null transport] Post-meeting email not sent — Brevo not configured.");
            Console.WriteLine($"  To      : {recipientEmail}");
            Console.WriteLine($"  Subject : {email.Subject}");
            Console.WriteLine($"  Body    : {email.PlainTextBody.Length} chars");
            Console.WriteLine(new string('─', 64));
            Console.WriteLine("To enable sending, set Brevo credentials via user secrets:");
            Console.WriteLine("  dotnet user-secrets set \"Brevo:SmtpKey\" \"<key>\" --project tools/GraphAuthHarness");
            Console.WriteLine("  dotnet user-secrets set \"Brevo:SenderEmail\" \"<email>\" --project tools/GraphAuthHarness");
            Console.WriteLine(new string('─', 64));
            return;
        }

        var smtpHost   = Environment.GetEnvironmentVariable("BREVO_SMTP_HOST")  ?? "smtp-relay.brevo.com";
        var smtpPort   = int.TryParse(Environment.GetEnvironmentVariable("BREVO_SMTP_PORT"), out var p) ? p : 587;
        var smtpLogin  = config["Brevo:SmtpUser"]
                         ?? Environment.GetEnvironmentVariable("BREVO_SMTP_LOGIN")
                         ?? "9f924d001@smtp-brevo.com";
        var senderName = Environment.GetEnvironmentVariable("BREVO_SENDER_NAME") ?? "Kozmo Post-Meeting";

        Console.WriteLine($"Sending post-meeting email via Brevo SMTP...");
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

    // ── LLM factory ───────────────────────────────────────────────────────────

    private static IKozmoLlm? BuildLlm()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var cachePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "transcript-extraction-cache.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        return new CachingLlmClient(cachePath, recordMode: true, inner: new OpenAiLlmClient());
    }

    private static string FormatTs(TimeSpan ts) =>
        $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";

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
}
