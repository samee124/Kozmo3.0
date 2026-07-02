using Kozmo.Contracts;

namespace Ii.Completeness;

/// <summary>
/// Deterministic completeness rubric: given a fixed set of questions and answers,
/// produces a CompletenessProfile. Same inputs → same profile, always.
/// A question is "answered" when its answer's confidence ≥ the question's RequiredConfidence.
/// Questions with no answer, or whose answer falls below the bar, become gaps.
/// </summary>
public static class CompletenessRubric
{
    public static CompletenessProfile Compute(
        IReadOnlyList<Question> questions,
        IReadOnlyList<Answer>   answers)
    {
        var answerIndex = answers.ToDictionary(a => a.QuestionId, StringComparer.Ordinal);

        var answeredIds = new List<string>();
        var gapIds      = new List<string>();

        foreach (var q in questions.OrderBy(q => q.Id, StringComparer.Ordinal))
        {
            if (answerIndex.TryGetValue(q.Id, out var ans) && ans.Confidence >= q.RequiredConfidence)
                answeredIds.Add(q.Id);
            else
                gapIds.Add(q.Id);
        }

        var coveredSet = new HashSet<string>(answeredIds, StringComparer.Ordinal);

        var coverages = Enum.GetValues<Dimension>()
            .Select(dim =>
            {
                var dimQs    = questions.Where(q => q.Dimension == dim).ToList();
                var answered = dimQs.Count(q => coveredSet.Contains(q.Id));
                return new DimensionCoverage(dim, answered, dimQs.Count);
            })
            .ToList();

        return new CompletenessProfile(
            DimensionCoverages:  coverages.AsReadOnly(),
            AnsweredQuestionIds: answeredIds.AsReadOnly(),
            GapQuestionIds:      gapIds.AsReadOnly());
    }
}
