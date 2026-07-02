namespace Wc.Contracts;

public enum AnswerOutcome { Ok, NotFound, AlreadyAnswered, ShapeMismatch }

public sealed record AnswerResult(AnswerOutcome Outcome, CheckIn? Updated = null);
