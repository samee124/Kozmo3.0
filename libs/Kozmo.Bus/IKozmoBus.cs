// RESERVED — Phase 1+. FakeBus (Ii.Fakes) is the seam used in Phase 0.

namespace Kozmo.Bus;

public interface IKozmoBus
{
    Task PublishAsync<T>(string topic, T message, CancellationToken ct = default) where T : class;
    Task<IAsyncEnumerable<T>> SubscribeAsync<T>(string topic, CancellationToken ct = default) where T : class;
}
