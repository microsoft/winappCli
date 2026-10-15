// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Spectre.Console;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Services;

/// <summary>
/// 'winapp run --watch' for C++/WinRT apps. A running C++ app can't take new code, so a XAML save applies live only
/// when the XAML compiler's generated C++ for it is byte-identical to the running build's: then the page's compiled
/// Connect still matches, and the compiler's own .xbf is loaded into the live page the way its generated
/// InitializeComponent loads it. Any other change rebuilds and restarts the app, and says why.
/// </summary>
internal sealed partial class WatchSession
{
    /// <summary>A XAML compiler with a service mode (XamlCompiler.exe --serve), used instead of the one in the app's WinUI package.</summary>
    internal const string CompilerServiceVariable = "WINAPP_XAML_COMPILER_SERVICE";

    // The XAML compiler's pass 2 inputs, as XamlCompiler.exe reads them (input.json). Builds under Visual Studio's MSBuild
    // run the compiler in-process and don't write them; the parameters are those of the package's own InputSerializer call.
    private const string CppTargets = """
        <Project>
          <Target Name="WinAppHotReloadXamlInputs" AfterTargets="MarkupCompilePass2" Condition="'$(WinAppHotReloadXamlInputs)' != ''">
            <InputSerializer JsonFilePath="$(WinAppHotReloadXamlInputs)" LanguageSourceExtension="$(DefaultLanguageSourceExtension)"
                Language="$(XamlLanguage)" RootNamespace="$(RootNamespace)" XamlPages="@(Page)" XamlApplications="@(ApplicationDefinition)"
                SdkXamlPages="@(SdkXamlItems)" PriIndexName="$(PriIndexName)" ProjectName="$(XamlProjectName)" IsPass1="False"
                DisableXbfGeneration="$(DisableXbfGeneration)" DisableXbfLineInfo="$(DisableXbfLineInfo)"
                CodeGenerationControlFlags="$(XamlCodeGenerationControlFlags)" ClIncludeFiles="@(ClInclude)"
                CIncludeDirectories="$(XamlCppIncludeDirectories)" LocalAssembly="$(XamlLocalAssembly)" ProjectPath="$(MSBuildProjectFullPath)"
                OutputPath="$(XamlGeneratedOutputPath)" OutputType="$(OutputType)" ReferenceAssemblyPaths="@(ReferenceAssemblyPaths)"
                ReferenceAssemblies="@(XamlReferencesToCompile)" ForceSharedStateShutdown="False" CompileMode="RealBuildPass2"
                XAMLFingerprint="$(XAMLFingerprint)" FingerprintIgnorePaths="$(XAMLFingerprintIgnorePaths)" VCInstallDir="$(VCInstallDir)"
                WindowsSdkPath="$(WindowsSdkPath)" SavedStateFile="$(XamlSavedStateFilePath)" RootsLog="$(XamlRootsLog)"
                SuppressWarnings="$(SuppressXamlWarnings)" TargetPlatformMinVersion="$(TargetPlatformMinVersion)"
                XamlResourceMapName="$(XamlResourceMapName)" XamlComponentResourceLocation="$(XamlComponentResourceLocation)"
                FeatureControlFlags="@(XamlFeatureControlFlags)" VCInstallPath32="$(VCInstallPath32)" VCInstallPath64="$(VCInstallPath64)"
                GenXbfPath="$(GenXbfPath)" PrecompiledHeaderFile="$(PrecompiledHeaderFile)"
                EnableHotReloadStableConnectionIds="$(EnableHotReloadStableConnectionIds)"
                EnabledXamlOptionalChanges="$(EnabledXamlOptionalChanges)" DisabledXamlOptionalChanges="$(DisabledXamlOptionalChanges)" />
          </Target>
        </Project>
        """;

    private bool IsCpp => ProjectRunService.IsCppProject(_project);

    private string XamlInputs => Path.Combine(_directory, "xaml-inputs.json");

    private IReadOnlyList<string> CppBuildProperties =>
    [
        $"ForceImportAfterCppTargets={_targets}",
        $"WinAppHotReloadXamlInputs={XamlInputs}",
    ];

    private static readonly string[] CppSourceExtensions = [".xaml", ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".hxx", ".inl", ".idl", ".vcxproj", ".props", ".targets", ".appxmanifest", ".resw", ".manifest"];

    /// <summary>Folders a C++ build writes into, or NuGet restores into: never sources.</summary>
    private static readonly string[] CppBuildFolders = ["bin", "obj", "x64", "x86", "win32", "arm64", "arm", "debug", "release", "packages", "generated files", ".vs", ".git"];

    private bool IsCppSource(string path)
    {
        var relative = Path.GetRelativePath(_project.DirectoryName!, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return false;
        }
        var segments = relative.Split(Path.DirectorySeparatorChar);
        // The project's own intermediate folder is named after it (CppApp\x64\Debug).
        var projectFolder = Path.GetFileNameWithoutExtension(_project.Name);
        return CppSourceExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) &&
            !segments.SkipLast(1).Any(s => CppBuildFolders.Contains(s, StringComparer.OrdinalIgnoreCase) || s.Equals(projectFolder, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<int> RunCppAsync(ILaunchedProcess app, IAnsiConsole console, CancellationToken cancellationToken)
    {
        CppXamlSession? xaml = null;
        string? unavailable;
        try
        {
            (xaml, unavailable) = await CppXamlSession.StartAsync(_project, XamlInputs, Path.Combine(_directory, "xaml"), _job, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            unavailable = "the XAML compiler service could not start: " + ex.Message;
        }
        using var _ = xaml;
        Print(console, unavailable is null
            ? $"[watch] ready (C++: a XAML save applies live when its generated C++ is unchanged; other changes rebuild and restart the app; warm-up {xaml!.WarmUpMilliseconds} ms)"
            : $"[watch] ready (C++: every save rebuilds and restarts the app)");
        LastSettled = DateTime.UtcNow;
        if (unavailable is not null)
        {
            Print(console, "[watch] note: XAML edits can't apply live: " + unavailable);
        }

        using var saves = new CppSaves(_project.DirectoryName!, IsCppSource);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var appExit = app.WaitForExitAsync(stop.Token);
        try
        {
            while (true)
            {
                var saved = saves.NextAsync(stop.Token);
                await Task.WhenAny(saved, appExit);
                cancellationToken.ThrowIfCancellationRequested();
                if (app.HasExited || !saved.IsCompletedSuccessfully)
                {
                    await appExit;
                    return app.ExitCode;
                }
                var paths = saved.Result;
                var outcome = xaml is null
                    ? CppSaveOutcome.Restart($"{Describe(paths)}: XAML can't be applied live in this session")
                    : await xaml.ProcessAsync(paths, app.ProcessId, cancellationToken);
                foreach (var line in outcome.Lines)
                {
                    Print(console, line);
                }
                LastSettled = DateTime.UtcNow;
                if (outcome.NeedsRestart)
                {
                    RestartRequested = true;
                    console.MarkupLine("[yellow][[watch]] rebuilding and restarting the app...[/]");
                    app.Kill();
                    await app.WaitForExitAsync(CancellationToken.None);
                    return -1;
                }
            }
        }
        catch (OperationCanceledException)
        {
            app.Kill();
            return -1;
        }
        finally
        {
            stop.Cancel();
        }

        string Describe(IReadOnlyCollection<string> paths) => string.Join(", ", paths.Select(p => Path.GetRelativePath(_project.DirectoryName!, p)));
    }

    /// <summary>Saved C++ project sources, collected from one watcher for the whole run so a save made while another is handled isn't lost.</summary>
    private sealed class CppSaves : IDisposable
    {
        private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _signal = new(0);
        private readonly FileSystemWatcher _watcher;

        public CppSaves(string directory, Func<string, bool> isSource)
        {
            _watcher = new FileSystemWatcher(directory) { IncludeSubdirectories = true };
            void OnChange(string path)
            {
                if (!isSource(path))
                {
                    return;
                }
                lock (_changed)
                {
                    if (_changed.Add(path) && _changed.Count == 1)
                    {
                        _signal.Release();
                    }
                }
            }
            _watcher.Changed += (_, e) => OnChange(e.FullPath);
            _watcher.Created += (_, e) => OnChange(e.FullPath);
            _watcher.Deleted += (_, e) => OnChange(e.FullPath);
            _watcher.Renamed += (_, e) => { OnChange(e.OldFullPath); OnChange(e.FullPath); };
            _watcher.EnableRaisingEvents = true;
        }

        /// <summary>The next batch: changes that arrive within a short quiet period count as one save.</summary>
        public async Task<IReadOnlyCollection<string>> NextAsync(CancellationToken cancellationToken)
        {
            await _signal.WaitAsync(cancellationToken);
            int count;
            do
            {
                lock (_changed)
                {
                    count = _changed.Count;
                }
                await Task.Delay(250, cancellationToken);
            }
            while (count != Count());
            lock (_changed)
            {
                var batch = _changed.ToList();
                _changed.Clear();
                return batch;
            }
        }

        private int Count()
        {
            lock (_changed)
            {
                return _changed.Count;
            }
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _signal.Dispose();
        }
    }
}
internal sealed record CppSaveOutcome(bool NeedsRestart, IReadOnlyList<string> Lines)
{
    public static CppSaveOutcome Restart(string reason, params string[] detail) => new(true, ["[watch] restart " + reason, .. detail.Select(d => "  " + d)]);
}

/// <summary>
/// One XAML compiler service (XamlCompiler.exe --serve) for a running C++ app, and the running build's generated code to
/// compare its output with.
/// </summary>
internal sealed partial class CppXamlSession : IDisposable
{
    private static readonly string[] GeneratedCodeExtensions = [".h", ".hpp", ".cpp", ".idl"];

    private readonly Process _compiler;
    private readonly string _projectDirectory;
    private readonly string _workDirectory;
    private readonly IReadOnlyDictionary<string, string> _baseline;
    private readonly Dictionary<string, byte[]> _lastXbf;
    private readonly IReadOnlyList<string> _pages;
    private readonly string? _application;
    private int _requestId;
    private int _xbfCount;

    public long WarmUpMilliseconds { get; private set; }

    private CppXamlSession(Process compiler, string projectDirectory, string workDirectory, IReadOnlyDictionary<string, string> baseline,
        Dictionary<string, byte[]> lastXbf, IReadOnlyList<string> pages, string? application)
    {
        _compiler = compiler;
        _projectDirectory = projectDirectory;
        _workDirectory = workDirectory;
        _baseline = baseline;
        _lastXbf = lastXbf;
        _pages = pages;
        _application = application;
    }

    public static async Task<(CppXamlSession? Session, string? Unavailable)> StartAsync(FileInfo project, string inputs, string workDirectory,
        ExecutionTargets.GuestAgent.GuestJobObject? job, CancellationToken cancellationToken)
    {
        if (!File.Exists(inputs))
        {
            return (null, "the build did not report its XAML compiler inputs (is this a WinUI 3 C++/WinRT project?)");
        }
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(inputs, cancellationToken));
        var root = document.RootElement;
        var projectDirectory = project.DirectoryName!;
        var outputPath = root.GetProperty("OutputPath").GetString() ?? "";
        var generated = Path.GetFullPath(Path.Combine(projectDirectory, outputPath));
        var pages = Items(root, "XamlPages");
        var applications = Items(root, "XamlApplications");
        var compiler = FindCompiler(root, projectDirectory, out var compilerNote);
        if (compiler is null)
        {
            return (null, compilerNote);
        }

        // The running build's generated code and .xbf, read now: a restart's rebuild overwrites them.
        var baseline = Directory.EnumerateFiles(generated)
            .Where(p => GeneratedCodeExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .ToDictionary(p => Path.GetFileName(p), p => File.ReadAllText(p), StringComparer.OrdinalIgnoreCase);
        var lastXbf = Directory.EnumerateFiles(generated, "*.xbf")
            .ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(workDirectory);
        var start = new ProcessStartInfo(compiler)
        {
            WorkingDirectory = projectDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in new[] { "--serve", inputs, Path.Combine(workDirectory, "out") })
        {
            start.ArgumentList.Add(argument);
        }
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + compiler);
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        job?.Add(process.SafeHandle);
        var session = new CppXamlSession(process, projectDirectory, workDirectory, baseline, lastXbf, pages, applications.FirstOrDefault());
        try
        {
            // Warm the compiler with the whole project, and check that it reproduces the running build exactly: the
            // comparison every save relies on is meaningless otherwise.
            var watch = Stopwatch.StartNew();
            var pass1 = await session.CompileAsync(1, null, false, cancellationToken);
            var pass2 = await session.CompileAsync(2, null, true, cancellationToken);
            session.WarmUpMilliseconds = watch.ElapsedMilliseconds;
            if (pass1 is null || pass2 is null)
            {
                session.Dispose();
                return (null, $"{Path.GetFileName(compiler)} has no service mode (XamlCompiler.exe --serve); set {WatchSession.CompilerServiceVariable} to one that does");
            }
            var failed = pass1.Errors.Concat(pass2.Errors).FirstOrDefault();
            var different = session.ChangedGeneratedCode(pass1).Concat(session.ChangedGeneratedCode(pass2)).FirstOrDefault();
            if (failed is not null || different is not null)
            {
                session.Dispose();
                return (null, failed is not null
                    ? "the XAML compiler service could not compile the project as built: " + failed
                    : $"the XAML compiler service doesn't reproduce this build's {different!.File} ({different.Detail})");
            }
            return (session, null);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static List<string> Items(JsonElement root, string name) =>
        root.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array
            ? [.. items.EnumerateArray().Select(i => i.GetProperty("ItemSpec").GetString()!)]
            : [];

    /// <summary>The compiler to serve with: an explicit one, else the one in the app's WinUI package.</summary>
    private static string? FindCompiler(JsonElement root, string projectDirectory, out string? note)
    {
        note = null;
        if (Environment.GetEnvironmentVariable(WatchSession.CompilerServiceVariable) is { Length: > 0 } configured)
        {
            if (File.Exists(configured))
            {
                return configured;
            }
            note = $"{WatchSession.CompilerServiceVariable} names {configured}, which doesn't exist";
            return null;
        }
        // GenXbfPath is the package's tools folder (…\build\..\tools\net472\..\); its XamlCompiler.exe is the one the build would run.
        var tools = root.TryGetProperty("GenXbfPath", out var genXbf) && genXbf.GetString() is { Length: > 0 } path
            ? Path.GetFullPath(Path.Combine(projectDirectory, path))
            : null;
        var packaged = tools is null ? null : Path.Combine(tools, "net472", "XamlCompiler.exe");
        if (packaged is not null && File.Exists(packaged))
        {
            return packaged;
        }
        note = "the app's WinUI package has no XAML compiler executable";
        return null;
    }

    public async Task<CppSaveOutcome> ProcessAsync(IReadOnlyCollection<string> paths, uint processId, CancellationToken cancellationToken)
    {
        string Relative(string p) => Path.GetRelativePath(_projectDirectory, p);
        var code = paths.Where(p => !p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)).ToList();
        if (code.Count > 0)
        {
            return CppSaveOutcome.Restart($"{string.Join(", ", code.Select(Relative))} changed: C++ code and project files can't be applied to a running app");
        }
        var xaml = paths.Select(Relative).ToList();
        var unknown = xaml.Where(p => !_pages.Concat(_application is null ? [] : [_application]).Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
        {
            return CppSaveOutcome.Restart($"{string.Join(", ", unknown)}: not a XAML page of the running build (a new page needs its C++ class built)");
        }
        var deleted = xaml.Where(p => !File.Exists(Path.Combine(_projectDirectory, p))).ToList();
        if (deleted.Count > 0)
        {
            return CppSaveOutcome.Restart($"{string.Join(", ", deleted)} was deleted: its C++ class is still in the app");
        }

        var watch = Stopwatch.StartNew();
        var pass1 = await CompileAsync(1, xaml, false, cancellationToken);
        var pass2 = pass1 is { Errors.Count: 0 } ? await CompileAsync(2, xaml, false, cancellationToken) : null;
        var compileMs = watch.ElapsedMilliseconds;
        if (pass1 is null || (pass1.Errors.Count == 0 && pass2 is null))
        {
            return CppSaveOutcome.Restart($"{string.Join(", ", xaml)}: the XAML compiler service stopped");
        }
        var errors = pass1.Errors.Concat(pass2?.Errors ?? []).ToList();
        if (errors.Count > 0)
        {
            return new CppSaveOutcome(false, [.. errors.Select(e => "[watch] error " + e)]);
        }

        var changed = ChangedGeneratedCode(pass1).Concat(ChangedGeneratedCode(pass2!)).ToList();
        if (changed.Count > 0)
        {
            var first = changed[0];
            return CppSaveOutcome.Restart($"{first.File} changed: {first.Why}",
                [.. changed.Select(c => $"{c.File}: {c.Detail}"), $"(compile {compileMs} ms)"]);
        }
        if (pass2!.TypeInfoMayBeStale)
        {
            return CppSaveOutcome.Restart($"{string.Join(", ", xaml)}: XamlTypeInfo would change (the page uses a type or member the build didn't register)");
        }

        // Generated C++ unchanged: the page's compiled Connect still fits the new markup.
        var lines = new List<string>();
        foreach (var file in xaml)
        {
            var name = Path.GetFileName(file);
            var xbfName = Path.ChangeExtension(name, ".xbf");
            var xbf = pass2.Xbf.FirstOrDefault(x => Path.GetFileName(x.Key).Equals(xbfName, StringComparison.OrdinalIgnoreCase)).Value;
            if (xbf is null)
            {
                return CppSaveOutcome.Restart($"{file}: the XAML compiler produced no .xbf for it");
            }
            if (_lastXbf.TryGetValue(xbfName, out var last) && last.AsSpan().SequenceEqual(xbf))
            {
                lines.Add($"[watch] unchanged {file}");
                continue;
            }
            var component = Component(file);
            if (component is null)
            {
                return CppSaveOutcome.Restart($"{file}: no x:Class, or its generated InitializeComponent wasn't found");
            }
            var xbfPath = Path.Combine(_workDirectory, $"{Path.GetFileNameWithoutExtension(name)}.{++_xbfCount}.xbf");
            await File.WriteAllBytesAsync(xbfPath, xbf, cancellationToken);
            var apply = Stopwatch.StartNew();
            var result = await ReloadAsync(processId, component, xbfPath, cancellationToken);
            // App.xaml's resources are read when pages load: every page loads again.
            if (file.Equals(_application, StringComparison.OrdinalIgnoreCase) && result.Error is null)
            {
                foreach (var page in _pages)
                {
                    if (Component(page) is { } other)
                    {
                        var again = await ReloadAsync(processId, other, null, cancellationToken);
                        result = result with { Reloaded = result.Reloaded + again.Reloaded, Notes = [.. result.Notes, .. again.Notes], Errors = [.. result.Errors, .. again.Errors] };
                    }
                }
            }
            if (result.Error is not null)
            {
                return CppSaveOutcome.Restart($"{file}: the app could not take the new markup: {result.Error}");
            }
            if (result.Errors.Count > 0)
            {
                lines.AddRange(result.Errors.Select(e => $"[watch] error {file}: {e}"));
                continue;
            }
            _lastXbf[xbfName] = xbf;
            lines.Add(result.Instances == 0
                ? $"[watch] applied {file} (no live {component.ClassName}; the next one created uses the new markup; generated C++ unchanged; compile {compileMs} ms)"
                : $"[watch] applied {file} ({result.Reloaded} live {component.ClassName} reloaded; generated C++ unchanged; compile {compileMs} ms, apply {apply.ElapsedMilliseconds} ms)");
            lines.AddRange(result.Notes.Select(n => "  " + n));
        }
        return new CppSaveOutcome(false, lines);
    }

    private sealed record ComponentInfo(string ClassName, string Uri, string Location);

    /// <summary>The page's class and the URI and location its generated InitializeComponent loads.</summary>
    private ComponentInfo? Component(string relativeXaml)
    {
        string? className;
        try
        {
            className = XDocument.Load(Path.Combine(_projectDirectory, relativeXaml)).Root?.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
        if (className is null)
        {
            return null;
        }
        var stem = Path.GetFileName(relativeXaml);
        foreach (var generated in new[] { stem + ".g.hpp", stem + ".g.h" })
        {
            if (_baseline.TryGetValue(generated, out var text) && ResourceLocator().Match(text) is { Success: true } match)
            {
                var location = ResourceLocation().Match(text, match.Index) is { Success: true } l ? l.Groups[1].Value : "Application";
                return new(className, match.Groups[1].Value, location);
            }
        }
        return null;
    }

    [GeneratedRegex(@"resourceLocator\{\s*L""([^""]+)""\s*\}")]
    private static partial Regex ResourceLocator();

    [GeneratedRegex(@"ComponentResourceLocation::(\w+)")]
    private static partial Regex ResourceLocation();

    private sealed record ReloadResult(string? Error, int Instances, int Reloaded, IReadOnlyList<string> Notes, IReadOnlyList<string> Errors);

    private static Task<ReloadResult> ReloadAsync(uint processId, ComponentInfo component, string? xbfPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var response = new VisualTreeTap(processId).Request("HotReload.reloadComponent", w =>
            {
                w.WriteString("uri", component.Uri);
                w.WriteString("className", component.ClassName);
                w.WriteString("location", component.Location);
                if (xbfPath is not null)
                {
                    w.WriteString("xbf", xbfPath);
                }
            }, cancellationToken: cancellationToken);
            using var result = response.TryParseResult();
            if (!response.Ok || result is null)
            {
                return new ReloadResult(response.Error?.Message ?? "no answer", 0, 0, [], []);
            }
            var root = result.RootElement;
            static List<string> Strings(JsonElement e, string name) => [.. e.GetProperty(name).EnumerateArray().Select(s => s.GetString() ?? "")];
            return new ReloadResult(null, root.GetProperty("instances").GetInt32(), root.GetProperty("reloaded").GetInt32(), Strings(root, "notes"), Strings(root, "errors"));
        }, cancellationToken);

    private sealed record Compiled(Dictionary<string, string> Text, Dictionary<string, byte[]> Xbf, List<string> Errors, bool TypeInfoMayBeStale);

    private sealed record Difference(string File, string Why, string Detail);

    /// <summary>Generated C++ that differs from the running build's (a new file counts), with what the difference means.</summary>
    private IEnumerable<Difference> ChangedGeneratedCode(Compiled compiled)
    {
        foreach (var (path, text) in compiled.Text)
        {
            var name = Path.GetFileName(path);
            if (!GeneratedCodeExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!_baseline.TryGetValue(name, out var built))
            {
                yield return new(name, "a new generated file", "not in the running build");
                continue;
            }
            // The file on disk is the compiler's text with a final newline.
            var before = built.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            var after = text.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            if (before.SequenceEqual(after))
            {
                continue;
            }
            var line = 0;
            while (line < before.Length && line < after.Length && before[line] == after[line])
            {
                line++;
            }
            var was = line < before.Length ? before[line].Trim() : "(end of file)";
            var now = line < after.Length ? after[line].Trim() : "(end of file)";
            var added = after.Except(before).Count();
            var removed = before.Except(after).Count();
            yield return new(name, Why(name, added, removed),
                $"{(added == 0 && removed == 0 ? "the same lines in a different order" : $"+{added} -{removed} lines")}; first difference at line {line + 1}: was '{Clip(was)}', now '{Clip(now)}'");
        }

        static string Clip(string s) => s.Length > 100 ? s[..100] + "…" : s;
    }

    /// <summary>What a change to one of the compiler's generated files stands for, by the file's role.</summary>
    private static string Why(string file, int added, int removed)
    {
        var order = added == 0 && removed == 0;
        if (file.StartsWith("XamlTypeInfo", StringComparison.OrdinalIgnoreCase) || file.StartsWith("XamlMetaDataProvider", StringComparison.OrdinalIgnoreCase))
        {
            return "the app's XAML type information changed";
        }
        if (file.StartsWith("XamlBindingInfo", StringComparison.OrdinalIgnoreCase))
        {
            return "the shared x:Bind support code changed";
        }
        if (file.EndsWith(".xaml.g.h", StringComparison.OrdinalIgnoreCase))
        {
            return order
                ? "the page's named elements are declared in a different order"
                : "the page's C++ class changed (an x:Name added, removed or retyped, or its first x:Bind)";
        }
        if (file.EndsWith(".xaml.g.hpp", StringComparison.OrdinalIgnoreCase))
        {
            return order
                ? "the page's Connect wires the same elements in a different order"
                : "the page's compiled Connect or x:Bind code changed (an x:Name, event handler, x:Bind, or element numbering)";
        }
        return "generated C++ changed";
    }

    private async Task<Compiled?> CompileAsync(int pass, IReadOnlyList<string>? files, bool regenerateTypeInfo, CancellationToken cancellationToken)
    {
        var request = new MemoryStream();
        using (var writer = new Utf8JsonWriter(request))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Id", ++_requestId);
            writer.WriteNumber("Pass", pass);
            if (files is not null)
            {
                writer.WriteStartArray("Files");
                foreach (var file in files)
                {
                    writer.WriteStringValue(file);
                }
                writer.WriteEndArray();
            }
            writer.WriteBoolean("RegenerateTypeInfo", regenerateTypeInfo);
            writer.WriteEndObject();
        }
        await _compiler.StandardInput.WriteLineAsync(Encoding.UTF8.GetString(request.ToArray()).AsMemory(), cancellationToken);
        await _compiler.StandardInput.FlushAsync(cancellationToken);
        var line = await _compiler.StandardOutput.ReadLineAsync(cancellationToken);
        if (line is null || !line.StartsWith('{'))
        {
            return null;
        }
        using var response = JsonDocument.Parse(line);
        var root = response.RootElement;
        var text = root.GetProperty("GeneratedText").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
        var xbf = root.GetProperty("GeneratedXbf").EnumerateObject().ToDictionary(p => p.Name, p => Convert.FromBase64String(p.Value.GetString() ?? ""), StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var diagnostic in root.GetProperty("Diagnostics").EnumerateArray().Where(d => d.GetProperty("IsError").GetBoolean()))
        {
            var file = diagnostic.GetProperty("File").GetString();
            var where = file is { Length: > 0 }
                ? $"{Path.GetRelativePath(_projectDirectory, Path.GetFullPath(Path.Combine(_projectDirectory, file)))}({diagnostic.GetProperty("Line").GetInt32()},{diagnostic.GetProperty("Column").GetInt32()}): "
                : "";
            errors.Add($"{where}{diagnostic.GetProperty("Code").GetString()} {diagnostic.GetProperty("Message").GetString()?.Split('\n')[0].Trim()}");
        }
        if (!root.GetProperty("Success").GetBoolean() && errors.Count == 0)
        {
            errors.Add("the XAML compiler failed without saying why");
        }
        return new Compiled(text, xbf, errors, root.GetProperty("XamlTypeInfoMayBeStale").GetBoolean());
    }

    public void Dispose()
    {
        try
        {
            if (!_compiler.HasExited)
            {
                _compiler.StandardInput.WriteLine();
                if (!_compiler.WaitForExit(2000))
                {
                    _compiler.Kill();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        _compiler.Dispose();
    }
}
