using System.Text.Json.Nodes;

namespace Po.VendorCall;

public sealed record RecipeLimits(
    int MaximumEmails,
    int MaximumCheckInsPerMeeting,
    int MaximumQuestionsInBriefing)
{
    /// <summary>Maximum ranked priorities surfaced in Q4. Defaults to 3 if absent from config.</summary>
    public int MaximumPrioritiesInQ4 { get; init; } = 3;
}

/// <summary>
/// Loaded from vendor_call_recipe.saas.v1.json.
/// Controls timing windows, section ordering, and hard limits for the vendor-call pipeline.
/// </summary>
public sealed record VendorCallRecipe(
    string                RecipeId,
    string                Version,
    string                MeetingType,
    int                   CalendarWindowDays,
    int                   EmailLookbackDays,
    int                   PreMeetingCheckInHours,
    int                   BriefingOffsetMinutes,
    int                   PostMeetingCheckInMinutes,
    IReadOnlyList<string> Sections,
    RecipeLimits          Limits)
{
    public static VendorCallRecipe Load(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        var lim  = node["limits"]!;
        return new VendorCallRecipe(
            RecipeId:                  node["recipeId"]!.GetValue<string>(),
            Version:                   node["version"]!.GetValue<string>(),
            MeetingType:               node["meetingType"]!.GetValue<string>(),
            CalendarWindowDays:        node["calendarWindowDays"]!.GetValue<int>(),
            EmailLookbackDays:         node["emailLookbackDays"]!.GetValue<int>(),
            PreMeetingCheckInHours:    node["preMeetingCheckInHours"]!.GetValue<int>(),
            BriefingOffsetMinutes:     node["briefingOffsetMinutes"]!.GetValue<int>(),
            PostMeetingCheckInMinutes: node["postMeetingCheckInMinutes"]!.GetValue<int>(),
            Sections:                  node["sections"]!.AsArray()
                                           .Select(s => s!.GetValue<string>()).ToList(),
            Limits: new RecipeLimits(
                MaximumEmails:              lim["maximumEmails"]!.GetValue<int>(),
                MaximumCheckInsPerMeeting:  lim["maximumCheckInsPerMeeting"]!.GetValue<int>(),
                MaximumQuestionsInBriefing: lim["maximumQuestionsInBriefing"]!.GetValue<int>())
            {
                MaximumPrioritiesInQ4 = lim["maximumPrioritiesInQ4"]?.GetValue<int>() ?? 3,
            });
    }
}
