namespace Kozmo.Architecture.Tests;

internal static class ArchTestHelpers
{
    internal static string FindCatalogueDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "catalogue", "profiles", "saas");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate catalogue/profiles/saas/ walking up from {AppContext.BaseDirectory}");
    }
}
