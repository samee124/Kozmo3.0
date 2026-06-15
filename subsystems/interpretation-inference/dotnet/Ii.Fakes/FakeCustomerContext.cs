using Kozmo.Identity;

namespace Ii.Fakes;

/// <summary>
/// Scriptable in-process fake for ICustomerContext. Set CustomerId before each test scenario.
/// </summary>
public sealed class FakeCustomerContext : ICustomerContext
{
    public Guid   CustomerId  { get; set; } = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public string TenantSlug  { get; set; } = "demo-customer-001";
}
