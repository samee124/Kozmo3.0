namespace Kyv.ProgramRunner;

public enum ProgramRunStatus { Running, Completed, Failed }

public sealed record ProgramStageExecution(
    int            StageOrder,
    string         StageName,
    DateTimeOffset ExecutedAt,
    int            ItemsProcessed
);

/// <summary>
/// A PDF that was ingested but yielded no extractable text — visible in every run report
/// so unreadable documents are never silently dropped. OCR is a later pipeline addition.
/// </summary>
public sealed record UnreadableDocument(
    string RelativePath,
    string Reason
);

public sealed record ProgramRun(
    Guid                                  RunId,
    string                                ProgramName,
    string                                SourceFolder,
    DateTimeOffset                        StartedAt,
    DateTimeOffset?                       FinishedAt,
    ProgramRunStatus                      Status,
    IReadOnlyList<ProgramStageExecution>  Stages,
    IReadOnlyList<UnreadableDocument>     UnreadableDocuments
);
