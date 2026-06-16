using Km.Store;
using Kozmo.Contracts.Config;

namespace Ii.Tests;

internal static class TestHelpers
{
    public static SaasProfile LoadProfile() =>
        new Catalogue().Load(FindCatalogueDir());

    public static string FindLlmCachePath()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "fixtures", "llm-cache.json");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "fixtures/llm-cache.json not found. Run 'dotnet run --project tools/Kozmo.SeedPrep' first.");
    }

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
