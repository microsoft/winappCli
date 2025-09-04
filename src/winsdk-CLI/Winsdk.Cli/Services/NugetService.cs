using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;

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

    public async Task EnsureNugetExeAsync()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var toolsDir = Path.Combine(userProfile, ".winsdk", "tools");
        var nugetExe = Path.Combine(toolsDir, "nuget.exe");
        if (File.Exists(nugetExe))
            return;

        Directory.CreateDirectory(toolsDir);
        using var resp = await Http.GetAsync(NugetExeUrl);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(nugetExe);
        await resp.Content.CopyToAsync(fs);
    }

    public async Task<string> GetLatestVersionAsync(string packageName, bool includePrerelease)
    {
        var url = $"{FlatIndex}/{packageName.ToLowerInvariant()}/index.json";
        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var s = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(s);
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

    public async Task InstallPackageAsync(string package, string version, string outputDir)
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
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            Console.Error.WriteLine(stdout);
            Console.Error.WriteLine(stderr);
            throw new Exception($"nuget install failed for {package} {version}");
        }
    }

    public async Task InstallPackagesFromConfigAsync(
        IReadOnlyDictionary<string, string> packagesAndVersions,
        string outputDir,
        string? framework = null,
        string dependencyVersion = "lowest")
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
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine(stdout);
                Console.Error.WriteLine(stderr);
                throw new Exception($"nuget install failed for packages.config '{tempFile}'");
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
}
