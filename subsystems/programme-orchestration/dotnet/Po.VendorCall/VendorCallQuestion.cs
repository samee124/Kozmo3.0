using System.Text.Json.Nodes;

namespace Po.VendorCall;

/// <summary>
/// A single entry from vendor_call_questions.saas.v1.json.
/// The question bank is FIXED and VERSIONED — answers and claim keys are immutable.
/// </summary>
public sealed record VendorCallQuestion(
    string                QuestionId,
    string                ClaimKey,
    string                Stage,
    string                Prompt,
    IReadOnlyList<string> Answers,
    int                   ExpiryDays);

/// <summary>Loads the question bank from the catalogue JSON file.</summary>
public static class VendorCallQuestionBank
{
    public static IReadOnlyList<VendorCallQuestion> Load(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        return node["questions"]!.AsArray()
            .Select(q => new VendorCallQuestion(
                QuestionId: q!["questionId"]!.GetValue<string>(),
                ClaimKey:   q["claimKey"]!.GetValue<string>(),
                Stage:      q["stage"]!.GetValue<string>(),
                Prompt:     q["prompt"]!.GetValue<string>(),
                Answers:    q["answers"]!.AsArray().Select(a => a!.GetValue<string>()).ToList(),
                ExpiryDays: q["expiryDays"]!.GetValue<int>()))
            .ToList();
    }
}
