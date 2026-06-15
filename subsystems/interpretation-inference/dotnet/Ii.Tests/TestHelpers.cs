using Km.Store;
using Kozmo.Contracts.Config;

namespace Ii.Tests;

internal static class TestHelpers
{
    public static SaasProfile LoadProfile() =>
        new Catalogue().Load(FindCatalogueDir());

    private static string FindCatalogueDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "catalogue", "profiles", "saas");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Cannot locate catalogue/profiles/saas above {AppContext.BaseDirectory}");
    }
}
