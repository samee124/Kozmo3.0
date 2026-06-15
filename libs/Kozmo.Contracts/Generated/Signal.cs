// GENERATED — do not hand-edit; regenerate via tools/codegen/generate.ps1

using System.Collections.Generic;

namespace Kozmo.Contracts;

public sealed record Signal(
    Guid         Id,
    Guid         EntityId,
    Guid         CustomerId,
    SourceSystem SourceSystem,
    string       ExternalId,
    IReadOnlyDictionary<string, object?> Payload,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt,
    Guid         TraceId
);
