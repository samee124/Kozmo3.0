using If.MicrosoftGraph;
using Kozmo.Llm;
using Kozmo.Llm.OpenAi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Po.VendorCall;
using Wc.CheckIn;
using Wc.Contracts;
using Wj.MeetingPulse;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((ctx, cfg) =>
    {
        cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
        cfg.AddUserSecrets<MeetingPulseWorker>(optional: true);
        cfg.AddEnvironmentVariables();
    })
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration;

        // ── Options ───────────────────────────────────────────────────────────
        services.Configure<MeetingPulseOptions>(
            config.GetSection(MeetingPulseOptions.Section));

        // ── Microsoft Graph ───────────────────────────────────────────────────
        var graphSection = config.GetSection("MicrosoftGraph");
        var graphOptions = new MicrosoftGraphOptions
        {
            TenantId       = graphSection["TenantId"]    ?? throw new InvalidOperationException("MicrosoftGraph:TenantId missing"),
            ClientId       = graphSection["ClientId"]    ?? throw new InvalidOperationException("MicrosoftGraph:ClientId missing"),
            ClientSecret   = graphSection["ClientSecret"] ?? throw new InvalidOperationException("MicrosoftGraph:ClientSecret missing"),
            RedirectUri    = graphSection["RedirectUri"] ?? "http://localhost:5050/auth/callback",
            Scopes         = graphSection.GetSection("Scopes").Get<List<string>>()
                             ?? ["Calendars.Read", "User.Read", "offline_access"],
            TokenCachePath = graphSection["TokenCachePath"],
        };
        services.AddSingleton(graphOptions);
        services.AddSingleton(new MicrosoftGraphTokenProvider(graphOptions));

        // ── Vendor call recognition ───────────────────────────────────────────
        var recognitionCfg = VendorCallRecognitionConfig.Load(
            ResolveCataloguePath("vendor_call_recognition.saas.v1.json"));
        services.AddSingleton(recognitionCfg);
        services.AddSingleton(new VendorCallRecognizer(recognitionCfg));

        // ── Question bank ─────────────────────────────────────────────────────
        IReadOnlyList<VendorCallQuestion> questionBank = VendorCallQuestionBank.Load(
            ResolveCataloguePath("vendor_call_questions.saas.v1.json"));
        services.AddSingleton(questionBank);

        // ── Post-meeting check-in transport (Brevo email) ────────────────────
        var ownerEmail  = config["MeetingPulse:OwnerEmail"] ?? "";
        var tokenSecret = config["CheckIn:TokenSecret"]
                          ?? Environment.GetEnvironmentVariable("KOZMO_CHECKIN_TOKEN_SECRET")
                          ?? "dev-secret-change-in-production";
        var apiBaseUrl  = Environment.GetEnvironmentVariable("KOZMO_API_BASE_URL") ?? "http://localhost:5000";
        var uiBaseUrl   = Environment.GetEnvironmentVariable("KOZMO_UI_BASE_URL")  ?? "http://localhost:3000";
        var tokenOptions = new CheckInTokenOptions(tokenSecret, 7, uiBaseUrl, apiBaseUrl);

        var smtpKey     = config["Brevo:SmtpKey"]     ?? Environment.GetEnvironmentVariable("BREVO_SMTP_KEY") ?? "";
        var senderEmail = config["Brevo:SenderEmail"] ?? Environment.GetEnvironmentVariable("BREVO_SENDER_EMAIL") ?? "";

        ICheckInTransport postMeetingTransport = !string.IsNullOrWhiteSpace(smtpKey)
            ? new BrevoCheckInTransport(
                smtpHost:          "smtp-relay.brevo.com",
                smtpPort:          587,
                smtpLogin:         config["Brevo:SmtpUser"] ?? "9f924d001@smtp-brevo.com",
                smtpKey:           smtpKey,
                senderEmail:       senderEmail,
                senderName:        "Kozmo Check-Ins",
                recipientResolver: _ => Task.FromResult<string?>(ownerEmail),
                recipientName:     "Kozmo User",
                tokenOptions:      tokenOptions)
            : new NullCheckInTransport();

        services.AddSingleton(tokenOptions);
        services.AddSingleton(postMeetingTransport);

        // ── LLM narrative client ──────────────────────────────────────────────
        // Builds a real OpenAI client when OPENAI_API_KEY is set and EnableLlmNarrative=true.
        // ReviewComposer falls back to Mode A deterministic text automatically when llm is null
        // or if any LLM call fails at runtime — no additional handling needed here.
        IKozmoLlm? reviewLlm = null;
        var enableLlmNarrative = config.GetValue("MeetingPulse:EnableLlmNarrative", defaultValue: true);
        if (enableLlmNarrative)
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var cachePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "review-llm-cache.json");
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                try { reviewLlm = new CachingLlmClient(cachePath, recordMode: true, inner: new OpenAiLlmClient()); }
                catch { /* API key invalid or service unavailable — stay null, Mode A fallback active */ }
            }
        }
        services.AddSingleton(new WorkerLlmProvider(reviewLlm));

        // ── Worker ────────────────────────────────────────────────────────────
        services.AddHostedService<MeetingPulseWorker>();
    })
    .Build();

await host.RunAsync();

// ── Path helpers ──────────────────────────────────────────────────────────────

static string ResolveCataloguePath(string fileName)
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
    throw new FileNotFoundException(
        $"Catalogue file '{fileName}' not found. " +
        "Run from the repository root or set --contentRoot.");
}
