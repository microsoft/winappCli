// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinApp.Cli.Services;

internal class MSStoreCLIService(IWinappDirectoryService winappDirectoryService, ILogger<MSStoreCLIService> logger) : IMSStoreCLIService
{
    private static readonly HttpClient SharedHttp = new();

    // Test seam: HttpClient for the GitHub release API and asset downloads. Defaults to
    // the shared production client; tests inject a fake-handler-backed client to exercise
    // the download / checksum / verify flow offline. This is the network boundary —
    // production behavior is unchanged.
    internal HttpClient Http { get; set; } = SharedHttp;

    // Test seam: OS architecture lookup, defaulting to the real runtime value. Lets tests
    // drive both the arch-specific asset selection and the unsupported-architecture path.
    internal Func<Architecture> OsArchitectureProvider { get; set; } = () => RuntimeInformation.OSArchitecture;

    private const string ExeName = "msstore.exe";
    private const string GitHubApiLatestRelease = "https://api.github.com/repos/microsoft/msstore-cli/releases/latest";

    public async Task EnsureMSStoreCLIAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!IsMSStoreCLIAvailable())
        {
            logger.LogInformation("MSStoreCLI not found. Downloading and installing MSStore Developer CLI...");

            await DownloadAndInstallAsync(cancellationToken);

            logger.LogInformation("MSStoreCLI installation completed.");
        }
    }

    private async Task DownloadAndInstallAsync(CancellationToken cancellationToken)
    {
        var arch = OsArchitectureProvider() switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture: {OsArchitectureProvider()}")
        };

        var zipFileName = $"MSStoreCLI-win-{arch}.zip";

        // Create the install folder before any network work, so a folder winapp can't write to
        // fails right away with a clear error rather than after (or behind) a download.
        var installDir = GetInstallDirectory();
        try
        {
            Directory.CreateDirectory(installDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw CannotInstall(installDir, ex);
        }

        // Query the GitHub API to resolve the latest release version and checksum
        var (version, downloadUrl, expectedHash) = await GetLatestReleaseInfoAsync(zipFileName, cancellationToken);

        logger.LogInformation("Downloading MSStoreCLI {Version} from {Url}", version, downloadUrl);

        var zipPath = Path.Combine(installDir, "MSStoreCLI.zip");

        try
        {
            using (var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await response.Content.CopyToAsync(fs, cancellationToken);
            }

            VerifyFileHash(zipPath, expectedHash);

            logger.LogDebug("Extracting MSStoreCLI to {InstallDir}", installDir);
            await ZipFile.ExtractToDirectoryAsync(zipPath, installDir, overwriteFiles: true, cancellationToken: cancellationToken);

            logger.LogDebug("MSStoreCLI {Version} installed to {InstallDir}", version, installDir);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw CannotInstall(installDir, ex);
        }
        finally
        {
            try
            {
                File.Delete(zipPath);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    /// <summary>
    /// Queries the GitHub releases API to resolve the latest version, download URL, and SHA-256 checksum.
    /// </summary>
    private async Task<(string Version, string DownloadUrl, string ExpectedHash)> GetLatestReleaseInfoAsync(string zipFileName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiLatestRelease);
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("WinAppCLI");

        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var tagName = doc.RootElement.GetProperty("tag_name").GetString()
            ?? throw new InvalidOperationException("Could not determine the latest MSStoreCLI version.");

        // Find the matching asset download URL
        string? downloadUrl = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (string.Equals(name, zipFileName, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }

        downloadUrl ??= $"https://github.com/microsoft/msstore-cli/releases/download/{tagName}/{zipFileName}";

        // Retrieve the SHA-256 checksum file from the release assets
        var expectedHash = await GetChecksumAsync(doc, tagName, zipFileName, cancellationToken);

        return (tagName, downloadUrl, expectedHash);
    }

    /// <summary>
    /// Retrieves the SHA-256 checksum for the given zip file from the release assets.
    /// </summary>
    private async Task<string> GetChecksumAsync(JsonDocument releaseDoc, string tagName, string zipFileName, CancellationToken cancellationToken)
    {
        if (!releaseDoc.RootElement.TryGetProperty("assets", out var assets))
        {
            throw new InvalidOperationException(
                $"No assets found in MSStoreCLI release {tagName}. Cannot verify download integrity.");
        }

        var checksumFileName = $"{zipFileName}.sha256.txt";

        string? checksumUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (string.Equals(name, checksumFileName, StringComparison.OrdinalIgnoreCase))
            {
                checksumUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        if (checksumUrl is null)
        {
            throw new InvalidOperationException(
                $"Checksum file '{checksumFileName}' not found in MSStoreCLI release {tagName}. Cannot verify download integrity.");
        }

        var checksumContent = await Http.GetStringAsync(checksumUrl, cancellationToken);

        // Format: "hash  filename" (sha256sum output)
        var hash = checksumContent.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];
        if (hash.Length != 64 || !hash.All(c => char.IsAsciiHexDigit(c)))
        {
            throw new InvalidOperationException(
                $"Invalid SHA-256 checksum format in '{checksumFileName}' for MSStoreCLI release {tagName}.");
        }

        return hash;
    }

    /// <summary>
    /// Verifies the SHA-256 hash of the downloaded file against the expected hash.
    /// </summary>
    private void VerifyFileHash(string filePath, string expectedHash)
    {
        var actualHash = ComputeSha256Hash(filePath);

        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SHA-256 hash mismatch for downloaded MSStoreCLI. Expected: {expectedHash}, Actual: {actualHash}. " +
                "The downloaded file may be corrupted or tampered with.");
        }

        logger.LogDebug("SHA-256 hash verified for {FilePath}", filePath);
    }

    private static string ComputeSha256Hash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexString(hashBytes);
    }

    public string GetMSStoreCLIPath()
    {
        return Path.Combine(GetInstallDirectory(), ExeName);
    }

    private string GetInstallDirectory()
    {
        return Path.Combine(winappDirectoryService.GetGlobalWinappDirectory().FullName, "tools", "msstore");
    }

    private static InvalidOperationException CannotInstall(string installDir, Exception inner) => new(
        $"winapp can't install the Microsoft Store Developer CLI to '{installDir}'. " +
        "Set WINAPP_CLI_CACHE_DIRECTORY to a folder winapp can write to, then retry.", inner);

    private bool IsMSStoreCLIAvailable()
    {
        var exePath = GetMSStoreCLIPath();
        var exists = File.Exists(exePath);
        if (exists)
        {
            logger.LogDebug("MSStoreCLI found at {ExePath}", exePath);
        }
        return exists;
    }
}
