using System.Diagnostics;
using System.Text.Json;

namespace Winsdk.Cli;

internal class NugetService
{
    private static readonly HttpClient Http = new();
    private const string NugetExeUrl = "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe";
    private const string FlatIndex = "https://api.nuget.org/v3-flatcontainer";

    public static readonly string[] SDK_PACKAGES = new[]
    {
        "Microsoft.Windows.CppWinRT",
        BuildToolsService.BUILD_TOOLS_PACKAGE,
        "Microsoft.WindowsAppSDK",
        "Microsoft.Windows.ImplementationLibrary",
        $"{BuildToolsService.CPP_SDK_PACKAGE}",
        $"{BuildToolsService.CPP_SDK_PACKAGE}.x64",
        $"{BuildToolsService.CPP_SDK_PACKAGE}.arm64"
    };

    public async Task EnsureNugetExeAsync(string winsdkDir, CancellationToken cancellationToken = default)
    {
        var toolsDir = Path.Combine(winsdkDir, "tools");
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

    public async Task InstallPackageAsync(string winsdkDir, string package, string version, string outputDir, CancellationToken cancellationToken = default)
    {
        var nugetExe = Path.Combine(winsdkDir, "tools", "nuget.exe");
        if (!File.Exists(nugetExe))
        {
            throw new FileNotFoundException("nuget.exe missing; call EnsureNugetExeAsync first", nugetExe);
        }

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
