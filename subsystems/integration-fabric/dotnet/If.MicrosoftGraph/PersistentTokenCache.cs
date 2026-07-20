using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace If.MicrosoftGraph;

/// <summary>
/// Wires a cross-process MSAL token cache to a file so tokens survive process restarts.
/// Both GraphAuthHarness (interactive sign-in) and Wj.MeetingPulse (worker) share the same
/// cache file, allowing the worker to acquire tokens silently after the user signs in once.
///
/// On Windows the cache is protected by DPAPI; on macOS by the Keychain; on Linux by an
/// encrypted file (libsecret/gnome-keyring if available, else plaintext fallback).
/// </summary>
public static class PersistentTokenCache
{
    /// <summary>
    /// Registers a file-backed token cache on the MSAL application.
    /// Call once at startup, before any token acquisition.
    /// Blocks briefly while the cache helper initialises (one-time startup cost).
    /// </summary>
    public static void Attach(IClientApplicationBase app, string cacheFilePath)
    {
        // Resolve relative paths to %APPDATA%\Kozmo\ so all processes (harness + worker)
        // share the same cache file regardless of their working directory.
        if (!Path.IsPathRooted(cacheFilePath))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            cacheFilePath = Path.Combine(appData, "Kozmo", cacheFilePath);
        }

        var fullPath = Path.GetFullPath(cacheFilePath);
        var dir      = Path.GetDirectoryName(fullPath) ?? ".";
        var fileName = Path.GetFileName(fullPath);

        // Ensure the directory exists
        Directory.CreateDirectory(dir);

        var storageProps = new StorageCreationPropertiesBuilder(fileName, dir)
            .Build();

        var helper = MsalCacheHelper.CreateAsync(storageProps).GetAwaiter().GetResult();
        helper.RegisterCache(app.UserTokenCache);
    }
}
