namespace Ii.Completeness;

/// <summary>
/// The expected shape of an answer for a completeness question.
/// Maps 1-to-1 to Wc.Contracts.ResponseShape at the check-in boundary (Commit 3).
/// FREE_TEXT is deferred — it reopens the free-text-response problem and is a later addition.
/// </summary>
public enum AnswerType { YesNo, TypedValue, StatusSelect }
