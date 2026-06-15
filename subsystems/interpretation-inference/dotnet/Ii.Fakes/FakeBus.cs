using Kozmo.Bus;

namespace Ii.Fakes;

/// <summary>
/// Scriptable in-process fake for IKozmoBus. Captures published messages for assertion in tests.
/// The harness drives the façade directly — this fake exists as the production seam, not for async messaging.
/// </summary>
public sealed class FakeBus : IKozmoBus
{
    private readonly List<(string Topic, object Message)> _published = [];

    public IReadOnlyList<(string Topic, object Message)> Published => _published;

    public Task PublishAsync<T>(string topic, T message, CancellationToken ct = default) where T : class
    {
        _published.Add((topic, message));
        return Task.CompletedTask;
    }

    public Task<IAsyncEnumerable<T>> SubscribeAsync<T>(string topic, CancellationToken ct = default) where T : class =>
        Task.FromResult(AsyncEnumerable.Empty<T>());

    public void Clear() => _published.Clear();
}

file static class AsyncEnumerable
{
#pragma warning disable CS1998
    public static async IAsyncEnumerable<T> Empty<T>() { yield break; }
#pragma warning restore CS1998
}
