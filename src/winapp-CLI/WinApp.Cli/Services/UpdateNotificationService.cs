// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Globalization;
using System.Text.Json;
using WinApp.Cli.Helpers;
using WinApp.Cli.Telemetry;

namespace WinApp.Cli.Services;

internal class UpdateNotificationService(
    IWinappDirectoryService winappDirectoryService,
    ILogger<UpdateNotificationService> logger,
    IStorageDiagnostics? diagnostics = null) : IUpdateNotificationService
{
    private readonly IStorageDiagnostics _diagnostics = diagnostics ?? new StorageDiagnostics(Console.Error);
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static int _refreshScheduled;  // guarded by Interlocked; see NotScheduled/Scheduled constants
    private const int NotScheduled = 0;
    private const int Scheduled = 1;
    private const string GitHubApiLatestRelease = "https://api.github.com/repos/microsoft/winappcli/releases/latest";
    private const string UpdateCheckFileName = ".update-check";
    private const int CheckIntervalHours = 24;
    private const int MaxReasonableFutureMajorDelta = 20;
    private static readonly TimeSpan FirstRunRefreshTimeout = TimeSpan.FromMilliseconds(1000);

    // For testing only — when true, skips the fire-and-forget background network refresh
    internal bool SkipBackgroundRefreshForTesting;
    internal Func<string> CurrentVersionProvider { get; set; } = VersionHelper.GetVersionString;

    // Network boundary seam: defaults to the shared production client; tests inject a fake handler.
    internal HttpClient Http { get; set; } = SharedHttp;

    // OS boundary seam: defaults to the real process path; tests override to exercise
    // the install-channel path heuristics without controlling the host process.
    internal Func<string?> ProcessPathProvider { get; set; } = () => Environment.ProcessPath;

    // The console used for upgrade notices. Defaults to stderr so scripted commands
    // (e.g. get-winapp-path, --version) are never corrupted. Tests can override to capture output.
    internal IAnsiConsole NotificationConsole { get; set; } = AnsiConsole.Create(
        new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

    // Cache file format (one value per line):
    //   Line 0: last-check timestamp (round-trip "O" format, UTC)
    //   Line 1: latest version found (or empty)
    //   Line 2: date when notice was last shown (yyyy-MM-dd, or empty)

    public void CheckAndNotify()
    {
        try
        {
            // Opt-out: user explicitly disabled update checks, or running in CI
            if (Environment.GetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK") == "0"
                || CIEnvironmentDetectorForTelemetry.IsCIEnvironment())
            {
                return;
            }

            var cacheFile = GetUpdateCheckFile();
            var cache = ReadCache(cacheFile);

            var currentVersion = CurrentVersionProvider();

            // Paranoia: discard cached versions that look like test artifacts (e.g., 99.0.0, 999.0.0)
            if (!string.IsNullOrEmpty(cache.LatestVersion) && IsUnreasonableVersion(cache.LatestVersion, currentVersion))
            {
                logger.LogDebug("Discarded unreasonable cached update version {Version}", cache.LatestVersion);
                cache = cache with { LastCheck = null, LatestVersion = "", LastShownDate = "" };
                WriteCacheFile(cacheFile, cache);
            }

            // Show notice if a newer version is cached and not yet shown today.
            if (!string.IsNullOrEmpty(cache.LatestVersion)
                && IsNewerVersion(cache.LatestVersion, currentVersion)
                && cache.LastShownDate != DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            {
                DisplayUpdateNotification(cache.LatestVersion);
                cache = cache with { LastShownDate = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
                WriteCacheFile(cacheFile, cache);
            }

            // If cache is stale (or missing), refresh in the background — fire and forget.
            // On first run (no cache at all), do a short bounded foreground wait so the cache
            // has a real chance of being populated before the process exits.
            if (!SkipBackgroundRefreshForTesting
                && (!cache.LastCheck.HasValue
                    || (DateTimeOffset.UtcNow - cache.LastCheck.Value).TotalHours >= CheckIntervalHours)
                && Interlocked.CompareExchange(ref _refreshScheduled, Scheduled, NotScheduled) == NotScheduled)
            {
                // Persist the throttle before starting a network request. A restricted invocation
                // must not repeatedly fetch an optional update it cannot remember.
                if (!WriteCacheFile(cacheFile, new UpdateCheckCache(DateTimeOffset.UtcNow, cache.LatestVersion, cache.LastShownDate)))
                {
                    Interlocked.Exchange(ref _refreshScheduled, NotScheduled);
                    return;
                }

                var refreshTask = Task.Run(async () =>
                {
                    try
                    {
                        await RefreshCacheAsync(cacheFile);
                    }
                    catch
                    {
                        // Best-effort — never crash the process
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _refreshScheduled, NotScheduled);
                    }
                });

                // First-run: briefly wait so short-lived commands can still populate the cache
                if (!cache.LastCheck.HasValue)
                {
                    refreshTask.Wait(FirstRunRefreshTimeout);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            _diagnostics.Warning("optional_storage_unavailable", $"Skipping the automatic CLI update check: {ex.Message}");
        }
    }

    internal async Task RefreshCacheAsync(FileInfo? cacheFileOverride = null)
    {
        var cacheFile = cacheFileOverride ?? GetUpdateCheckFile();
        var latestVersion = await GetLatestVersionAsync();

        // Preserve lastShownDate from existing cache
        var existingCache = ReadCache(cacheFile);
        var newCache = new UpdateCheckCache(
            LastCheck: DateTimeOffset.UtcNow,
            LatestVersion: latestVersion ?? "",
            LastShownDate: existingCache.LastShownDate);

        WriteCacheFile(cacheFile, newCache);
    }

    internal async Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiLatestRelease);
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("WinAppCLI");

            using var response = await Http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            return ParseTagName(doc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to check for CLI updates.");
            return null;
        }
    }

    /// <summary>
    /// Extracts the version string from a GitHub release JSON document.
    /// Strips the leading "v" prefix if present (e.g. "v0.3.0" → "0.3.0").
    /// Returns null if the tag_name property is missing, null, or empty.
    /// </summary>
    internal static string? ParseTagName(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("tag_name", out var tagNameElement))
        {
            return null;
        }

        var tagName = tagNameElement.GetString();
        if (string.IsNullOrEmpty(tagName))
        {
            return null;
        }

        return tagName.StartsWith('v') ? tagName[1..] : tagName;
    }

    private void DisplayUpdateNotification(string newVersion)
    {
        var upgradeHint = DetectInstallChannel() switch
        {
            InstallChannel.Npm => "npm update -g @microsoft/winappcli",
            InstallChannel.NuGet => "visit https://github.com/microsoft/winappcli/releases",
            _ => "visit https://github.com/microsoft/winappcli/releases"
        };

        NotificationConsole.MarkupLine($"[yellow]v{Markup.Escape(newVersion)} is available. To update, {Markup.Escape(upgradeHint)}.[/]");
    }

    /// <summary>
    /// Parses a SemVer-like version string into its core <see cref="Version"/> and optional prerelease suffix.
    /// Handles v-prefix, build metadata (+...), and prerelease (-...) in a single pass.
    /// </summary>
    private static bool TryParseSemVer(string value, out Version coreVersion, out string? prerelease)
    {
        coreVersion = new Version(0, 0);
        prerelease = null;

        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var plusIdx = value.IndexOf('+');
        if (plusIdx >= 0)
        {
            value = value[..plusIdx];
        }

        var dashIdx = value.IndexOf('-');
        if (dashIdx >= 0)
        {
            prerelease = value[(dashIdx + 1)..];
            value = value[..dashIdx];
        }

        return Version.TryParse(value, out coreVersion!);
    }

    /// <summary>
    /// Returns the core version without prerelease suffix or build metadata.
    /// E.g., "0.3.2-prerelease.73+abc" → "0.3.2"
    /// </summary>
    internal static string GetCoreVersion(string version)
    {
        return TryParseSemVer(version, out var core, out _) ? core.ToString() : version;
    }

    /// <summary>
    /// Detects unreasonably high cached version numbers that likely came from test fixtures
    /// (e.g., 99.0.0, 999.0.0) by comparing against the current CLI major version.
    /// </summary>
    internal static bool IsUnreasonableVersion(string version, string currentVersion)
    {
        if (!TryParseSemVer(version, out var cachedCore, out _)
            || !TryParseSemVer(currentVersion, out var currentCore, out _))
        {
            return false;
        }

        return cachedCore.Major > currentCore.Major + MaxReasonableFutureMajorDelta;
    }

    internal static bool IsNewerVersion(string latest, string current)
    {
        if (!TryParseSemVer(latest, out var latestCore, out var latestPre))
        {
            return false;
        }

        if (!TryParseSemVer(current, out var currentCore, out var currentPre))
        {
            return false;
        }

        var coreCompare = latestCore.CompareTo(currentCore);
        if (coreCompare != 0)
        {
            return coreCompare > 0;
        }

        // Same core version: compare pre-release identifiers per SemVer 2.0.0
        return ComparePreRelease(latestPre, currentPre) > 0;
    }

    // Compares two SemVer pre-release strings identifier-by-identifier.
    // Returns positive if a > b, negative if a < b, zero if equal.
    // A null value (stable release) is always greater than any pre-release string.
    private static int ComparePreRelease(string? a, string? b)
    {
        if (a == null && b == null) { return 0; }
        if (a == null) { return 1; }   // stable > pre-release
        if (b == null) { return -1; }  // pre-release < stable

        var aIds = a.Split('.');
        var bIds = b.Split('.');
        var len = Math.Min(aIds.Length, bIds.Length);

        for (var i = 0; i < len; i++)
        {
            var aIsNum = int.TryParse(aIds[i], out var aNum);
            var bIsNum = int.TryParse(bIds[i], out var bNum);

            int cmp;
            if (aIsNum && bIsNum)
            {
                cmp = aNum.CompareTo(bNum);
            }
            else if (aIsNum)
            {
                // Per SemVer: numeric identifiers have lower precedence than alphanumeric
                cmp = -1;
            }
            else if (bIsNum)
            {
                cmp = 1;
            }
            else
            {
                cmp = string.Compare(aIds[i], bIds[i], StringComparison.Ordinal);
            }

            if (cmp != 0) { return cmp; }
        }

        // All compared identifiers are equal; a longer pre-release has higher precedence
        return aIds.Length.CompareTo(bIds.Length);
    }

    internal InstallChannel DetectInstallChannel()
    {
        // Check caller env var (set by wrapper scripts via --caller option)
        var caller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        if (string.Equals(caller, "npm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(caller, "nodejs-package", StringComparison.OrdinalIgnoreCase))
        {
            return InstallChannel.Npm;
        }
        if (string.Equals(caller, "nuget-package", StringComparison.OrdinalIgnoreCase))
        {
            return InstallChannel.NuGet;
        }

        // Check exe path heuristics
        var exePath = ProcessPathProvider();
        if (!string.IsNullOrEmpty(exePath))
        {
            if (exePath.Contains("node_modules", StringComparison.OrdinalIgnoreCase))
            {
                return InstallChannel.Npm;
            }
            if (exePath.Contains(".nuget", StringComparison.OrdinalIgnoreCase))
            {
                return InstallChannel.NuGet;
            }
        }

        return InstallChannel.StandaloneExe;
    }

    private FileInfo GetUpdateCheckFile()
    {
        var globalDir = winappDirectoryService.GetGlobalWinappDirectory();
        return new FileInfo(Path.Combine(globalDir.FullName, UpdateCheckFileName));
    }

    internal static UpdateCheckCache ReadCache(FileInfo cacheFile)
    {
        if (!cacheFile.Exists)
        {
            return UpdateCheckCache.Empty;
        }

        try
        {
            var lines = File.ReadAllLines(cacheFile.FullName);

            DateTimeOffset? lastCheck = null;
            if (lines.Length >= 1
                && DateTimeOffset.TryParse(lines[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                lastCheck = parsed;
            }

            var latestVersion = lines.Length >= 2 ? lines[1] : "";
            var lastShownDate = lines.Length >= 3 ? lines[2] : "";

            return new UpdateCheckCache(lastCheck, latestVersion, lastShownDate);
        }
        catch
        {
            return UpdateCheckCache.Empty;
        }
    }

    private bool WriteCacheFile(FileInfo cacheFile, UpdateCheckCache cache)
    {
        try
        {
            cacheFile.Directory?.Create();

            var content = $"{cache.LastCheck?.ToString("O", CultureInfo.InvariantCulture) ?? ""}\n{cache.LatestVersion ?? ""}\n{cache.LastShownDate ?? ""}";
            AtomicFile.WriteAllText(cacheFile.FullName, content);

            cacheFile.Refresh();
            cacheFile.Attributes |= FileAttributes.Hidden;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to write update check cache.");
            _diagnostics.Warning("optional_storage_unavailable",
                $"CLI update bookkeeping at '{cacheFile.FullName}' is unavailable. The automatic update check may be skipped.");
            return false;
        }
    }

    internal record UpdateCheckCache(
        DateTimeOffset? LastCheck,
        string LatestVersion,
        string LastShownDate)
    {
        public static readonly UpdateCheckCache Empty = new(null, "", "");
    }
}

internal enum InstallChannel
{
    Msix,
    StandaloneExe,
    Npm,
    NuGet
}
