using System.Text;
using System.Text.Json;
using Kozmo.Llm;

namespace Po.VendorCall;

/// <summary>
/// Return type for all narrative composers.
/// Text is the final answer string; LlmEnhanced flags whether the LLM was used;
/// SourceReferenceIds are forwarded unchanged from the fact packet.
/// </summary>
public sealed record ComposedAnswer(
    string                Text,
    bool                  LlmEnhanced,
    IReadOnlyList<string> SourceReferenceIds);

// ==========================================================
// Q1 — What are we trying to accomplish?
// ==========================================================

public interface IQ1NarrativeComposer
{
    Task<ComposedAnswer> ComposeAsync(Q1FactPacket packet, CancellationToken ct = default);
}

/// <summary>
/// Turns a Q1FactPacket into a short meeting-purpose paragraph.
/// Mode A (no LLM): deterministic template. Mode B (LLM): polished sentence with grounding check.
/// </summary>
public sealed class Q1NarrativeComposer : IQ1NarrativeComposer
{
    private readonly IKozmoLlm? _llm;

    public Q1NarrativeComposer(IKozmoLlm? llm = null) => _llm = llm;

    public async Task<ComposedAnswer> ComposeAsync(Q1FactPacket packet, CancellationToken ct = default)
    {
        var deterministic = BuildDeterministic(packet);

        if (_llm is null)
            return new ComposedAnswer(deterministic, LlmEnhanced: false, packet.SourceReferenceIds);

        try
        {
            var result = await _llm.CompleteJsonAsync(
                SystemPrompt,
                JsonSerializer.Serialize(packet),
                maxTokens: 300,
                ct: ct);

            if (result.Answer is JsonElement el &&
                el.TryGetProperty("text", out var textEl))
            {
                var text = textEl.GetString();
                if (!string.IsNullOrWhiteSpace(text) &&
                    GroundingChecker.Passes(text, BuildAllowed(packet)))
                    return new ComposedAnswer(text, LlmEnhanced: true, packet.SourceReferenceIds);
            }
        }
        catch { /* any failure → deterministic fallback */ }

        return new ComposedAnswer(deterministic, LlmEnhanced: false, packet.SourceReferenceIds);
    }

    private static string BuildDeterministic(Q1FactPacket p)
    {
        var sb     = new StringBuilder();
        var prefix = p.IsFirstReview ? "first" : "follow-up";

        sb.Append($"This {p.MeetingTypePhrase} with {p.VendorName} is a {prefix} " +
                  "structured review of the vendor relationship.");

        if (p.NoticeDeadline is not null && p.DaysUntilDeadline.HasValue)
            sb.Append($" Renewal notice deadline is {p.NoticeDeadline} — " +
                      $"{p.DaysUntilDeadline} day(s) remaining.");
        else if (p.RenewalDate is not null)
            sb.Append($" Renewal date is {p.RenewalDate}.");

        if (!string.IsNullOrWhiteSpace(p.PreviousAnswer))
            sb.Append($" Prior review context: {p.PreviousAnswer.Trim()}");

        return sb.ToString();
    }

    private static IReadOnlySet<string> BuildAllowed(Q1FactPacket p)
    {
        var facts = new List<string>();
        if (p.RenewalDate is not null)    facts.Add(p.RenewalDate);
        if (p.NoticeDeadline is not null) facts.Add(p.NoticeDeadline);
        return GroundingChecker.BuildAllowed(facts);
    }

    private const string SystemPrompt =
        "You are a vendor relationship analyst writing a structured review document. " +
        "Write a concise 2-3 sentence answer to Q1: 'What are we trying to accomplish in this meeting?' " +
        "Use only the facts provided. No speculation. " +
        "Return JSON exactly: {\"text\": \"<your answer>\"}";
}

// ==========================================================
// Q2 — What is our current / contemplated position?
// ==========================================================

public interface IQ2NarrativeComposer
{
    Task<ComposedAnswer> ComposeAsync(Q2FactPacket packet, CancellationToken ct = default);
}

/// <summary>
/// Turns a Q2FactPacket into a commercial-position summary paragraph.
/// </summary>
public sealed class Q2NarrativeComposer : IQ2NarrativeComposer
{
    private readonly IKozmoLlm? _llm;

    public Q2NarrativeComposer(IKozmoLlm? llm = null) => _llm = llm;

    public async Task<ComposedAnswer> ComposeAsync(Q2FactPacket packet, CancellationToken ct = default)
    {
        var deterministic = BuildDeterministic(packet);

        if (_llm is null)
            return new ComposedAnswer(deterministic, LlmEnhanced: false, packet.SourceReferenceIds);

        try
        {
            var result = await _llm.CompleteJsonAsync(
                SystemPrompt,
                JsonSerializer.Serialize(packet),
                maxTokens: 400,
                ct: ct);

            if (result.Answer is JsonElement el &&
                el.TryGetProperty("text", out var textEl))
            {
                var text = textEl.GetString();
                if (!string.IsNullOrWhiteSpace(text) &&
                    GroundingChecker.Passes(text, BuildAllowed(packet)))
                    return new ComposedAnswer(text, LlmEnhanced: true, packet.SourceReferenceIds);
            }
        }
        catch { /* any failure → deterministic fallback */ }

        return new ComposedAnswer(deterministic, LlmEnhanced: false, packet.SourceReferenceIds);
    }

    private static string BuildDeterministic(Q2FactPacket p)
    {
        var sb = new StringBuilder();

        if (p.Contracts.Count == 0)
        {
            sb.Append("No signed contract on file.");
        }
        else
        {
            var c = p.Contracts[0];
            sb.Append($"Active contract: {c.Type}");
            if (c.AnnualValue.HasValue)
                sb.Append($" at {c.Currency ?? "GBP"} {c.AnnualValue:N0}");
            if (!string.IsNullOrEmpty(c.RenewalDate))
                sb.Append($", renewal {c.RenewalDate}");
            sb.Append('.');
        }

        var overdueCount = p.OpenCommitments.Count(c => c.IsOverdue);
        if (p.OpenCommitments.Count > 0)
            sb.Append($" {p.OpenCommitments.Count} open commitment(s), {overdueCount} overdue.");

        if (p.CommercialSignals.Count > 0)
            sb.Append($" {p.CommercialSignals.Count} unresolved commercial signal(s).");

        if (p.EvidenceGaps.Count > 0 && p.Contracts.Count == 0)
            sb.Append($" Gap: {p.EvidenceGaps[0]}");

        if (p.UpdateNotes.Count > 0)
            sb.Append($" Context note: {p.UpdateNotes[0]}");

        return sb.ToString();
    }

    private static IReadOnlySet<string> BuildAllowed(Q2FactPacket p)
    {
        var facts = new List<string>();
        foreach (var c in p.Contracts)
        {
            if (!string.IsNullOrEmpty(c.RenewalDate)) facts.Add(c.RenewalDate);
            if (c.AnnualValue.HasValue) facts.Add(c.AnnualValue.Value.ToString("F0"));
            if (c.NoticePeriodDays.HasValue) facts.Add(c.NoticePeriodDays.Value.ToString());
        }
        foreach (var c in p.OpenCommitments)
        {
            if (c.DueDate is not null) facts.Add(c.DueDate);
        }
        return GroundingChecker.BuildAllowed(facts);
    }

    private const string SystemPrompt =
        "You are a vendor relationship analyst writing a structured review document. " +
        "Write a concise 2-3 sentence answer to Q2: 'What is our current commercial position?' " +
        "Use only the facts provided. No speculation. " +
        "Return JSON exactly: {\"text\": \"<your answer>\"}";
}

// ==========================================================
// Q3 — What is helping, preventing, or changing progress?
// ==========================================================

public interface IQ3NarrativeComposer
{
    Task<ComposedAnswer> ComposeAsync(Q3FactPacket packet, CancellationToken ct = default);
}

/// <summary>
/// Turns a Q3FactPacket into a three-part helping / preventing / changing summary.
/// </summary>
public sealed class Q3NarrativeComposer : IQ3NarrativeComposer
{
    private readonly IKozmoLlm? _llm;

    public Q3NarrativeComposer(IKozmoLlm? llm = null) => _llm = llm;

    public async Task<ComposedAnswer> ComposeAsync(Q3FactPacket packet, CancellationToken ct = default)
    {
        var deterministic = BuildDeterministic(packet);

        if (_llm is null)
            return new ComposedAnswer(deterministic, LlmEnhanced: false, packet.SourceReferenceIds);

        try
        {
            var result = await _llm.CompleteJsonAsync(
                SystemPrompt,
                JsonSerializer.Serialize(packet),
                maxTokens: 400,
                ct: ct);

            if (result.Answer is JsonElement el &&
                el.TryGetProperty("text", out var textEl))
            {
                var text = textEl.GetString();
                if (!string.IsNullOrWhiteSpace(text) &&
                    GroundingChecker.Passes(text, BuildAllowed(packet)))
                    return new ComposedAnswer(text, LlmEnhanced: true, packet.SourceReferenceIds);
            }
        }
        catch { /* any failure → deterministic fallback */ }

        return new ComposedAnswer(deterministic, LlmEnhanced: false, packet.SourceReferenceIds);
    }

    private static string BuildDeterministic(Q3FactPacket p)
    {
        var helping   = p.HelpingFacts.Count > 0
            ? string.Join(" ", p.HelpingFacts)
            : "No positive indicators recorded.";

        var preventing = p.PreventingFacts.Count > 0
            ? string.Join(" ", p.PreventingFacts)
            : "No blocking issues identified.";

        var changing  = p.ChangingFacts.Count > 0
            ? string.Join(" ", p.ChangingFacts)
            : "No changes from prior checkpoint.";

        return $"Helping: {helping} Preventing: {preventing} Changing: {changing}";
    }

    private static IReadOnlySet<string> BuildAllowed(Q3FactPacket p)
    {
        // Extract dates embedded in the fact strings (e.g. deadline dates)
        var facts = p.HelpingFacts
            .Concat(p.PreventingFacts)
            .Concat(p.ChangingFacts);
        return GroundingChecker.BuildAllowed(facts);
    }

    private const string SystemPrompt =
        "You are a vendor relationship analyst writing a structured review document. " +
        "Write a concise answer to Q3: 'What is helping, preventing, or changing progress toward our goals?' " +
        "Structure the answer with three labelled groups: Helping, Preventing, Changing. " +
        "Use only the facts provided. No speculation. " +
        "Return JSON exactly: {\"text\": \"<your answer>\"}";
}

// ==========================================================
// Q4 — What matters most now?
// ==========================================================

public interface IQ4NarrativeComposer
{
    Task<ComposedAnswer> ComposeAsync(Q4FactPacket packet, CancellationToken ct = default);
}

/// <summary>
/// Turns a Q4FactPacket into a ranked-priority narrative.
/// </summary>
public sealed class Q4NarrativeComposer : IQ4NarrativeComposer
{
    private readonly IKozmoLlm? _llm;

    public Q4NarrativeComposer(IKozmoLlm? llm = null) => _llm = llm;

    public async Task<ComposedAnswer> ComposeAsync(Q4FactPacket packet, CancellationToken ct = default)
    {
        var deterministic = BuildDeterministic(packet);

        if (_llm is null)
            return new ComposedAnswer(deterministic, LlmEnhanced: false, packet.SourceReferenceIds);

        try
        {
            var result = await _llm.CompleteJsonAsync(
                SystemPrompt,
                JsonSerializer.Serialize(packet),
                maxTokens: 400,
                ct: ct);

            if (result.Answer is JsonElement el &&
                el.TryGetProperty("text", out var textEl))
            {
                var text = textEl.GetString();
                if (!string.IsNullOrWhiteSpace(text) &&
                    GroundingChecker.Passes(text, BuildAllowed(packet)))
                    return new ComposedAnswer(text, LlmEnhanced: true, packet.SourceReferenceIds);
            }
        }
        catch { /* any failure → deterministic fallback */ }

        return new ComposedAnswer(deterministic, LlmEnhanced: false, packet.SourceReferenceIds);
    }

    private static string BuildDeterministic(Q4FactPacket p)
    {
        if (p.TopPriorities.Count == 0)
            return "No priority items identified. Relationship appears stable.";

        var lines = p.TopPriorities
            .Select((pr, i) => $"{i + 1}. {pr.Description} — {pr.UrgencyReason}");
        return "Top priorities:\n" + string.Join("\n", lines);
    }

    private static IReadOnlySet<string> BuildAllowed(Q4FactPacket p)
    {
        // Pull any date-like strings embedded in urgency reasons (e.g. "deadline 2026-07-30")
        var facts = p.TopPriorities.Select(pr => pr.UrgencyReason)
            .Concat(p.TopPriorities.Select(pr => pr.Description));
        return GroundingChecker.BuildAllowed(facts);
    }

    private const string SystemPrompt =
        "You are a vendor relationship analyst writing a structured review document. " +
        "Write a concise answer to Q4: 'What matters most right now?' " +
        "List priorities in descending urgency order. " +
        "Use only the facts provided. No speculation. " +
        "Return JSON exactly: {\"text\": \"<your answer>\"}";
}

// ==========================================================
// Q5 — What should happen next?
// ==========================================================

public interface IQ5NarrativeComposer
{
    Task<ComposedAnswer> ComposeAsync(Q5FactPacket packet, CancellationToken ct = default);
}

/// <summary>
/// Turns a Q5FactPacket into a concrete action-items narrative.
/// </summary>
public sealed class Q5NarrativeComposer : IQ5NarrativeComposer
{
    private readonly IKozmoLlm? _llm;

    public Q5NarrativeComposer(IKozmoLlm? llm = null) => _llm = llm;

    public async Task<ComposedAnswer> ComposeAsync(Q5FactPacket packet, CancellationToken ct = default)
    {
        var deterministic = BuildDeterministic(packet);

        if (_llm is null)
            return new ComposedAnswer(deterministic, LlmEnhanced: false, packet.SourceReferenceIds);

        try
        {
            var result = await _llm.CompleteJsonAsync(
                SystemPrompt,
                JsonSerializer.Serialize(packet),
                maxTokens: 400,
                ct: ct);

            if (result.Answer is JsonElement el &&
                el.TryGetProperty("text", out var textEl))
            {
                var text = textEl.GetString();
                if (!string.IsNullOrWhiteSpace(text) &&
                    GroundingChecker.Passes(text, BuildAllowed(packet)))
                    return new ComposedAnswer(text, LlmEnhanced: true, packet.SourceReferenceIds);
            }
        }
        catch { /* any failure → deterministic fallback */ }

        return new ComposedAnswer(deterministic, LlmEnhanced: false, packet.SourceReferenceIds);
    }

    private static string BuildDeterministic(Q5FactPacket p)
    {
        if (p.RecommendedActions.Count == 0)
            return "No actions required. Continue standard monitoring.";

        var lines = p.RecommendedActions.Select((a, i) =>
        {
            var due   = a.DueDate is not null ? $"Due: {a.DueDate}" : "Due: TBD";
            var owner = a.Owner   is not null ? $"Owner: {a.Owner}" : "Owner: TBD";
            return $"{i + 1}. {a.Action} | {owner} | {due} | Effect: {a.Effect}";
        });
        return "Recommended actions:\n" + string.Join("\n", lines);
    }

    private static IReadOnlySet<string> BuildAllowed(Q5FactPacket p)
    {
        var facts = p.RecommendedActions
            .Where(a => a.DueDate is not null)
            .Select(a => a.DueDate!);
        return GroundingChecker.BuildAllowed(facts);
    }

    private const string SystemPrompt =
        "You are a vendor relationship analyst writing a structured review document. " +
        "Write a concise answer to Q5: 'What should happen next?' " +
        "List concrete recommended actions with owner and due date where available. " +
        "Use only the facts provided. No speculation. " +
        "Return JSON exactly: {\"text\": \"<your answer>\"}";
}

// ==========================================================
// Overview — Executive status header
// ==========================================================

public interface IOverviewNarrativeComposer
{
    Task<ComposedAnswer> ComposeAsync(
        ReviewStatus     status,
        ReviewMovement   movement,
        ReviewConfidence confidence,
        Q1FactPacket     q1,
        Q2FactPacket     q2,
        CancellationToken ct = default);
}

/// <summary>
/// Produces the executive overview block: status / movement / confidence header
/// plus a one-paragraph snapshot of the vendor relationship.
/// </summary>
public sealed class OverviewNarrativeComposer : IOverviewNarrativeComposer
{
    private readonly IKozmoLlm? _llm;

    public OverviewNarrativeComposer(IKozmoLlm? llm = null) => _llm = llm;

    public async Task<ComposedAnswer> ComposeAsync(
        ReviewStatus     status,
        ReviewMovement   movement,
        ReviewConfidence confidence,
        Q1FactPacket     q1,
        Q2FactPacket     q2,
        CancellationToken ct = default)
    {
        var sourceIds     = q1.SourceReferenceIds.Concat(q2.SourceReferenceIds)
                             .Distinct().ToList();
        var deterministic = BuildDeterministic(status, movement, confidence, q1, q2);

        if (_llm is null)
            return new ComposedAnswer(deterministic, LlmEnhanced: false, sourceIds);

        try
        {
            var context = new
            {
                Status     = status.ToString(),
                Movement   = movement.ToString(),
                Confidence = confidence.ToString(),
                Q1         = q1,
                Q2         = q2
            };

            var result = await _llm.CompleteJsonAsync(
                SystemPrompt,
                JsonSerializer.Serialize(context),
                maxTokens: 300,
                ct: ct);

            if (result.Answer is JsonElement el &&
                el.TryGetProperty("text", out var textEl))
            {
                var text = textEl.GetString();
                var allowed = BuildAllowed(q1, q2);
                if (!string.IsNullOrWhiteSpace(text) &&
                    GroundingChecker.Passes(text, allowed))
                    return new ComposedAnswer(text, LlmEnhanced: true, sourceIds);
            }
        }
        catch { /* any failure → deterministic fallback */ }

        return new ComposedAnswer(deterministic, LlmEnhanced: false, sourceIds);
    }

    private static string BuildDeterministic(
        ReviewStatus s, ReviewMovement m, ReviewConfidence c,
        Q1FactPacket q1, Q2FactPacket q2)
    {
        var contractSummary = q2.Contracts.Count > 0
            ? $"{q2.Contracts.Count} contract(s) on file"
            : "no contract on file";

        var overdueCount = q2.OpenCommitments.Count(cm => cm.IsOverdue);
        var commitmentSummary = q2.OpenCommitments.Count > 0
            ? $"{q2.OpenCommitments.Count} open commitment(s), {overdueCount} overdue"
            : "no open commitments";

        var signalSummary = q2.CommercialSignals.Count > 0
            ? $"{q2.CommercialSignals.Count} commercial signal(s)"
            : "no unresolved signals";

        return $"Status: {s} | Movement: {m} | Confidence: {c}\n" +
               $"{q1.VendorName}: {contractSummary}, {commitmentSummary}, {signalSummary}.";
    }

    private static IReadOnlySet<string> BuildAllowed(Q1FactPacket q1, Q2FactPacket q2)
    {
        var facts = new List<string>();
        if (q1.RenewalDate is not null)    facts.Add(q1.RenewalDate);
        if (q1.NoticeDeadline is not null) facts.Add(q1.NoticeDeadline);
        foreach (var c in q2.Contracts)
        {
            if (!string.IsNullOrEmpty(c.RenewalDate)) facts.Add(c.RenewalDate);
            if (c.AnnualValue.HasValue) facts.Add(c.AnnualValue.Value.ToString("F0"));
        }
        return GroundingChecker.BuildAllowed(facts);
    }

    private const string SystemPrompt =
        "You are a vendor relationship analyst writing the executive overview for a structured vendor review. " +
        "Write 2-3 sentences summarising the vendor relationship status, referencing the status, movement, and confidence indicators. " +
        "Use only the facts provided. No speculation. " +
        "Return JSON exactly: {\"text\": \"<your answer>\"}";
}
