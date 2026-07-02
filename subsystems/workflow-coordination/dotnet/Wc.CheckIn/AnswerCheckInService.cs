using Wc.Contracts;

namespace Wc.CheckIn;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Records a structured human response against an OPEN check-in.
/// Validates existence, open state, and shape compatibility; then stamps ANSWERED.
/// Does NOT process the response further — that is Commit 3 (process_response stage).
/// Does NOT read the clock — caller supplies 'now'.
/// </summary>
public sealed class AnswerCheckInService
{
    public async Task<AnswerResult> AnswerAsync(
        Guid           checkInId,
        string         responseValue,
        DateTimeOffset now,
        ICheckInStore  store,
        CancellationToken ct = default)
    {
        var checkIn = await store.GetAsync(checkInId, ct);
        if (checkIn is null)
            return new AnswerResult(AnswerOutcome.NotFound);

        if (checkIn.Status != PendingStatus.OPEN)
            return new AnswerResult(AnswerOutcome.AlreadyAnswered);

        if (!IsValid(checkIn.ResponseShape, responseValue))
            return new AnswerResult(AnswerOutcome.ShapeMismatch);

        var updated = checkIn with
        {
            Status        = PendingStatus.ANSWERED,
            AnsweredAt    = now,
            ResponseValue = responseValue
        };
        await store.SaveAsync(updated, ct);
        return new AnswerResult(AnswerOutcome.Ok, updated);
    }

    private static bool IsValid(ResponseShape shape, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return shape switch
        {
            ResponseShape.YES_NO => value.Equals("true",  StringComparison.OrdinalIgnoreCase) ||
                                    value.Equals("false", StringComparison.OrdinalIgnoreCase),
            ResponseShape.TYPED_VALUE   => true,
            ResponseShape.STATUS_SELECT => true,
            _                           => false
        };
    }
}
