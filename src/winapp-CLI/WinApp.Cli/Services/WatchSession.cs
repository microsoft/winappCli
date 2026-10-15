// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;

namespace WinApp.Cli.Services;

/// <summary>
/// The CLI half of 'winapp run --watch': instruments the build so the hot reload helper can reproduce the exact
/// compilation, launches the app with the in-app agent, and runs the helper until the app exits or needs a restart.
/// </summary>
internal sealed class WatchSession
{
    private const int RestartExitCode = 3;

    // CoreCompile is forced (a missing input) so the build reports the compiler command line every launch.
    private const string Targets = """
        <Project>
          <ItemGroup Condition="'$(WinAppHotReloadCapture)' == 'true'">
            <CustomAdditionalCompileInputs Include="$(MSBuildThisFileDirectory)winapp-hotreload-force-compile.missing" />
          </ItemGroup>
          <Target Name="WinAppHotReloadCaptureCompile" AfterTargets="CoreCompile" Condition="'$(WinAppHotReloadCapture)' == 'true'">
            <WriteLinesToFile File="$(WinAppHotReloadOut)" Lines="@(CscCommandLineArgs)" Overwrite="true" />
            <WriteLinesToFile File="$(WinAppHotReloadOut).props" Lines="Configuration=$(Configuration);Platform=$(Platform);RuntimeIdentifier=$(RuntimeIdentifier);TargetFramework=$(TargetFramework)" Overwrite="true" />
            <!-- The sources as compiled, so a save made while the app starts can still be diffed against them. -->
            <ItemGroup>
              <_WinAppHotReloadSource Include="@(Compile)" Condition="!$([System.IO.Path]::IsPathRooted('%(Compile.Identity)')) and !$([System.String]::Copy('%(Compile.Identity)').StartsWith('..'))" />
            </ItemGroup>
            <Copy SourceFiles="@(_WinAppHotReloadSource)" DestinationFiles="@(_WinAppHotReloadSource->'$(WinAppHotReloadOut).src\%(Identity)')" SkipUnchangedFiles="true" />
          </Target>
          <Target Name="WinAppHotReloadReferences" DependsOnTargets="ResolveReferences;FindReferenceAssembliesForReferences;AssignTargetPaths">
            <!-- Data files the build copies next to the app (None items with CopyToOutputDirectory), like Content. -->
            <ItemGroup>
              <_WinAppHotReloadCopied Include="@(_NoneWithTargetPath)" Condition="'%(_NoneWithTargetPath.CopyToOutputDirectory)' == 'Always' or '%(_NoneWithTargetPath.CopyToOutputDirectory)' == 'PreserveNewest'" />
            </ItemGroup>
            <WriteLinesToFile File="$(WinAppHotReloadOut)" Lines="@(ReferencePathWithRefAssemblies->'/reference:%(FullPath)');@(Analyzer->'/analyzer:%(FullPath)');@(ReferenceCopyLocalPaths->'/copylocal:%(FullPath)|%(DestinationSubDirectory)');@(ContentWithTargetPath->'/content:%(FullPath)|%(TargetPath)');@(_WinAppHotReloadCopied->'/content:%(FullPath)|%(TargetPath)');@(Compile->'/item:%(FullPath)');@(Page->'/item:%(FullPath)')" Overwrite="true" />
          </Target>
        </Project>
        """;

    private readonly FileInfo _project;
    private readonly string _directory;
    private readonly string _targets;
    private readonly string _compilerArgs;
    private readonly string _pipe = "winapp-hotreload-" + Guid.NewGuid().ToString("N");
    private readonly string _token = Guid.NewGuid().ToString("N");

    /// <summary>The app and the helper die with winapp, however it ends (a closed console, a killed process).</summary>
    private readonly GuestJobObject? _job;

    /// <summary>Held for the session: a second watch of the same project is refused instead of fighting over its build output.</summary>
    private readonly FileStream? _lock;

    private WatchSession(FileInfo project, FileStream projectLock)
    {
        _project = project;
        _lock = projectLock;
        SweepStaleFolders();
        _directory = Path.Combine(Path.GetTempPath(), "winapp-watch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, OwnerFile), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            _job = GuestJobObject.Create();
        }
        catch (ExecutionTargetException)
        {
            // Without a job, a winapp killed outright leaves the app running, as a plain run does.
        }
        _targets = Path.Combine(_directory, "winapp-hotreload.targets");
        _compilerArgs = Path.Combine(_directory, "csc.rsp");
        File.WriteAllText(_targets, Targets);
        // An old timestamp keeps the import from making the rest of the build (e.g. the XAML compiler) out of date.
        File.SetLastWriteTimeUtc(_targets, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "WinApp.HotReload.dll");

    private const string OwnerFile = "owner.pid";

    /// <summary>Starts a watch of <paramref name="project"/>, or returns null with the PID of the winapp already watching it.</summary>
    public static WatchSession? TryStart(FileInfo project, out int? watchingPid)
    {
        watchingPid = null;
        var obj = Path.Combine(project.DirectoryName!, "obj");
        Directory.CreateDirectory(obj);
        var path = Path.Combine(obj, "winapp-watch.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.DeleteOnClose);
            stream.SetLength(0);
            var pid = System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            stream.Write(pid);
            stream.Flush();
            return new WatchSession(project, stream);
        }
        catch (IOException)
        {
            try
            {
                using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                watchingPid = int.TryParse(new StreamReader(reader).ReadToEnd(), out var pid) ? pid : null;
            }
            catch (IOException)
            {
            }
            return null;
        }
    }

    /// <summary>Temp folders of earlier sessions whose winapp (or app) is gone, e.g. after it was killed.</summary>
    private static void SweepStaleFolders()
    {
        foreach (var root in new[] { "winapp-watch", "winapp-hotreload" })
        {
            var parent = Path.Combine(Path.GetTempPath(), root);
            if (!Directory.Exists(parent))
            {
                continue;
            }
            foreach (var directory in Directory.EnumerateDirectories(parent))
            {
                var owner = Path.Combine(directory, OwnerFile);
                int? pid = File.Exists(owner) && int.TryParse(File.ReadAllText(owner), out var p) ? p : null;
                // Unmarked: an older winapp's, kept a while in case it still runs.
                var stale = pid is { } id ? !IsRunning(id) : Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddHours(-2);
                if (stale)
                {
                    try
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            // Crash notes a killed winapp never read.
            foreach (var file in Directory.EnumerateFiles(parent, "*.crash*.txt").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-2)))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // A process this user can't open (the PID was reused by another account's process): it exists, so the
            // folder is left alone rather than risk removing a live session's.
            return true;
        }
    }

    /// <summary>Why the app last stopped unexpectedly, as the in-app agent recorded it.</summary>
    public string? CrashReport(int exitCode)
    {
        var crash = Take(CrashFile);
        var last = Take(LastExceptionFile);
        // A fail-fast for an exception that left a UI-thread callback (STATUS_STOWED_EXCEPTION): the last one thrown there.
        return crash ?? (exitCode == unchecked((int)0xC000027B) && last is not null ? "the last exception thrown on the UI thread: " + last : null);

        static string? Take(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                var text = File.ReadAllText(path).Trim();
                File.Delete(path);
                return text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private string CrashFile => Path.Combine(Path.GetTempPath(), "winapp-hotreload", _pipe + ".crash.txt");
    private string LastExceptionFile => Path.Combine(Path.GetTempPath(), "winapp-hotreload", _pipe + ".crash.last.txt");

    public void DeleteTemporaryFiles()
    {
        try
        {
            File.Delete(CrashFile);
            File.Delete(LastExceptionFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        _lock?.Dispose();
        _job?.Dispose();
        // The helper's markup folder too: the helper is stopped without a chance to clean up after itself.
        foreach (var directory in new[] { _directory, Path.Combine(Path.GetTempPath(), "winapp-hotreload", _pipe) })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a temp folder left behind is harmless.
            }
        }
    }

    /// <summary>Set when the last run ended because a change needs a rebuild.</summary>
    public bool RestartRequested { get; set; }

    /// <summary>Set once the app of the current run has been launched (false when its build failed).</summary>
    public bool Launched { get; set; }

    /// <summary>Waits until a watched source file of the project changes after <paramref name="since"/>, e.g. to retry a failed rebuild.</summary>
    public async Task WaitForSourceChangeAsync(DateTime since, CancellationToken cancellationToken)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(_project.DirectoryName!) { IncludeSubdirectories = true };
        void OnChange(object sender, FileSystemEventArgs e)
        {
            if (IsSource(e.FullPath))
            {
                changed.TrySetResult();
            }
        }
        watcher.Changed += OnChange;
        watcher.Created += OnChange;
        watcher.Deleted += OnChange;
        watcher.Renamed += OnChange;
        watcher.EnableRaisingEvents = true;
        // A save made while the failed build ran (or before the watcher started) counts too.
        foreach (var file in Directory.EnumerateFiles(_project.DirectoryName!, "*", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(file) > since)
            {
                OnChange(watcher, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(file)!, Path.GetFileName(file)));
            }
        }
        await changed.Task.WaitAsync(cancellationToken);
        await Task.Delay(300, cancellationToken);
    }

    private bool IsSource(string path)
    {
        var segments = Path.GetRelativePath(_project.DirectoryName!, path).Split(Path.DirectorySeparatorChar);
        return Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".xaml" or ".csproj" or ".appxmanifest" or ".resw" &&
            !segments.SkipLast(1).Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Helper lines that end handling a save (or say it is ready): files saved after the last of them haven't reached the app.</summary>
    private static readonly string[] SettledPrefixes = ["[watch] ready", "[watch] applied", "[watch] unchanged", "[watch] error", "[watch] waiting"];

    /// <summary>When the helper last finished handling a save, or null before it is ready.</summary>
    public DateTime? LastSettled { get; private set; }

    /// <summary>Whether a source file was saved after the helper last finished a save: a change the app never got.</summary>
    public bool HasUnappliedSave() => LastSettled is { } settled &&
        Directory.EnumerateFiles(_project.DirectoryName!, "*", SearchOption.AllDirectories).Any(file => IsSource(file) && File.GetLastWriteTimeUtc(file) > settled);

    public IReadOnlyList<string> BuildProperties =>
    [
        "ProvideCommandLineArgs=true",
        $"CustomAfterMicrosoftCommonTargets={_targets}",
        "WinAppHotReloadCapture=true",
        $"WinAppHotReloadOut={_compilerArgs}",
        // The compiler pipeline applies the XAML compiler's code as .NET hot reload deltas: stable connection IDs keep an
        // element's generated members when elements are inserted or removed before it. WINAPP_HOT_RELOAD_STABLE_IDS=0 turns them off.
        .. string.Equals(Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_PIPELINE"), "compiler", StringComparison.OrdinalIgnoreCase) &&
            Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_STABLE_IDS") != "0"
            ? new[] { "EnableHotReloadStableConnectionIds=true" } : [],
    ];

    public IReadOnlyDictionary<string, string?> Augment(IReadOnlyDictionary<string, string?>? environment)
    {
        var result = new Dictionary<string, string?>(environment ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_MODIFIABLE_ASSEMBLIES"] = "debug",
            ["WINAPP_HOT_RELOAD_PIPE"] = _pipe,
            ["WINAPP_HOT_RELOAD_TOKEN"] = _token,
        };
        return result;
    }

    /// <summary>Runs the helper alongside the app. Returns the app's exit code, or -1 when it was stopped for a restart or Ctrl+C.</summary>
    public async Task<int> RunAsync(ILaunchedProcess app, IAnsiConsole console, ILogger logger, CancellationToken cancellationToken)
    {
        RestartRequested = false;
        Launched = true;
        if (!File.Exists(_compilerArgs))
        {
            console.MarkupLine("[yellow][[watch]] hot reload is unavailable: the build did not report its compiler command line.[/]");
            return await WaitAsync(app, cancellationToken);
        }
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _project.DirectoryName!,
        };
        foreach (var argument in new[] { "exec", HelperPath, "--project", _project.FullName, "--compiler-args", _compilerArgs, "--targets", _targets,
            "--pid", app.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--pipe", _pipe, "--token", _token })
        {
            start.ArgumentList.Add(argument);
        }

        using var helper = Process.Start(start) ?? throw new InvalidOperationException("Could not start the hot reload helper.");
        if (_job is not null)
        {
            _job.Add(helper.SafeHandle);
            try
            {
                using var appProcess = Process.GetProcessById((int)app.ProcessId);
                _job.Add(appProcess.SafeHandle);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
        helper.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } settled && SettledPrefixes.Any(p => settled.StartsWith(p, StringComparison.Ordinal)))
            {
                LastSettled = DateTime.UtcNow;
            }
            // A session-wide note (XAML hot reload not enabled for this build) is said once, not after every restart.
            if (e.Data is { } line && (!line.StartsWith("[watch] note:", StringComparison.Ordinal) || _notes.Add(line)))
            {
                Print(console, line);
            }
        };
        helper.ErrorDataReceived += (_, e) => { if (e.Data is { } line) { logger.LogDebug("{Line}", line); } };
        helper.BeginOutputReadLine();
        helper.BeginErrorReadLine();
        try
        {
            var appExit = app.WaitForExitAsync(cancellationToken);
            var helperExit = helper.WaitForExitAsync(cancellationToken);
            await Task.WhenAny(appExit, helperExit);
            cancellationToken.ThrowIfCancellationRequested();
            if (helperExit.IsCompleted && !app.HasExited)
            {
                helper.WaitForExit();
                if (helper.ExitCode == RestartExitCode)
                {
                    RestartRequested = true;
                    console.MarkupLine("[yellow][[watch]] rebuilding and restarting the app...[/]");
                    app.Kill();
                    await app.WaitForExitAsync(CancellationToken.None);
                    return -1;
                }
                // The helper stopped (its last line says why): the app keeps running, and the next save rebuilds and
                // restarts it rather than going unnoticed.
                console.MarkupLine("[yellow][[watch]] live edits stopped; the next save rebuilds and restarts the app[/]");
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var saved = WaitForSourceChangeAsync(DateTime.UtcNow, stop.Token);
                var exited = app.WaitForExitAsync(stop.Token);
                await Task.WhenAny(saved, exited);
                cancellationToken.ThrowIfCancellationRequested();
                if (saved.IsCompletedSuccessfully && !app.HasExited)
                {
                    stop.Cancel();
                    RestartRequested = true;
                    console.MarkupLine("[yellow][[watch]] rebuilding and restarting the app...[/]");
                    app.Kill();
                    await app.WaitForExitAsync(CancellationToken.None);
                    return -1;
                }
                stop.Cancel();
                return app.ExitCode;
            }
            return app.ExitCode;
        }
        catch (OperationCanceledException)
        {
            app.Kill();
            return -1;
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task<int> WaitAsync(ILaunchedProcess app, CancellationToken cancellationToken)
    {
        try
        {
            await app.WaitForExitAsync(cancellationToken);
            return app.ExitCode;
        }
        catch (OperationCanceledException)
        {
            app.Kill();
            return -1;
        }
    }

    private readonly HashSet<string> _notes = new(StringComparer.Ordinal);

    private static void Print(IAnsiConsole console, string line)
    {
        if (Console.IsOutputRedirected)
        {
            // Redirected output: keep each status on one unwrapped line for tools that read it.
            console.Profile.Out.Writer.WriteLine(line);
            console.Profile.Out.Writer.Flush();
            return;
        }
        var color = line.StartsWith("[watch] applied", StringComparison.Ordinal) || line.StartsWith("[watch] ready", StringComparison.Ordinal) ? "green"
            : line.StartsWith("[watch] waiting", StringComparison.Ordinal) || line.StartsWith("[watch] restart", StringComparison.Ordinal) ? "yellow"
            : line.StartsWith("[watch] error", StringComparison.Ordinal) ? "red"
            : line.StartsWith("[watch] note", StringComparison.Ordinal) ? "yellow"
            : null;
        console.MarkupLine(color is null ? Markup.Escape(line) : $"[{color}]{Markup.Escape(line)}[/]");
    }
}
