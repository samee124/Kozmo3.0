using Kozmo.Contracts.Config;

namespace Kozmo.Contracts.Interfaces;

/// <summary>
/// Loads and validates the nine *.saas.v1.json profiles.
/// The loaded profile is immutable — all nine configs must pass validation before any profile is returned.
/// </summary>
public interface ICatalogue
{
    SaasProfile Load(string profileDirectory);
}
