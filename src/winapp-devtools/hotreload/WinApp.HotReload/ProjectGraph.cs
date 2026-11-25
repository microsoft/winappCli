// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.CodeAnalysis;

namespace WinApp.HotReload;

/// <summary>
/// The app project as its build compiled it, read from what winapp's capture targets wrote (see
/// <c>HotReloadCapture</c> in the CLI): the compiler arguments, the build's settings, the referenced projects and, after
/// a project file save, its references again. Class libraries are only located for now: their edits restart the app.
/// </summary>
internal sealed class ProjectGraph
{
    /// <summary>The build settings that change how the app compiles; a project file edit that changes one restarts.</summary>
    public const string SettingsFile = "settings.txt";

    private readonly string _capture;

    public ProjectGraph(string projectFile, string captureDirectory)
    {
        ProjectFile = Path.GetFullPath(projectFile);
        ProjectDirectory = Path.GetDirectoryName(ProjectFile)!;
        _capture = captureDirectory;
        Libraries = ReadLines("projects.txt").Where(p => !string.Equals(p, ProjectFile, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string ProjectFile { get; }
    public string ProjectDirectory { get; }
    public string CompilerArgumentsFile => Path.Combine(_capture, "csc.rsp");
    public string SourcesAsBuilt => Path.Combine(_capture, "src");

    /// <summary>The XAML compiler's inputs as the build used them (an editor's design-time build rewrites obj's).</summary>
    public string XamlInputsAsBuilt => Path.Combine(_capture, "xaml-input.json");

    /// <summary>The ID of the build whose XAML hot reload state the running app has (XamlHotReload\build.txt).</summary>
    public string XamlBuildAsBuilt => Path.Combine(_capture, "xaml-build.txt");

    /// <summary>The copy of an app source the build compiled, or null for other files.</summary>
    public string? AsBuiltCopy(string path) =>
        IsUnder(path, ProjectDirectory) && LibraryOf(path) is null ? Path.Combine(SourcesAsBuilt, Path.GetRelativePath(ProjectDirectory, path)) : null;

    /// <summary>Project files of the class libraries the app references, directly or not.</summary>
    public IReadOnlyList<string> Libraries { get; }

    /// <summary>The folders to watch: the app's and each library's.</summary>
    public IEnumerable<string> Roots => Libraries.Select(Path.GetDirectoryName).OfType<string>().Prepend(ProjectDirectory).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The library project a file belongs to (the deepest project folder holding it), or null for the app's own files.</summary>
    public string? LibraryOf(string path) => Libraries
        .Where(library => IsUnder(path, Path.GetDirectoryName(library)!))
        .Where(library => !IsUnder(path, ProjectDirectory) || Path.GetDirectoryName(library)!.Length > ProjectDirectory.Length)
        .MaxBy(library => library.Length);

    /// <summary>An optimized build (-c Release): the runtime refuses updates to it.</summary>
    public bool IsOptimized => CompilerArguments().Any(a => a is "/optimize+" or "-optimize+" or "/optimize" or "-optimize" or "/o+" or "-o+");

    /// <summary>The arguments of the compile that produced the running app.</summary>
    public string[] CompilerArguments()
    {
        var lines = File.ReadAllLines(CompilerArgumentsFile);
        var start = Array.FindLastIndex(lines, line => line == "/noconfig");
        return start > 0 ? lines[start..] : lines;
    }

    /// <summary>The app project as a Roslyn project, from compiler arguments.</summary>
    public ProjectInfo CreateProjectInfo(Workspace workspace, IEnumerable<string> arguments)
    {
        var info = CommandLineProject.CreateProjectInfo(Path.GetFileNameWithoutExtension(ProjectFile), LanguageNames.CSharp,
            arguments.Where(a => !IsExcludedAnalyzer(a)), ProjectDirectory, workspace);
        var output = Path.GetFullPath(Path.Combine(ProjectDirectory, info.OutputFilePath!));
        return info.WithFilePath(ProjectFile)
            .WithDocuments(info.Documents.DistinctBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase))
            .WithCompilationOutputInfo(info.CompilationOutputInfo.WithAssemblyPath(output));
    }

    // The CsWinRT AOT generator names its helper classes by discovery order, so adding a type renames them (a rude
    // edit). They only matter for trimmed or AOT marshalling; a Debug app falls back to reflection.
    private static bool IsExcludedAnalyzer(string argument) =>
        argument.StartsWith("/analyzer:", StringComparison.Ordinal) && argument.Contains("WinRT.SourceGenerator", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// After the project file is saved: restore, then ask MSBuild for the references again, with the arguments of the
    /// build that produced the app (build.args). Returns the compiler arguments with the new references, the
    /// assemblies the app needs next to it, or why the save can't apply.
    /// </summary>
    public async Task<(string[]? Arguments, IReadOnlyList<(string Source, string SubDirectory)> CopyLocal, string? Restart, string? Error)> RefreshAsync(
        CancellationToken cancellationToken)
    {
        var before = ReadLines(SettingsFile);
        File.Delete(Path.Combine(_capture, SettingsFile));
        if (await MSBuildAsync([.. ReadLines("build.args"), "-t:WinAppHotReloadReferences"], cancellationToken).ConfigureAwait(false) is { } error)
        {
            File.WriteAllLines(Path.Combine(_capture, SettingsFile), before);
            return (null, [], null, error);
        }
        var after = ReadLines(SettingsFile);
        if (after.Except(before).FirstOrDefault() is { } setting)
        {
            File.WriteAllLines(Path.Combine(_capture, SettingsFile), before);
            return (null, [], $"{Path.GetFileName(ProjectFile)}: a build setting changed ({setting.Split('=')[0]}); it takes effect when the app is rebuilt", null);
        }
        var lines = ReadLines("references.txt");
        var arguments = CompilerArguments().Where(a => !a.StartsWith("/reference:", StringComparison.Ordinal) && !a.StartsWith("/analyzer:", StringComparison.Ordinal))
            .Concat(lines.Where(l => l.StartsWith("/reference:", StringComparison.Ordinal) || l.StartsWith("/analyzer:", StringComparison.Ordinal))
                .Select(l => l[..(l.IndexOf(':') + 1)] + "\"" + l[(l.IndexOf(':') + 1)..] + "\""))
            .ToArray();
        var copyLocal = lines.Where(l => l.StartsWith("/copylocal:", StringComparison.Ordinal))
            .Select(l => l["/copylocal:".Length..].Split('|') is [var source, var sub] ? (source, sub) : (l["/copylocal:".Length..], ""))
            .ToList();
        return (arguments, copyLocal, null, null);
    }

    /// <summary>
    /// Builds the project as the app's build did, but into a scratch folder, so the running app's files aren't touched.
    /// Returns the build's first errors, or null when it succeeds. A restart only stops the app once its rebuild works.
    /// </summary>
    public async Task<string?> CheckBuildAsync(CancellationToken cancellationToken)
    {
        if (ReadLines("build.args") is not { Count: > 0 } build)
        {
            return null;
        }
        var scratch = Path.Combine(_capture, "check") + Path.DirectorySeparatorChar;
        var arguments = build.Where(a => !a.Contains("WinAppHotReloadProject=", StringComparison.Ordinal))
            // The XAML compiler copies .xbf files to OutputPath, the rest of the build to OutDir.
            .Concat([$"-p:OutDir={scratch}", $"-p:OutputPath={scratch}"]);
        var (exitCode, output) = await DotnetAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (exitCode == 0)
        {
            return null;
        }
        // "C:\app\Page.xaml(3,5): XamlCompiler error WMC1110: ... [C:\app\App.csproj]" -> "Page.xaml(3,5): XamlCompiler error WMC1110: ..."
        var errors = output.Split('\n').Select(l => l.Trim()).Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"\berror( [A-Z]+\d+)? ?:"))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\s*\[[^\]]+\.\w+proj\]$", ""))
            .Select(l => l.StartsWith(ProjectDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? l[(ProjectDirectory.Length + 1)..] : l)
            .Distinct().Take(3).ToList();
        return errors.Count > 0 ? string.Join(Environment.NewLine + "  ", errors) : $"{Path.GetFileName(ProjectFile)}: the build failed (exit code {exitCode})";
    }

    private async Task<string?> MSBuildAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await DotnetAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (exitCode == 0)
        {
            return null;
        }
        var log = output.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Select(l => l.Trim()).Distinct().Take(3);
        return $"{Path.GetFileName(ProjectFile)}: restoring and reading the project failed: {string.Join(" ", log)}";
    }

    private async Task<(int ExitCode, string Output)> DotnetAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { WorkingDirectory = ProjectDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments.Append("-nologo"))
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false) + await errors.ConfigureAwait(false));
    }

    /// <summary>The assemblies the project references, as its last restore resolved them (after a project file save).</summary>
    public IReadOnlyList<string> References() =>
        [.. ReadLines("references.txt").Where(l => l.StartsWith("/reference:", StringComparison.Ordinal)).Select(l => l["/reference:".Length..])];

    private List<string> ReadLines(string name)
    {
        var path = Path.Combine(_capture, name);
        return File.Exists(path) ? [.. File.ReadAllLines(path).Where(l => l.Length > 0)] : [];
    }

    public static bool IsUnder(string path, string directory) =>
        Path.GetRelativePath(directory, path) is var relative && relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
}
