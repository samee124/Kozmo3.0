using Ig.Contracts;

namespace Ig.Tests;

/// <summary>
/// Offline fake for IEntityTypeClassifier. Records all calls so tests can assert
/// the LLM is NOT invoked for deterministic cases.
/// </summary>
internal sealed class FakeEntityTypeClassifier : IEntityTypeClassifier
{
    private readonly EntityType _returnValue;

    public int CallCount { get; private set; }

    public FakeEntityTypeClassifier(EntityType returnValue = EntityType.Unknown)
        => _returnValue = returnValue;

    public Task<EntityType> ClassifyAsync(
        string effectiveName, string comparisonKey, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(_returnValue);
    }
}
