using System.Text;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;

namespace Kozmo.Connector.GoogleDrive;

/// <summary>
/// Downloads files from a Google Drive folder or single file URL to a local temp directory.
///
/// Supported file types:
///   - PDF files        → downloaded directly as .pdf
///   - Google Docs      → exported as PDF
///   - Google Sheets    → exported as PDF
///   - Google Slides    → exported as PDF
///   - Other types      → skipped silently
///
/// The output directory contains only .pdf files so KyvProgramRunner.RunAsync
/// can enumerate them with Directory.EnumerateFiles(path, "*.pdf").
///
/// Token refresh is handled automatically by UserCredential (Google SDK).
/// </summary>
public sealed class GoogleDriveDownloader
{
    private readonly string _clientId;
    private readonly string _clientSecret;

    private const string PdfMimeType = "application/pdf";

    private static readonly HashSet<string> ExportableMimeTypes =
    [
        "application/vnd.google-apps.document",
        "application/vnd.google-apps.spreadsheet",
        "application/vnd.google-apps.presentation",
        "application/vnd.google-apps.drawing",
    ];

    public GoogleDriveDownloader(string clientId, string clientSecret)
    {
        _clientId     = clientId;
        _clientSecret = clientSecret;
    }

    /// <summary>
    /// Downloads all supported files from a Drive folder or single file URL
    /// to a fresh temp directory. Returns the temp directory path.
    /// Caller is responsible for deleting the temp directory after use.
    /// </summary>
    public async Task<string> DownloadToTempFolderAsync(
        OAuthToken        token,
        string            driveUrl,
        CancellationToken ct = default)
    {
        var service = BuildDriveService(token);
        var tempDir = Path.Combine(Path.GetTempPath(), $"kozmo-kyv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var (kind, id) = ParseDriveUrl(driveUrl);

        if (kind == DriveUrlKind.Folder)
            await DownloadFolderContentsAsync(service, id, tempDir, ct);
        else
            await DownloadSingleFileAsync(service, id, tempDir, ct);

        return tempDir;
    }

    // ── URL parsing ───────────────────────────────────────────────────────────

    internal static (DriveUrlKind kind, string id) ParseDriveUrl(string url)
    {
        // Folder: https://drive.google.com/drive/folders/ID
        //         https://drive.google.com/drive/u/0/folders/ID
        var folderMatch = Regex.Match(url, @"/folders/([a-zA-Z0-9_-]+)");
        if (folderMatch.Success)
            return (DriveUrlKind.Folder, folderMatch.Groups[1].Value);

        // File:   https://drive.google.com/file/d/ID/view
        //         https://docs.google.com/document/d/ID/edit
        //         https://docs.google.com/spreadsheets/d/ID/edit
        //         https://docs.google.com/presentation/d/ID/edit
        var fileMatch = Regex.Match(url,
            @"/(?:file/d|document/d|spreadsheets/d|presentation/d)/([a-zA-Z0-9_-]+)");
        if (fileMatch.Success)
            return (DriveUrlKind.File, fileMatch.Groups[1].Value);

        // Bare file ID passed directly
        return (DriveUrlKind.File, url.Trim());
    }

    // ── Folder download ───────────────────────────────────────────────────────

    private async Task DownloadFolderContentsAsync(
        DriveService service, string folderId, string destDir, CancellationToken ct)
    {
        var listRequest = service.Files.List();
        listRequest.Q                         = $"'{folderId}' in parents and trashed = false";
        listRequest.Fields                    = "nextPageToken, files(id, name, mimeType)";
        listRequest.PageSize                  = 100;
        listRequest.SupportsAllDrives         = true;
        listRequest.IncludeItemsFromAllDrives = true;

        string? pageToken = null;
        do
        {
            listRequest.PageToken = pageToken;
            var page = await listRequest.ExecuteAsync(ct);

            foreach (var file in page.Files ?? [])
            {
                // Recurse into subfolders so nested files are included
                if (file.MimeType == "application/vnd.google-apps.folder")
                    await DownloadFolderContentsAsync(service, file.Id, destDir, ct);
                else
                    await DownloadOrExportAsync(service, file.Id, file.Name, file.MimeType, destDir, ct);
            }

            pageToken = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));
    }

    // ── Single file download ──────────────────────────────────────────────────

    private async Task DownloadSingleFileAsync(
        DriveService service, string fileId, string destDir, CancellationToken ct)
    {
        var req = service.Files.Get(fileId);
        req.Fields            = "id, name, mimeType";
        req.SupportsAllDrives = true;
        var meta = await req.ExecuteAsync(ct);
        await DownloadOrExportAsync(service, meta.Id, meta.Name, meta.MimeType, destDir, ct);
    }

    // ── Core download / export ────────────────────────────────────────────────

    private static async Task DownloadOrExportAsync(
        DriveService service, string fileId, string fileName,
        string mimeType, string destDir, CancellationToken ct)
    {
        var baseName  = SanitizeFileName(Path.GetFileNameWithoutExtension(fileName));
        var localPath = UniquePath(destDir, baseName + ".pdf");

        if (mimeType == PdfMimeType)
        {
            var req = service.Files.Get(fileId);
            await using var stream = File.Create(localPath);
            var result = await req.DownloadAsync(stream, ct);
            if (result.Status != Google.Apis.Download.DownloadStatus.Completed)
                File.Delete(localPath);
        }
        else if (ExportableMimeTypes.Contains(mimeType))
        {
            var req = service.Files.Export(fileId, PdfMimeType);
            await using var stream = File.Create(localPath);
            var result = await req.DownloadAsync(stream, ct);
            if (result.Status != Google.Apis.Download.DownloadStatus.Completed)
                File.Delete(localPath);
        }
        // other types: skip silently
    }

    // ── Drive service factory ─────────────────────────────────────────────────

    private DriveService BuildDriveService(OAuthToken token)
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = _clientId, ClientSecret = _clientSecret },
            Scopes        = [DriveService.Scope.DriveReadonly]
        });

        var tokenResponse = new TokenResponse
        {
            AccessToken      = token.AccessToken,
            RefreshToken     = token.RefreshToken,
            // Convert remaining lifetime to seconds; 0 causes immediate refresh
            ExpiresInSeconds = (long)Math.Max(0, (token.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds)
        };

        var credential = new UserCredential(flow, "user", tokenResponse);

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName       = "Kozmo KYV"
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.Length > 0 ? sb.ToString() : "document";
    }

    private static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return Path.Combine(dir, $"{stem}_{Guid.NewGuid():N}.pdf");
    }
}

internal enum DriveUrlKind { Folder, File }
