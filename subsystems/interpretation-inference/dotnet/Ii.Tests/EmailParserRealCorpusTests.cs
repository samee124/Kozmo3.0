using Ii.CandidateExtraction;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// E-signal Part 5 Step 2 — MIME parsing + ingestion. Proves <see cref="EmailParser"/> parses the
/// real 338-.eml workspace corpus into clean structured objects: sender, recipients, date, subject
/// (RFC 2047 decoded), body (transfer-encoding decoded), thread/reference headers. No
/// interpretation, no belief/signal production, no LLM call — pure parsing, proven on real data
/// per the corpus-diff/real-data acceptance discipline (Kozmo_Phase_E_Signal_Spec.md Part 7).
/// </summary>
public sealed class EmailParserRealCorpusTests
{
    private static readonly string Workspace = @"D:\June\Kozmo Workspace";

    [SkippableFact]
    public void AllRealEmails_ParseWithoutError()
    {
        Skip.If(!Directory.Exists(Workspace), $"Workspace absent: '{Workspace}'.");

        var emlPaths = Directory.EnumerateFiles(Workspace, "*.eml", SearchOption.AllDirectories).ToList();

        // Pins the corpus audit's finding (Kozmo_Phase_E_Signal_Spec.md §1.1) — 338 real .eml files.
        Assert.Equal(338, emlPaths.Count);

        var failures = new List<string>();
        var parsed = new List<ParsedEmail>();

        foreach (var path in emlPaths)
        {
            try
            {
                parsed.Add(EmailParser.ParseFile(path));
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetRelativePath(Workspace, path)}: {ex.GetType().Name} — {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{emlPaths.Count} .eml files failed to parse:\n" + string.Join("\n", failures));

        Assert.Equal(emlPaths.Count, parsed.Count);

        // Sanity: every parsed message carries a real sender and non-empty body — not silently
        // parsing to an empty shell.
        Assert.All(parsed, p => Assert.False(string.IsNullOrWhiteSpace(p.From?.Address)));
        Assert.All(parsed, p => Assert.False(string.IsNullOrWhiteSpace(p.Body)));
    }

    [SkippableFact]
    public void RealScenario07Email_ParsesExpectedStructure()
    {
        Skip.If(!Directory.Exists(Workspace), $"Workspace absent: '{Workspace}'.");

        var path = Path.Combine(Workspace, "Scenario 07 — Email-Driven Relationship",
            "300 .eml files spanning 3 years", "0001_demo_followup.eml");
        Skip.If(!File.Exists(path), $"Sample email absent: '{path}'.");

        var email = EmailParser.ParseFile(path);

        Assert.Equal("yoni.rouache@officespacesoftware.com", email.From?.Address);
        Assert.Equal("officespacesoftware.com", email.From?.Domain);

        var to = Assert.Single(email.To);
        Assert.Equal("renee.mallen@brookfield.com", to.Address);
        Assert.Equal("brookfield.com", to.Domain);
        Assert.Empty(email.Cc);

        // RFC 2047 encoded-word Subject decoded (source header: "=?utf-8?q?...=E2=80=94...?=").
        Assert.Equal("OfficeSpace Software — Workplace Management Demo Follow-Up", email.Subject);

        Assert.Equal(new DateTimeOffset(2022, 1, 10, 9, 35, 0, TimeSpan.Zero), email.Date);
        Assert.Equal("0001-demo_followup@mail.officespacesoftware.com", email.MessageId);
        Assert.Null(email.InReplyTo);
        Assert.Empty(email.References);

        Assert.Contains("Great speaking with you and the team this morning", email.Body);
        Assert.Contains("Yoni Rouache", email.Body);
    }

    [SkippableFact]
    public void RealMultipartBase64Email_DecodesTransferEncodingAndEncodedSubject()
    {
        Skip.If(!Directory.Exists(Workspace), $"Workspace absent: '{Workspace}'.");

        var path = Path.Combine(Workspace, "Scenario 01 — Golden Vendor", "Emails",
            "01_MSA_Execution_Confirmation_Apr2022.eml");
        Skip.If(!File.Exists(path), $"Sample email absent: '{path}'.");

        var email = EmailParser.ParseFile(path);

        Assert.Equal("jhughes@iivs.org", email.From?.Address);
        Assert.Equal("iivs.org", email.From?.Domain);
        Assert.Contains(email.To, t => t.Address == "mflagella@revmed.com" && t.Domain == "revmed.com");
        Assert.Contains(email.Cc, c => c.Address == "hraabe@iivs.org");
        Assert.Contains(email.Cc, c => c.Address == "legal@revmed.com");

        // Encoded-word Subject with a non-ASCII multiplication sign, decoded correctly.
        Assert.Equal("Executed MSA Confirmation — CON/09122024/4812 — IIVS × Revolution Medicines",
            email.Subject);

        // Body was base64-encoded in the source .eml — proves Content-Transfer-Encoding is decoded,
        // not passed through as raw base64 text.
        Assert.Contains("Master Services Agreement", email.Body);
        Assert.Contains("fully executed", email.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RGVhciBNaWNoYWVs", email.Body); // the raw base64 prefix, if undecoded
    }
}
