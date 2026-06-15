// RESERVED — Phase 1+. FakeCustomerContext (Ii.Fakes) is the seam used in Phase 0.

namespace Kozmo.Identity;

public interface ICustomerContext
{
    Guid CustomerId { get; }
    string TenantSlug { get; }
}
