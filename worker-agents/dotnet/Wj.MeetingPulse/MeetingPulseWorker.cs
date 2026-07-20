using If.Contracts;
using If.MicrosoftGraph;
using Ig.Resolution;
using Km.Store;
using Kozmo.Llm;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Po.VendorCall;
using Wc.Contracts;

namespace Wj.MeetingPulse;

/// <summary>
/// Background worker that drives the pre- and post-meeting vendor call pipeline in a
/// polling loop. Uses the persistent MSAL token cache (written by GraphAuthHarness on
/// interactive sign-in) to acquire Graph tokens without user interaction.
///
/// Cycle (every PollingIntervalMinutes):
///   1. Token   — TryAcquireSilentAsync; skip cycle if null (no cached session).
///   2. Discover — read calendar (next 14 days), recognize + match vendor meetings,
///                 create VendorCallRun rows for new meetings (status = Detected).
///   3. Pre-meeting — for Detected runs whose StartUtc is within the lead-time window,
///                    compose a briefing email and send it; status → BriefingSent.
///   4. Meeting-ended — advance BriefingSent/PreCheckInSent runs past their end + buffer
///                      to MeetingEnded.
///   5. Post-meeting — for MeetingEnded runs:
///                     • EnableTranscriptAnalysis=false (default): raise a post-meeting
///                       STATUS_SELECT check-in; status → PostCheckInSent.
///                     • EnableTranscriptAnalysis=true: fetch transcript; status → TranscriptReady.
///   6. Log — summary counts.
/// </summary>
public sealed class MeetingPulseWorker : BackgroundService
{
    private readonly MeetingPulseOptions            _options;
    private readonly MicrosoftGraphOptions          _graphOptions;
    private readonly MicrosoftGraphTokenProvider    _tokenProvider;
    private readonly VendorCallRecognitionConfig    _recognitionCfg;
    private readonly VendorCallRecognizer           _recognizer;
    private readonly IReadOnlyList<VendorCallQuestion> _questionBank;
    private readonly ICheckInTransport              _postMeetingTransport;
    private readonly IKozmoLlm?                     _llm;
    private readonly IConfiguration                 _config;
    private readonly ILogger<MeetingPulseWorker>    _logger;

    public MeetingPulseWorker(
        IOptions<MeetingPulseOptions>          options,
        MicrosoftGraphOptions                  graphOptions,
        MicrosoftGraphTokenProvider            tokenProvider,
        VendorCallRecognitionConfig            recognitionCfg,
        VendorCallRecognizer                   recognizer,
        IReadOnlyList<VendorCallQuestion>      questionBank,
        ICheckInTransport                      postMeetingTransport,
        WorkerLlmProvider                      llmProvider,
        IConfiguration                         config,
        ILogger<MeetingPulseWorker>            logger)
    {
        _options              = options.Value;
        _graphOptions         = graphOptions;
        _tokenProvider        = tokenProvider;
        _recognitionCfg       = recognitionCfg;
        _recognizer           = recognizer;
        _questionBank         = questionBank;
        _postMeetingTransport = postMeetingTransport;
        _llm                  = llmProvider.Llm;
        _config               = config;
        _logger               = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "MeetingPulse started. Polling every {Interval}s. TranscriptAnalysis={Flag}.",
            _options.PollingIntervalSeconds, _options.EnableTranscriptAnalysis);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cycle error — will retry in {Interval}s.",
                    _options.PollingIntervalSeconds);
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_options.PollingIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("MeetingPulse stopped.");
    }

    // ── Cycle ─────────────────────────────────────────────────────────────────

    internal async Task RunCycleAsync(CancellationToken ct)
    {
        // Step 1: Token — use specific account if configured, otherwise take any cached account
        // (the harness populates the shared cache on first interactive sign-in)
        TokenAcquisitionResult? token;
        if (string.IsNullOrWhiteSpace(_options.UserObjectId))
            token = await _tokenProvider.TryAcquireAnySilentAsync(ct);
        else
            token = await _tokenProvider.TryAcquireSilentAsync(_options.UserObjectId, ct);

        if (token is null)
        {
            _logger.LogWarning(
                "No cached token found. " +
                "Run 'dotnet run --project tools/GraphAuthHarness' once to sign in, then restart the worker.");
            return;
        }

        _logger.LogDebug("Token OK for {Upn} (expires {Expires:u}, scopes: {Scopes})",
            token.UserUpn, token.ExpiresOn, string.Join(' ', token.GrantedScopes));

        var now    = DateTimeOffset.UtcNow;
        var dbPath = ResolveDbPath();

        // Step 2: Discover (calendar window) + merge active runs from DB that have
        // dropped off the calendar window (past meetings still needing progression).
        var runs = await DiscoverAsync(token, now, dbPath, ct);
        var activeRuns = await LoadActiveRunsFromDbAsync(dbPath, ct);
        var seenIds    = new HashSet<Guid>(runs.Select(r => r.Id));
        var merged     = runs.ToList();
        foreach (var r in activeRuns)
            if (seenIds.Add(r.Id)) merged.Add(r);
        runs = merged;
        _logger.LogDebug("Cycle discovered {Count} tracked run(s) (incl. {Active} from DB).",
            runs.Count, activeRuns.Count);

        // Step 2.5: Cancellation check — skip runs whose calendar events were removed or cancelled.
        // Only check runs that are still in early-stage progression (before transcript fetch).
        var calSourceForCheck = new MicrosoftGraphCalendarSource(
            token.AccessToken, _options.UserObjectId, _tokenProvider, _graphOptions.TenantId);
        var stillActive = new List<VendorCallRun>();
        foreach (var run in runs)
        {
            if (run.Status is not (VendorCallStatus.Detected
                                or VendorCallStatus.BriefingSent
                                or VendorCallStatus.PreCheckInSent
                                or VendorCallStatus.MeetingEnded))
            {
                stillActive.Add(run);
                continue;
            }

            var rawId = ExtractGraphEventId(run.EventId);
            if (rawId is not null && await calSourceForCheck.IsEventCancelledOrDeletedAsync(rawId, ct))
            {
                run.Status    = VendorCallStatus.Cancelled;
                run.UpdatedAt = now;
                using var rs  = OpenRunStore(dbPath);
                await rs.SaveAsync(run, ct);
                _logger.LogWarning(
                    "Run cancelled — meeting removed from calendar: '{Subject}' ({Id})",
                    run.MeetingSubject, run.Id);
                continue; // Do not add to stillActive — no further processing
            }

            stillActive.Add(run);
        }
        runs = stillActive;

        // Step 3: Pre-meeting
        var leadTime = TimeSpan.FromHours(_options.PreMeetingLeadTimeHours);
        int pre = 0;
        foreach (var run in runs.Where(r => ShouldProcessPreMeeting(r, now, leadTime)))
        {
            await ProcessPreMeetingAsync(run, token, dbPath, now, ct);
            pre++;
        }

        // Step 4: Meeting-ended
        var buffer = TimeSpan.FromMinutes(_options.MeetingEndedBufferMinutes);
        int ended = 0;
        foreach (var run in runs.Where(r => ShouldAdvanceToMeetingEnded(r, now, buffer)))
        {
            run.Status    = VendorCallStatus.MeetingEnded;
            run.UpdatedAt = now;
            using var rs  = OpenRunStore(dbPath);
            await rs.SaveAsync(run, ct);
            _logger.LogInformation("Run advanced to MeetingEnded: {Subject}", run.MeetingSubject);
            ended++;
        }

        // Step 5: Post-meeting
        int post = 0;
        foreach (var run in runs.Where(ShouldProcessPostMeeting))
        {
            await ProcessPostMeetingAsync(run, token, dbPath, now, ct);
            post++;
        }

        // Step 6: Transcript-ready → compose + send post-meeting review email
        int transcriptProcessed = 0;
        foreach (var run in runs.Where(r => r.Status == VendorCallStatus.TranscriptReady))
        {
            await ProcessTranscriptReadyAsync(run, token, dbPath, now, ct);
            transcriptProcessed++;
        }

        _logger.LogInformation(
            "Cycle complete — detected:{Total} pre-meeting:{Pre} ended:{Ended} post:{Post} transcript:{Transcript}",
            runs.Count, pre, ended, post, transcriptProcessed);
    }

    // ── Pure decision helpers (testable without Graph or SMTP) ────────────────

    /// <summary>
    /// A Detected run should receive a pre-meeting brief when its start is within the
    /// lead-time window (i.e. it's coming up soon but hasn't started yet).
    /// </summary>
    public static bool ShouldProcessPreMeeting(
        VendorCallRun run, DateTimeOffset now, TimeSpan leadTime)
        => run.Status == VendorCallStatus.Detected
           && run.StartUtc > now
           && run.StartUtc <= now + leadTime;

    /// <summary>
    /// A run in BriefingSent or PreCheckInSent should advance to MeetingEnded once
    /// EndUtc + buffer has passed (i.e. the meeting is definitely over).
    /// </summary>
    public static bool ShouldAdvanceToMeetingEnded(
        VendorCallRun run, DateTimeOffset now, TimeSpan buffer)
        => (run.Status == VendorCallStatus.BriefingSent
            || run.Status == VendorCallStatus.PreCheckInSent)
           && run.EndUtc + buffer < now;

    /// <summary>Returns true when the run is ready for post-meeting processing.</summary>
    public static bool ShouldProcessPostMeeting(VendorCallRun run)
        => run.Status == VendorCallStatus.MeetingEnded;

    /// <summary>
    /// Extracts the raw Graph event ID from the stored EventId (format: "msgraph:event:{id}").
    /// Returns null if the format is not recognised.
    /// </summary>
    public static string? ExtractGraphEventId(string eventId)
        => eventId.StartsWith("msgraph:event:", StringComparison.Ordinal)
            ? eventId["msgraph:event:".Length..]
            : null;

    // ── Discover ──────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<VendorCallRun>> DiscoverAsync(
        TokenAcquisitionResult token,
        DateTimeOffset         now,
        string                 dbPath,
        CancellationToken      ct)
    {
        var window    = new CalendarWindow(now, now.AddDays(14));
        var calSource = new MicrosoftGraphCalendarSource(
            token.AccessToken, _options.UserObjectId, _tokenProvider, _graphOptions.TenantId);
        var events    = await calSource.GetEventsAsync(_options.OwnerEmail, window, ct);

        using var store    = new SqliteEntityStore($"Data Source={dbPath}");
        var       registry = new IdentityRegistry(store);
        var       lookup   = new IgVendorLookup(registry);
        var       matcher  = new VendorCallEntityMatcher(lookup);
        using var runStore = OpenRunStore(dbPath);

        var result = new List<VendorCallRun>();

        _logger.LogInformation("Calendar: {Count} event(s) in window.", events.Count);

        foreach (var ev in events)
        {
            var rec = _recognizer.Recognize(
                ev.Subject ?? "", ev.BodyPreview ?? "", ev.Attendees);
            if (!rec.IsRelevant)
            {
                _logger.LogInformation(
                    "Skipped '{Subject}' — not a vendor call (confidence {Score:F2}).",
                    ev.Subject, rec.Confidence);
                continue;
            }

            var matches = await matcher.MatchAsync(rec.ExternalAttendees, ct);
            foreach (var match in matches.Where(m => m.MatchType != VendorMatchType.Unmatched))
            {
                var existing = await runStore.GetByEventIdAsync(ev.ExternalId, ct);
                if (existing is not null)
                {
                    result.Add(existing);
                    continue;
                }

                var run = new VendorCallRun
                {
                    Id                      = Guid.NewGuid(),
                    EventId                 = ev.ExternalId,
                    ICalUid                 = ev.ICalUid,
                    JoinWebUrl              = ev.JoinWebUrl,
                    VendorId                = match.VendorId,
                    VendorName              = match.VendorName,
                    MeetingSubject          = ev.Subject ?? "",
                    StartUtc                = ev.StartUtc,
                    EndUtc                  = ev.EndUtc,
                    SignedInUserPrincipalId = _options.OwnerEmail,
                    Status                  = VendorCallStatus.Detected,
                    CreatedAt               = now,
                    UpdatedAt               = now,
                };
                await runStore.SaveAsync(run, ct);
                _logger.LogInformation(
                    "New run: '{Subject}' → {Vendor} (start {Start:yyyy-MM-dd HH:mm} UTC)",
                    run.MeetingSubject, run.VendorName, run.StartUtc);
                result.Add(run);
            }
        }

        return result;
    }

    // ── Pre-meeting ───────────────────────────────────────────────────────────

    private async Task ProcessPreMeetingAsync(
        VendorCallRun          run,
        TokenAcquisitionResult token,
        string                 dbPath,
        DateTimeOffset         now,
        CancellationToken      ct)
    {
        _logger.LogInformation(
            "Pre-meeting: '{Subject}' → {Vendor} (start {Start:yyyy-MM-dd HH:mm})",
            run.MeetingSubject, run.VendorName, run.StartUtc);

        using var store = new SqliteEntityStore($"Data Source={dbPath}");

        var artifact = BuildArtifact(run, now);
        var match    = new VendorEntityMatchResult(
            run.VendorId, run.VendorName, VendorMatchType.DomainExact, 0.95);
        var recipe   = LoadRecipe();
        var context  = new VendorCallContext(artifact, match, [], _options.OwnerEmail, recipe);

        // Collect evidence from Graph mail + entity store (seeded emails, contracts, beliefs)
        var mailSource = new MicrosoftGraphMailSource(
            token.AccessToken, _options.UserObjectId.Length > 0 ? _options.UserObjectId : token.UserObjectId,
            _tokenProvider, _graphOptions.TenantId);
        var collector  = new VendorCallEvidenceCollector(mailSource, store);
        var bundle     = await collector.CollectAsync(context, now, ct);

        // Raise pre-meeting check-in (persisted to DB only via NullCheckInTransport)
        var checkInStore  = new WorkerCheckInStore(store);
        var nullTransport = new NullCheckInTransport();
        var planner       = new VendorCallCheckInPlanner(_options.OwnerEmail);
        var dispatch      = await planner.PlanPreMeetingAsync(
            context, bundle, _questionBank, checkInStore, nullTransport, now, ct);

        _logger.LogDebug("Pre-meeting check-in: {Status}", dispatch.Status);

        // Compose pre-meeting review (Mode A — no LLM in worker)
        var beliefs = await store.GetCurrentBeliefsAsync(run.VendorId, ct);
        using var checkpointStore  = new SqliteReviewCheckpointStore($"Data Source={dbPath}");
        var previousCheckpoint     = await checkpointStore.GetLatestAsync(run.VendorId, ct);
        var reviewComposer         = new ReviewComposer(checkpointStore, llm: _llm);
        var result = await reviewComposer.ComposeAsync(
            vendorId:           run.VendorId,
            bundle:             bundle,
            beliefs:            beliefs,
            previousCheckpoint: previousCheckpoint,
            vendorName:         run.VendorName,
            ownerUpn:           _options.OwnerEmail,
            eventTypeCode:      recipe.MeetingType,
            now:                now,
            vendorCallRunId:    run.Id,
            kind:               CheckpointKind.PreMeeting,
            ct:                 ct);

        // Generate access token for evidence + update pages
        var reviewToken         = ReviewTokenGenerator.Generate();
        var apiBaseUrl          = _config["ApiBaseUrl"] ?? "";
        var minutesUntilMeeting = (int)Math.Max(0, (run.StartUtc - now).TotalMinutes);

        var emailCtx = new ReviewEmailContext(
            VendorName:             run.VendorName,
            ContractSummary:        ReviewEmailContextBuilder.BuildContractSummary(result.Q2Packet),
            MeetingTimeUtc:         run.StartUtc,
            PreviousReviewDateUtc:  previousCheckpoint?.CreatedAtUtc,
            RenewalStagePhrase:     recipe.MeetingType,
            ProposedSummary:        ReviewEmailContextBuilder.BuildProposedSummary(result.Q2Packet),
            CurrentPositionSummary: ReviewEmailContextBuilder.BuildCurrentPositionSummary(result.Q2Packet),
            Checkpoint:             result.Checkpoint,
            ViewEvidenceUrl:        $"{apiBaseUrl}/vendor-calls/{run.Id}/evidence?token={Uri.EscapeDataString(reviewToken)}",
            PostUpdateUrl:          $"{apiBaseUrl}/vendor-calls/{run.Id}/update?token={Uri.EscapeDataString(reviewToken)}",
            FlagUrl:                $"{apiBaseUrl}/vendor-calls/{run.Id}/flag?token={Uri.EscapeDataString(reviewToken)}");

        var rendered = new PreMeetingReviewEmailRenderer().Render(emailCtx, minutesUntilMeeting);
        await SendReviewEmailAsync(rendered, _options.OwnerEmail, ct);

        // Persist token + advance status
        using var runStore       = OpenRunStore(dbPath);
        run.ReviewToken          = reviewToken;
        run.ReviewTokenExpiresAt = ReviewTokenGenerator.ExpiresAt(now);
        run.Status               = VendorCallStatus.BriefingSent;
        run.BriefingSentAt       = now;
        run.UpdatedAt            = now;
        await runStore.SaveAsync(run, ct);
    }

    // ── Post-meeting ──────────────────────────────────────────────────────────

    private async Task ProcessPostMeetingAsync(
        VendorCallRun          run,
        TokenAcquisitionResult token,
        string                 dbPath,
        DateTimeOffset         now,
        CancellationToken      ct)
    {
        _logger.LogInformation(
            "Post-meeting: '{Subject}' → {Vendor} (EnableTranscript={Flag})",
            run.MeetingSubject, run.VendorName, _options.EnableTranscriptAnalysis);

        if (!_options.EnableTranscriptAnalysis)
        {
            await SendPostMeetingReviewAsync(run, token, dbPath, now, ct);
            return;
        }

        await FetchTranscriptAsync(run, token, dbPath, now, ct);
    }

    private async Task RaisePostCheckInAsync(
        VendorCallRun  run,
        string         dbPath,
        DateTimeOffset now,
        CancellationToken ct)
    {
        using var store = new SqliteEntityStore($"Data Source={dbPath}");
        var artifact    = BuildArtifact(run, now);
        var match       = new VendorEntityMatchResult(
            run.VendorId, run.VendorName, VendorMatchType.DomainExact, 0.95);
        var context     = new VendorCallContext(
            artifact, match, [], _options.OwnerEmail, LoadRecipe());

        var checkInStore = new WorkerCheckInStore(store);
        var planner      = new PostMeetingCheckInPlanner(_options.OwnerEmail);
        var dispatch     = await planner.PlanPostMeetingAsync(
            context, _questionBank, checkInStore, _postMeetingTransport, now, ct);

        _logger.LogInformation(
            "Post-meeting check-in: {Status} for '{Subject}'",
            dispatch.Status, run.MeetingSubject);

        using var runStore = OpenRunStore(dbPath);
        run.Status         = VendorCallStatus.PostCheckInSent;
        run.UpdatedAt      = now;
        await runStore.SaveAsync(run, ct);
    }

    private async Task SendPostMeetingReviewAsync(
        VendorCallRun          run,
        TokenAcquisitionResult token,
        string                 dbPath,
        DateTimeOffset         now,
        CancellationToken      ct)
    {
        using var store           = new SqliteEntityStore($"Data Source={dbPath}");
        using var checkpointStore = new SqliteReviewCheckpointStore($"Data Source={dbPath}");

        var beliefs            = await store.GetCurrentBeliefsAsync(run.VendorId, ct);
        var previousCheckpoint = await checkpointStore.GetLatestAsync(run.VendorId, ct);
        var recipe             = LoadRecipe();

        // Collect evidence via Graph mail + entity store (same as pre-meeting)
        var artifact   = BuildArtifact(run, now);
        var match      = new VendorEntityMatchResult(
            run.VendorId, run.VendorName, VendorMatchType.DomainExact, 0.95);
        var context    = new VendorCallContext(artifact, match, [], _options.OwnerEmail, recipe);
        var mailSource = new MicrosoftGraphMailSource(
            token.AccessToken,
            _options.UserObjectId.Length > 0 ? _options.UserObjectId : token.UserObjectId,
            _tokenProvider, _graphOptions.TenantId);
        var collector  = new VendorCallEvidenceCollector(mailSource, store);
        var bundle     = await collector.CollectAsync(context, now, ct);

        var composer = new ReviewComposer(checkpointStore, llm: _llm);
        var result   = await composer.ComposeAsync(
            vendorId:           run.VendorId,
            bundle:             bundle,
            beliefs:            beliefs,
            previousCheckpoint: previousCheckpoint,
            vendorName:         run.VendorName,
            ownerUpn:           _options.OwnerEmail,
            eventTypeCode:      recipe.MeetingType,
            now:                now,
            vendorCallRunId:    run.Id,
            kind:               CheckpointKind.PostMeeting,
            ct:                 ct);

        var reviewToken  = ReviewTokenGenerator.Generate();
        var apiBaseUrl   = _config["ApiBaseUrl"] ?? "";

        var emailCtx = new ReviewEmailContext(
            VendorName:             run.VendorName,
            ContractSummary:        ReviewEmailContextBuilder.BuildContractSummary(result.Q2Packet),
            MeetingTimeUtc:         run.EndUtc,
            PreviousReviewDateUtc:  previousCheckpoint?.CreatedAtUtc,
            RenewalStagePhrase:     recipe.MeetingType,
            ProposedSummary:        ReviewEmailContextBuilder.BuildProposedSummary(result.Q2Packet),
            CurrentPositionSummary: ReviewEmailContextBuilder.BuildCurrentPositionSummary(result.Q2Packet),
            Checkpoint:             result.Checkpoint,
            ViewEvidenceUrl:        $"{apiBaseUrl}/vendor-calls/{run.Id}/evidence?token={Uri.EscapeDataString(reviewToken)}",
            PostUpdateUrl:          $"{apiBaseUrl}/vendor-calls/{run.Id}/update?token={Uri.EscapeDataString(reviewToken)}",
            FlagUrl:                $"{apiBaseUrl}/vendor-calls/{run.Id}/flag?token={Uri.EscapeDataString(reviewToken)}");

        var rendered = new PostMeetingReviewEmailRenderer().Render(emailCtx);
        await SendReviewEmailAsync(rendered, _options.OwnerEmail, ct);

        using var runStore       = OpenRunStore(dbPath);
        run.ReviewToken          = reviewToken;
        run.ReviewTokenExpiresAt = ReviewTokenGenerator.ExpiresAt(now);
        run.Status               = VendorCallStatus.PostCheckInSent;
        run.UpdatedAt            = now;
        await runStore.SaveAsync(run, ct);

        _logger.LogInformation(
            "Post-meeting review email sent for '{Subject}'", run.MeetingSubject);
    }

    private async Task FetchTranscriptAsync(
        VendorCallRun          run,
        TokenAcquisitionResult token,
        string                 dbPath,
        DateTimeOffset         now,
        CancellationToken      ct)
    {
        if (string.IsNullOrWhiteSpace(run.JoinWebUrl))
        {
            _logger.LogWarning("Run {Id} has no JoinWebUrl — cannot fetch transcript.", run.Id);
            await AdvanceToNoTranscriptAsync(run, dbPath, now, ct);
            return;
        }

        var src = new MicrosoftGraphTranscriptSource(
            token.AccessToken, _options.UserObjectId, _tokenProvider, _graphOptions.TenantId);

        var meetingResult = await src.ResolveMeetingAsync(run.JoinWebUrl, ct);
        if (!meetingResult.Resolved)
        {
            _logger.LogWarning("Meeting resolve failed: {Reason}", meetingResult.FailureReason);
            await AdvanceToNoTranscriptAsync(run, dbPath, now, ct);
            return;
        }

        var transcriptResult = await src.CheckTranscriptAvailabilityAsync(
            meetingResult.MeetingId!, ct);
        if (!transcriptResult.Available)
        {
            var hoursWaited = (now - run.EndUtc).TotalHours;
            var maxWait     = _options.MaxTranscriptWaitHours;
            if (hoursWaited > maxWait)
            {
                _logger.LogWarning(
                    "Transcript not available after {Hours:F1}h for '{Subject}' (limit={Limit}h) — giving up.",
                    hoursWaited, run.MeetingSubject, maxWait);
                await AdvanceToNoTranscriptAsync(run, dbPath, now, ct);
                return;
            }
            _logger.LogInformation(
                "Transcript not yet available for '{Subject}': {Reason} ({Hours:F1}h / {Limit}h waited)",
                run.MeetingSubject, transcriptResult.FailureReason, hoursWaited, maxWait);
            // Stay MeetingEnded — will retry next cycle
            return;
        }

        _logger.LogInformation(
            "Transcript available for '{Subject}' — advancing to TranscriptReady.",
            run.MeetingSubject);

        using var runStore      = OpenRunStore(dbPath);
        run.OnlineMeetingId    = meetingResult.MeetingId;
        run.TranscriptId       = transcriptResult.TranscriptId;
        run.TranscriptFetchedAt = now;
        run.Status             = VendorCallStatus.TranscriptReady;
        run.UpdatedAt          = now;
        await runStore.SaveAsync(run, ct);
    }

    private async Task AdvanceToNoTranscriptAsync(
        VendorCallRun run, string dbPath, DateTimeOffset now, CancellationToken ct)
    {
        using var runStore = OpenRunStore(dbPath);
        run.Status    = VendorCallStatus.NoTranscriptAvailable;
        run.UpdatedAt = now;
        await runStore.SaveAsync(run, ct);
    }

    // ── Email ─────────────────────────────────────────────────────────────────

    private async Task SendBriefingEmailAsync(
        BriefingEmailContent email,
        string               recipient,
        CancellationToken    ct)
    {
        var smtpKey     = _config["Brevo:SmtpKey"]     ?? Environment.GetEnvironmentVariable("BREVO_SMTP_KEY");
        var senderEmail = _config["Brevo:SenderEmail"] ?? Environment.GetEnvironmentVariable("BREVO_SENDER_EMAIL");

        if (string.IsNullOrWhiteSpace(smtpKey) || string.IsNullOrWhiteSpace(senderEmail))
        {
            _logger.LogInformation(
                "[null transport] Briefing not sent — Brevo not configured. To: {To} Subject: {Subject}",
                recipient, email.Subject);
            return;
        }

        var smtpHost   = Environment.GetEnvironmentVariable("BREVO_SMTP_HOST")  ?? "smtp-relay.brevo.com";
        var smtpPort   = int.TryParse(Environment.GetEnvironmentVariable("BREVO_SMTP_PORT"), out var p) ? p : 587;
        var smtpLogin  = _config["Brevo:SmtpUser"]
                         ?? Environment.GetEnvironmentVariable("BREVO_SMTP_LOGIN")
                         ?? "9f924d001@smtp-brevo.com";
        var senderName = Environment.GetEnvironmentVariable("BREVO_SENDER_NAME") ?? "Kozmo Briefings";

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(senderName, senderEmail));
        msg.To.Add(MailboxAddress.Parse(recipient));
        msg.Subject = email.Subject;
        msg.Body    = new BodyBuilder
            { HtmlBody = email.HtmlBody, TextBody = email.PlainTextBody }
            .ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(smtpHost, smtpPort, SecureSocketOptions.StartTls, ct);
        await smtp.AuthenticateAsync(smtpLogin, smtpKey, ct);
        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(quit: true, ct);

        _logger.LogInformation(
            "Briefing sent to {To} ({Chars} chars)", recipient, email.PlainTextBody.Length);
    }

    /// <summary>
    /// Fetches VTT content, runs ReviewComposer (post-meeting), sends the post-meeting
    /// review email, and advances the run to PostSummarySent.
    /// </summary>
    private async Task ProcessTranscriptReadyAsync(
        VendorCallRun          run,
        TokenAcquisitionResult token,
        string                 dbPath,
        DateTimeOffset         now,
        CancellationToken      ct)
    {
        _logger.LogInformation(
            "Transcript-ready: composing post-meeting review for '{Subject}'", run.MeetingSubject);

        if (string.IsNullOrEmpty(run.OnlineMeetingId) || string.IsNullOrEmpty(run.TranscriptId))
        {
            _logger.LogWarning(
                "Run {Id} is TranscriptReady but missing OnlineMeetingId or TranscriptId — skipping.",
                run.Id);
            return;
        }

        // Fetch VTT content from Graph
        var src = new MicrosoftGraphTranscriptSource(
            token.AccessToken,
            _options.UserObjectId.Length > 0 ? _options.UserObjectId : token.UserObjectId,
            _tokenProvider, _graphOptions.TenantId);

        var content = await src.FetchTranscriptAsync(run.OnlineMeetingId, run.TranscriptId, ct);
        var parsed  = new TranscriptParser().Parse(content.RawContent);

        _logger.LogInformation(
            "Transcript parsed: {Segments} segments, {Speakers} speaker(s), {Words} words.",
            parsed.Segments.Count,
            parsed.Speakers.Count,
            parsed.TotalWordCount);

        // Compose post-meeting review using ReviewComposer
        using var store           = new SqliteEntityStore($"Data Source={dbPath}");
        using var checkpointStore = new SqliteReviewCheckpointStore($"Data Source={dbPath}");

        var beliefs            = await store.GetCurrentBeliefsAsync(run.VendorId, ct);
        var previousCheckpoint = await checkpointStore.GetLatestAsync(run.VendorId, ct);
        var recipe             = LoadRecipe();

        var artifact   = BuildArtifact(run, now);
        var match      = new VendorEntityMatchResult(
            run.VendorId, run.VendorName, VendorMatchType.DomainExact, 0.95);
        var context    = new VendorCallContext(artifact, match, [], _options.OwnerEmail, recipe);
        var mailSource = new MicrosoftGraphMailSource(
            token.AccessToken,
            _options.UserObjectId.Length > 0 ? _options.UserObjectId : token.UserObjectId,
            _tokenProvider, _graphOptions.TenantId);
        var collector  = new VendorCallEvidenceCollector(mailSource, store);
        var bundle     = await collector.CollectAsync(context, now, ct);

        // LLM transcript extraction — enrich the bundle with decisions/commitments/signals.
        // If _llm is null, ExtractAsync returns an empty result and the bundle is unchanged.
        var transcriptCtx = new TranscriptComprehensionContext(
            Run:                run,
            ParsedTranscript:   parsed,
            PreMeetingBriefing: null,
            OpenItemsFromBrief: []);

        var extraction = await new TranscriptComprehensionService(_llm).ExtractAsync(transcriptCtx, ct);

        _logger.LogInformation(
            "Transcript extraction: {Items} item(s), high={High}, confirmation={Confirm}, discarded={Discarded}.",
            extraction.Metadata.TotalItemsExtracted,
            extraction.Metadata.HighConfidenceCount,
            extraction.Metadata.RequiresConfirmationCount,
            extraction.Metadata.DiscardedLowConfidenceCount);

        var enrichedBundle = TranscriptEvidenceAdapter.Enrich(bundle, extraction, run.VendorId, now);

        var composer = new ReviewComposer(checkpointStore, llm: _llm);
        var result   = await composer.ComposeAsync(
            vendorId:           run.VendorId,
            bundle:             enrichedBundle,
            beliefs:            beliefs,
            previousCheckpoint: previousCheckpoint,
            vendorName:         run.VendorName,
            ownerUpn:           _options.OwnerEmail,
            eventTypeCode:      recipe.MeetingType,
            now:                now,
            vendorCallRunId:    run.Id,
            kind:               CheckpointKind.PostMeeting,
            ct:                 ct);

        var reviewToken = ReviewTokenGenerator.Generate();
        var apiBaseUrl  = _config["ApiBaseUrl"] ?? "";

        var emailCtx = new ReviewEmailContext(
            VendorName:             run.VendorName,
            ContractSummary:        ReviewEmailContextBuilder.BuildContractSummary(result.Q2Packet),
            MeetingTimeUtc:         run.EndUtc,
            PreviousReviewDateUtc:  previousCheckpoint?.CreatedAtUtc,
            RenewalStagePhrase:     recipe.MeetingType,
            ProposedSummary:        ReviewEmailContextBuilder.BuildProposedSummary(result.Q2Packet),
            CurrentPositionSummary: ReviewEmailContextBuilder.BuildCurrentPositionSummary(result.Q2Packet),
            Checkpoint:             result.Checkpoint,
            ViewEvidenceUrl:        $"{apiBaseUrl}/vendor-calls/{run.Id}/evidence?token={Uri.EscapeDataString(reviewToken)}",
            PostUpdateUrl:          $"{apiBaseUrl}/vendor-calls/{run.Id}/update?token={Uri.EscapeDataString(reviewToken)}",
            FlagUrl:                $"{apiBaseUrl}/vendor-calls/{run.Id}/flag?token={Uri.EscapeDataString(reviewToken)}");

        var rendered = new PostMeetingReviewEmailRenderer().Render(emailCtx);
        await SendReviewEmailAsync(rendered, _options.OwnerEmail, ct);

        using var runStore       = OpenRunStore(dbPath);
        run.ReviewToken          = reviewToken;
        run.ReviewTokenExpiresAt = ReviewTokenGenerator.ExpiresAt(now);
        run.PostSummarySentAt    = now;
        run.Status               = VendorCallStatus.PostSummarySent;
        run.UpdatedAt            = now;
        await runStore.SaveAsync(run, ct);

        _logger.LogInformation(
            "Post-meeting review email sent for '{Subject}' → {Status}", run.MeetingSubject, run.Status);
    }

    /// <summary>
    /// Loads runs in active progression states from the DB.
    /// These are runs that may have dropped off the calendar window (past meetings)
    /// but still need to advance through the pipeline.
    /// </summary>
    private async Task<IReadOnlyList<VendorCallRun>> LoadActiveRunsFromDbAsync(
        string dbPath, CancellationToken ct)
    {
        using var store = OpenRunStore(dbPath);
        var active = new List<VendorCallRun>();
        foreach (var status in new[]
        {
            VendorCallStatus.Detected,
            VendorCallStatus.BriefingSent,
            VendorCallStatus.PreCheckInSent,
            VendorCallStatus.MeetingEnded,
            VendorCallStatus.TranscriptReady,
        })
        {
            var rows = await store.GetByStatusAsync(status, ct);
            active.AddRange(rows);
        }
        return active;
    }

    private async Task SendReviewEmailAsync(
        RenderedEmail     email,
        string            recipient,
        CancellationToken ct)
    {
        var smtpKey     = _config["Brevo:SmtpKey"]     ?? Environment.GetEnvironmentVariable("BREVO_SMTP_KEY");
        var senderEmail = _config["Brevo:SenderEmail"] ?? Environment.GetEnvironmentVariable("BREVO_SENDER_EMAIL");

        if (string.IsNullOrWhiteSpace(smtpKey) || string.IsNullOrWhiteSpace(senderEmail))
        {
            _logger.LogInformation(
                "[null transport] Review email not sent — Brevo not configured. To: {To} Subject: {Subject}",
                recipient, email.Subject);
            return;
        }

        var smtpHost   = Environment.GetEnvironmentVariable("BREVO_SMTP_HOST")  ?? "smtp-relay.brevo.com";
        var smtpPort   = int.TryParse(Environment.GetEnvironmentVariable("BREVO_SMTP_PORT"), out var p) ? p : 587;
        var smtpLogin  = _config["Brevo:SmtpUser"]
                         ?? Environment.GetEnvironmentVariable("BREVO_SMTP_LOGIN")
                         ?? "9f924d001@smtp-brevo.com";
        var senderName = Environment.GetEnvironmentVariable("BREVO_SENDER_NAME") ?? "Kozmo Briefings";

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(senderName, senderEmail));
        msg.To.Add(MailboxAddress.Parse(recipient));
        msg.Subject = email.Subject;
        msg.Body    = new BodyBuilder
            { HtmlBody = email.HtmlBody, TextBody = email.PlainTextBody }
            .ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(smtpHost, smtpPort, SecureSocketOptions.StartTls, ct);
        await smtp.AuthenticateAsync(smtpLogin, smtpKey, ct);
        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(quit: true, ct);

        _logger.LogInformation(
            "Review email sent to {To} ({Chars} chars)", recipient, email.PlainTextBody.Length);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CalendarArtifact BuildArtifact(VendorCallRun run, DateTimeOffset now)
        => new(
            ArtifactId:        Guid.NewGuid(),
            SourceSystem:      "Microsoft.Graph",
            SourceType:        "OnlineMeeting",
            TenantId:          "",
            SourcePrincipalId: run.SignedInUserPrincipalId,
            ExternalId:        run.EventId,
            ICalUid:           run.ICalUid ?? "",
            Subject:           run.MeetingSubject,
            StartUtc:          run.StartUtc,
            EndUtc:            run.EndUtc,
            Organizer:         run.SignedInUserPrincipalId,
            Attendees:         [],
            BodyPreview:       "",
            CapturedAtUtc:     now,
            JoinWebUrl:        run.JoinWebUrl);

    private VendorCallRecipe LoadRecipe()
        => VendorCallRecipe.Load(ResolveCataloguePath("vendor_call_recipe.saas.v1.json"));

    private string ResolveDbPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.DbPath))
            return Path.GetFullPath(_options.DbPath);

        var envPath = Environment.GetEnvironmentVariable("KOZMO_DB_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        var dir = Directory.GetCurrentDirectory();
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

    private static string ResolveCataloguePath(string fileName)
    {
        const string rel = "catalogue/profiles/saas";
        var dir = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, rel, fileName);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException($"Catalogue file '{fileName}' not found.");
    }

    private static SqliteVendorCallRunStore OpenRunStore(string dbPath)
        => new($"Data Source={dbPath}");
}

// ── Internal adapters ─────────────────────────────────────────────────────────

/// <summary>
/// Adapts ICheckInRowStore (Km.Store) to ICheckInStore (Wc.Contracts) without
/// pulling in Wc.CheckIn (which carries MailKit / Brevo). Check-ins are persisted
/// to SQLite; the worker uses NullCheckInTransport so no emails are dispatched for
/// the pre-meeting check-in (status is visible via the API /check-ins endpoint).
/// </summary>
internal sealed class WorkerCheckInStore : ICheckInStore
{
    private readonly ICheckInRowStore _rows;

    public WorkerCheckInStore(ICheckInRowStore rows) => _rows = rows;

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

    public async Task<IReadOnlyList<CheckIn>> GetResolvedForVendorAsync(
        Guid vendorId, CancellationToken ct = default)
    {
        var rows = await _rows.GetResolvedCheckInsForVendorAsync(vendorId, ct);
        return rows.Select(ToCheckIn).ToList();
    }

    private static CheckInRow ToRow(CheckIn c) => new(
        CheckInId:      c.CheckInId,
        VendorId:       c.VendorId,
        ProgramRunId:   c.ProgramRunId,
        Kind:           c.Kind.ToString(),
        Question:       c.Question,
        ResponseShape:  c.ResponseShape.ToString(),
        TargetField:    c.TargetField,
        Owner:          c.Owner,
        Status:         c.Status.ToString(),
        RaisedAt:       c.RaisedAt,
        AnsweredAt:     c.AnsweredAt,
        ExpiresAt:      c.ExpiresAt,
        ResponseValue:  c.ResponseValue,
        PairedVendorId: c.PairedVendorId);

    private static CheckIn ToCheckIn(CheckInRow r) => new(
        CheckInId:      r.CheckInId,
        VendorId:       r.VendorId,
        ProgramRunId:   r.ProgramRunId,
        Kind:           Enum.Parse<CheckInKind>(r.Kind),
        Question:       r.Question,
        ResponseShape:  Enum.Parse<ResponseShape>(r.ResponseShape),
        TargetField:    r.TargetField,
        Owner:          r.Owner,
        Status:         Enum.Parse<PendingStatus>(r.Status),
        RaisedAt:       r.RaisedAt,
        AnsweredAt:     r.AnsweredAt,
        ExpiresAt:      r.ExpiresAt,
        ResponseValue:  r.ResponseValue,
        PairedVendorId: r.PairedVendorId);
}

/// <summary>ICheckInTransport that does nothing — check-ins are persisted to DB only.</summary>
internal sealed class NullCheckInTransport : ICheckInTransport
{
    public Task SendAsync(IReadOnlyList<CheckIn> checkIns, CancellationToken ct = default)
        => Task.CompletedTask;
}
