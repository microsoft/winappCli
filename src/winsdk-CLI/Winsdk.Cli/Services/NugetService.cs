using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Winsdk.Cli;

internal sealed class NugetService
{
    private static readonly HttpClient Http = new HttpClient();
    private const string NugetExeUrl = "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe";
    private const string FlatIndex = "https://api.nuget.org/v3-flatcontainer";

    public static readonly string[] SDK_PACKAGES = new[]
    {
        "Microsoft.Windows.CppWinRT",
        "Microsoft.Windows.SDK.BuildTools",
        "Microsoft.Web.WebView2",
        "Microsoft.WindowsAppSDK",
        "Microsoft.UI.Xaml",
        "Microsoft.Windows.ImplementationLibrary",
        "Microsoft.Windows.SDK.CPP",
        "Microsoft.Windows.SDK.CPP.x64",
        "Microsoft.Windows.SDK.CPP.arm64"
    };

    public async Task EnsureNugetExeAsync(CancellationToken cancellationToken = default)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var toolsDir = Path.Combine(userProfile, ".winsdk", "tools");
        var nugetExe = Path.Combine(toolsDir, "nuget.exe");
        if (File.Exists(nugetExe))
            return;

        Directory.CreateDirectory(toolsDir);
        using var resp = await Http.GetAsync(NugetExeUrl, cancellationToken);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(nugetExe);
        await resp.Content.CopyToAsync(fs, cancellationToken);
    }

    public async Task<string> GetLatestVersionAsync(string packageName, bool includePrerelease, CancellationToken cancellationToken = default)
    {
        var url = $"{FlatIndex}/{packageName.ToLowerInvariant()}/index.json";
        using var resp = await Http.GetAsync(url, cancellationToken);
        resp.EnsureSuccessStatusCode();
        using var s = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("versions", out var versionsElem) || versionsElem.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"No versions found for {packageName}");

        var list = new List<string>();
        foreach (var el in versionsElem.EnumerateArray())
        {
            var v = el.GetString();
            if (!string.IsNullOrWhiteSpace(v)) list.Add(v);
        }

        if (!includePrerelease)
            list = list.Where(v => !v.Contains('-', StringComparison.Ordinal)).ToList();

        if (list.Count == 0)
            throw new InvalidOperationException($"No versions found for {packageName}");

        list.Sort(CompareVersions);
        return list[^1];
    }

    public async Task InstallPackageAsync(string package, string version, string outputDir, CancellationToken cancellationToken = default)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var nugetExe = Path.Combine(userProfile, ".winsdk", "tools", "nuget.exe");
        if (!File.Exists(nugetExe))
            throw new FileNotFoundException("nuget.exe missing; call EnsureNugetExeAsync first", nugetExe);

        Directory.CreateDirectory(outputDir);

        // If already installed, skip
        var expectedFolder = Path.Combine(outputDir, $"{package}.{version}");
        if (Directory.Exists(expectedFolder))
        {
            Console.WriteLine($"{UiSymbols.Skip}  {package} {version} already present");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = nugetExe,
            Arguments = $"install {EscapeArg(package)} -Version {EscapeArg(version)} -OutputDirectory {Quote(outputDir)} -NonInteractive",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = outputDir,
        };
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await p.StandardError.ReadToEndAsync(cancellationToken);
        await p.WaitForExitAsync(cancellationToken);
        if (p.ExitCode != 0)
        {
            Console.Error.WriteLine(stdout);
            Console.Error.WriteLine(stderr);
            throw new InvalidOperationException($"nuget install failed for {package} {version}");
        }
    }

    public async Task InstallPackagesFromConfigAsync(
        IReadOnlyDictionary<string, string> packagesAndVersions,
        string outputDir,
        string? framework = null,
        string dependencyVersion = "lowest",
        bool cleanupOldVersions = false,
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        if (packagesAndVersions == null || packagesAndVersions.Count == 0)
            return;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var nugetExe = Path.Combine(userProfile, ".winsdk", "tools", "nuget.exe");
        if (!File.Exists(nugetExe))
            throw new FileNotFoundException("nuget.exe missing; call EnsureNugetExeAsync first", nugetExe);

        Directory.CreateDirectory(outputDir);

        // Skip work if every expected package folder already exists
        bool allPresent = packagesAndVersions.All(kv =>
            Directory.Exists(Path.Combine(outputDir, $"{kv.Key}.{kv.Value}")));
        if (allPresent)
        {
            foreach (var kv in packagesAndVersions)
                Console.WriteLine($"{UiSymbols.Skip}  {kv.Key} {kv.Value} already present");
            return;
        }

        // Create a temporary packages.config in %TEMP%
        string tempDir = Path.GetTempPath();
        string tempFile = Path.Combine(tempDir, $"{Guid.NewGuid():N}", $"packages.config");
        if (!Directory.Exists(Path.GetDirectoryName(tempFile)!))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
        }

        try
        {
            WritePackagesConfig(tempFile, packagesAndVersions);

            // Build arguments
            var args = new List<string>
            {
                "install",
                tempFile,
                "-OutputDirectory",
                Quote(outputDir),
                "-DependencyVersion",
                dependencyVersion,
                "-NonInteractive"
            };
            if (!string.IsNullOrWhiteSpace(framework))
            {
                args.Add("-Framework");
                args.Add(framework!);
            }

            var psi = new ProcessStartInfo
            {
                FileName = nugetExe,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = outputDir,
            };

            using var p = Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await p.StandardError.ReadToEndAsync(cancellationToken);
            await p.WaitForExitAsync(cancellationToken);

            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine(stdout);
                Console.Error.WriteLine(stderr);
                throw new InvalidOperationException($"nuget install failed for packages.config '{tempFile}'");
            }

            // Clean up old package versions if requested
            if (cleanupOldVersions)
            {
                foreach (var packageAndVersion in packagesAndVersions)
                {
                    var removedCount = CleanupOldPackageVersionsInternal(packageAndVersion.Key, packageAndVersion.Value, outputDir, verbose);
                    if (removedCount > 0 && verbose)
                    {
                        Console.WriteLine($"🗑️  Cleaned up {removedCount} old version(s) of {packageAndVersion.Key}");
                    }
                }
            }
        }
        finally
        {
            // Best-effort cleanup
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* ignore */ }
        }
    }

    private static void WritePackagesConfig(
        string path,
        IReadOnlyDictionary<string, string> packagesAndVersions)
    {
        // Minimal packages.config. We omit targetFramework on each package so that
        // a -Framework argument (if provided) controls resolution. If you prefer,
        // you can add targetFramework="native"|"..."
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        writer.WriteLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
        writer.WriteLine("<packages>");
        foreach (var kv in packagesAndVersions.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var id = SecurityElement.Escape(kv.Key);
            var ver = SecurityElement.Escape(kv.Value);
            writer.WriteLine($@"  <package id=""{id}"" version=""{ver}"" />");
        }
        writer.WriteLine("</packages>");
    }

    private static string Quote(string path) => $"\"{path}\"";

    private static string EscapeArg(string v)
    {
        if (v.Contains(' ') || v.Contains('"'))
            return Quote(v.Replace("\"", "\\\""));
        return v;
    }

    private static int CompareVersions(string a, string b)
    {
        var ap = a.Split('.', '-', StringSplitOptions.RemoveEmptyEntries);
        var bp = b.Split('.', '-', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Max(ap.Length, bp.Length); i++)
        {
            int ai = i < ap.Length && int.TryParse(ap[i], out var av) ? av : 0;
            int bi = i < bp.Length && int.TryParse(bp[i], out var bv) ? bv : 0;
            if (ai != bi) return ai.CompareTo(bi);
        }
        return 0;
    }

    /// <summary>
    /// Remove old versions of a package, keeping only the specified version
    /// </summary>
    /// <param name="packageName">Name of the NuGet package</param>
    /// <param name="keepVersion">Version to keep</param>
    /// <param name="packagesDir">Directory containing packages</param>
    /// <param name="verbose">Whether to log progress messages</param>
    /// <returns>Number of removed package versions</returns>
    public static int CleanupOldPackageVersions(string packageName, string keepVersion, string packagesDir, bool verbose = true)
    {
        return CleanupOldPackageVersionsInternal(packageName, keepVersion, packagesDir, verbose);
    }

    /// <summary>
    /// Remove old versions of a package, keeping only the specified version
    /// </summary>
    /// <param name="packageName">Name of the NuGet package</param>
    /// <param name="keepVersion">Version to keep</param>
    /// <param name="packagesDir">Directory containing packages</param>
    /// <param name="verbose">Whether to log progress messages</param>
    /// <returns>Number of removed package versions</returns>
    private static int CleanupOldPackageVersionsInternal(string packageName, string keepVersion, string packagesDir, bool verbose = true)
    {
        var removedCount = 0;

        try
        {
            if (!Directory.Exists(packagesDir))
            {
                return removedCount;
            }

            var entries = Directory.GetDirectories(packagesDir)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Cast<string>()
                .ToList();

            // Pattern to match package directories: PackageName.Version
            var packagePattern = new Regex(
                $@"^{Regex.Escape(packageName)}\.(.+)$",
                RegexOptions.IgnoreCase);

            foreach (var entry in entries)
            {
                var match = packagePattern.Match(entry);
                if (match.Success)
                {
                    var foundVersion = match.Groups[1].Value;
                    var entryPath = Path.Combine(packagesDir, entry);

                    // Skip if this is the version we want to keep
                    if (string.Equals(foundVersion, keepVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Check if this is actually a directory for this package
                    if (Directory.Exists(entryPath))
                    {
                        if (verbose)
                        {
                            Console.WriteLine($"🗑️  Removing old version: {packageName} v{foundVersion}");
                        }

                        try
                        {
                            Directory.Delete(entryPath, recursive: true);
                            removedCount++;

                            if (verbose)
                            {
                                Console.WriteLine($"✅ Removed: {entryPath}");
                            }
                        }
                        catch (Exception error)
                        {
                            if (verbose)
                            {
                                Console.WriteLine($"⚠️  Could not remove {entryPath}: {error.Message}");
                            }
                        }
                    }
                }
            }

            // Also clean up any .nupkg files for old versions
            var nupkgFiles = Directory.GetFiles(packagesDir, "*.nupkg")
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Cast<string>()
                .ToList();

            var nupkgPattern = new Regex(
                $@"^{Regex.Escape(packageName)}\.(.+)\.nupkg$",
                RegexOptions.IgnoreCase);

            foreach (var nupkgFile in nupkgFiles)
            {
                var match = nupkgPattern.Match(nupkgFile);
                if (match.Success)
                {
                    var foundVersion = match.Groups[1].Value;
                    if (!string.Equals(foundVersion, keepVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        var nupkgPath = Path.Combine(packagesDir, nupkgFile);
                        try
                        {
                            File.Delete(nupkgPath);
                            if (verbose)
                            {
                                Console.WriteLine($"🗑️  Removed old .nupkg: {nupkgFile}");
                            }
                        }
                        catch (Exception error)
                        {
                            if (verbose)
                            {
                                Console.WriteLine($"⚠️  Could not remove {nupkgPath}: {error.Message}");
                            }
                        }
                    }
                }
            }

        }
        catch (Exception error)
        {
            if (verbose)
            {
                Console.WriteLine($"⚠️  Error during cleanup of {packageName}: {error.Message}");
            }
        }

        return removedCount;
    }
}
