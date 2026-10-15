// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace WinApp.HotReload;

/// <summary>
/// Watches a running app's project and applies each save to the app. Spawned by 'winapp run --watch'.
/// Prints one '[watch] &lt;outcome&gt; ...' line per save; exits with code 3 when the app must be rebuilt and restarted.
/// </summary>
internal static class Program
{
    public const int RestartExitCode = 3;

    public static async Task<int> Main(string[] args)
    {
        var options = Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("usage: WinApp.HotReload --project <csproj> --compiler-args <file> --targets <file> --pid <pid> --pipe <name> --token <token>");
            return 2;
        }
        try
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Value.Session.ProjectFile))!;
            SdkRoslyn.Register(SdkRoslyn.ResolveSdkDirectory(projectDirectory));
            return await RunAsync(options.Value.Session, options.Value.Pid).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[watch] error hot reload stopped: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static (SessionOptions Session, int Pid)? Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            values[args[i]] = args[i + 1];
        }
        return values.TryGetValue("--project", out var project) && values.TryGetValue("--compiler-args", out var compilerArgs) &&
            values.TryGetValue("--targets", out var targets) && values.TryGetValue("--pipe", out var pipe) &&
            values.TryGetValue("--token", out var token) && values.TryGetValue("--pid", out var pid) && int.TryParse(pid, out var processId)
            ? (new SessionOptions(project, compilerArgs, targets, pipe, token), processId)
            : null;
    }

    // Kept out of Main so no Roslyn type is loaded before the SDK resolver is registered.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> RunAsync(SessionOptions options, int pid)
    {
        using var cts = new CancellationTokenSource();
        using var app = Process.GetProcessById(pid);
        _ = app.WaitForExitAsync().ContinueWith(_ => cts.Cancel(), TaskScheduler.Default);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var stopwatch = Stopwatch.StartNew();
        // Builds the runtime can't update while they run: say why once, and restart the app on each save.
        if (HotReloadSession.IsOptimizedBuild(options.CscArgsFile))
        {
            return await RestartOnSaveAsync(options, pid, stopwatch,
                "this is an optimized build (for example -c Release), which the runtime can't update while it runs: saving a file rebuilds and restarts the app. Use the Debug configuration for live edits.",
                path => $"{(path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "code changes" : "changes")} to an optimized build take effect when the app restarts", cts.Token).ConfigureAwait(false);
        }
        if (StartupHooksDisabled(app))
        {
            return await RestartOnSaveAsync(options, pid, stopwatch,
                "this build turns off .NET startup hooks (System.StartupHookProvider.IsSupported is false, for example because PublishTrimmed is set for this configuration), so winapp's hot reload agent can't load: saving a file rebuilds and restarts the app.",
                _ => "changes take effect when the app restarts", cts.Token).ConfigureAwait(false);
        }
        HotReloadSession session;
        try
        {
            session = await HotReloadSession.CreateAsync(options, cts.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return await RestartOnSaveAsync(options, pid, stopwatch,
                "the app didn't load winapp's hot reload agent, so saving a file rebuilds and restarts the app.",
                _ => "changes take effect when the app restarts", cts.Token).ConfigureAwait(false);
        }
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changes = Channel.CreateUnbounded<string>();
        // The app's folder, and those of the projects it references outside it (added as they are referenced).
        var watchers = new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        void WatchRoots()
        {
            foreach (var root in session.WatchRoots.Where(r => !watchers.ContainsKey(r) && Directory.Exists(r)))
            {
                lock (hashes)
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => IsWatched(session, f)))
                    {
                        hashes[file] = Hash(file);
                    }
                }
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Changed += (_, e) => Enqueue(e.FullPath);
                watcher.Created += (_, e) => Enqueue(e.FullPath);
                watcher.Deleted += (_, e) => { Enqueue(e.FullPath); EnqueueFolder(e.FullPath, null); };
                watcher.Renamed += (_, e) =>
                {
                    Enqueue(e.OldFullPath);
                    Enqueue(e.FullPath);
                    if (Directory.Exists(e.FullPath))
                    {
                        EnqueueFolder(e.OldFullPath, e.FullPath);
                    }
                };
                watcher.EnableRaisingEvents = true;
                watchers[root] = watcher;
            }
            // The Directory.Build files of the folders above: only those files, not the folders' contents.
            foreach (var folder in session.BuildFileFolders().Where(f => !watchers.ContainsKey(f) && Directory.Exists(f)))
            {
                lock (hashes)
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "Directory.*").Where(f => IsWatched(session, f)))
                    {
                        hashes[file] = Hash(file);
                    }
                }
                var watcher = new FileSystemWatcher(folder, "Directory.*")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Changed += (_, e) => Enqueue(e.FullPath);
                watcher.Created += (_, e) => Enqueue(e.FullPath);
                watcher.Deleted += (_, e) => Enqueue(e.FullPath);
                watcher.Renamed += (_, e) => { Enqueue(e.OldFullPath); Enqueue(e.FullPath); };
                watcher.EnableRaisingEvents = true;
                watchers[folder] = watcher;
            }
        }
        void Enqueue(string path)
        {
            if (IsWatched(session, path))
            {
                changes.Writer.TryWrite(path);
            }
        }
        // A folder renamed, moved or deleted: every file in it moved or went away.
        void EnqueueFolder(string? oldFolder, string? newFolder)
        {
            if (oldFolder is not null)
            {
                lock (hashes)
                {
                    foreach (var file in hashes.Keys.Where(k => k.StartsWith(oldFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        Enqueue(file);
                    }
                }
            }
            if (newFolder is not null && Directory.Exists(newFolder))
            {
                foreach (var file in Directory.EnumerateFiles(newFolder, "*", SearchOption.AllDirectories))
                {
                    Enqueue(file);
                }
            }
        }
        WatchRoots();
        // The running binary was compiled when the compiler arguments were written; a save after that isn't in it.
        var built = File.GetLastWriteTimeUtc(options.CscArgsFile);
        // Saves made while the app was starting: the session diffs them against the sources as built (see AsBuilt).
        foreach (var late in hashes.Keys.Where(file => File.GetLastWriteTimeUtc(file) > built).ToList())
        {
            changes.Writer.TryWrite(late);
            hashes[late] = "";
        }
        Console.WriteLine($"[watch] ready ({stopwatch.ElapsedMilliseconds} ms): saved .cs, .xaml and project file changes apply to the running app (PID {pid})");
        if (session.LaunchNote is { } note)
        {
            Console.WriteLine($"[watch] note: {note}");
        }
        // Files from a save that reported an error are processed again with the next save, so nothing in that batch is lost.
        var retry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            while (true)
            {
                var batch = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { await changes.Reader.ReadAsync(cts.Token).ConfigureAwait(false) };
                using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
                {
                    while (true)
                    {
                        quiet.CancelAfter(150);
                        try
                        {
                            batch.Add(await changes.Reader.ReadAsync(quiet.Token).ConfigureAwait(false));
                            quiet.TryReset();
                        }
                        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }

                List<string> changed;
                // A save can show up while it's still being written (truncated, then filled): an empty or half-written
                // file would look like code deleted. Each file is read once it stops changing.
                await WaitUntilWrittenAsync(batch, cts.Token).ConfigureAwait(false);
                lock (hashes)
                {
                    changed = batch.Where(path =>
                    {
                        var hash = File.Exists(path) ? Hash(path) : "";
                        var differs = !hashes.TryGetValue(path, out var previous) ? hash.Length > 0 : previous != hash;
                        hashes[path] = hash;
                        return differs;
                    }).ToList();
                }
                if (changed.Count == 0)
                {
                    continue;
                }
                changed = changed.Union(retry, StringComparer.OrdinalIgnoreCase).ToList();

                stopwatch.Restart();
                SaveResult result;
                try
                {
                    result = await session.ProcessAsync(changed, cts.Token).ConfigureAwait(false);
                }
                catch (CompilerCrashException ex)
                {
                    Console.Error.WriteLine(ex.InnerException);
                    if (Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_LOG") is { Length: > 0 } log)
                    {
                        File.AppendAllText(log + ".helper", $"{DateTime.Now:HH:mm:ss.fff} {ex.InnerException}{Environment.NewLine}");
                    }
                    result = new SaveResult(Outcome.Restart, $"{string.Join(", ", changed.Select(Path.GetFileName))}: .NET hot reload's compiler failed on this change " +
                        $"({ex.InnerException?.GetType().Name ?? ex.GetType().Name} in Roslyn's edit and continue); it takes effect when the app restarts");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine(ex);
                    if (Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_LOG") is { Length: > 0 } log)
                    {
                        File.AppendAllText(log + ".helper", $"{DateTime.Now:HH:mm:ss.fff} {ex}{Environment.NewLine}");
                    }
                    result = new SaveResult(Outcome.Error, $"internal error: {ex.Message}");
                }
                retry = new HashSet<string>(result.Outcome == Outcome.Error ? changed : result.StillFailing ?? [], StringComparer.OrdinalIgnoreCase);
                WatchRoots();
                var outcome = result.Outcome.ToString().ToLowerInvariant();
                Console.WriteLine($"[watch] {outcome} {result.Detail} ({stopwatch.ElapsedMilliseconds} ms)");
                if (result.Outcome == Outcome.Restart)
                {
                    return RestartExitCode;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            foreach (var watcher in watchers.Values)
            {
                watcher.Dispose();
            }
            try
            {
                Directory.Delete(Path.Combine(Path.GetTempPath(), "winapp-hotreload", options.PipeName), recursive: true);
            }
            catch (IOException)
            {
                // Markup files can still be mapped by an app that is shutting down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>True when the running app's runtimeconfig.json turns startup hooks off (no agent can load).</summary>
    private static bool StartupHooksDisabled(Process app)
    {
        try
        {
            if (app.MainModule?.FileName is not { } exe)
            {
                return false;
            }
            var config = Path.ChangeExtension(exe, ".runtimeconfig.json");
            return File.Exists(config) && System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(config), @"""System\.StartupHookProvider\.IsSupported""\s*:\s*false");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// For a build that can't be updated live: ready, one note saying why, then the first save restarts the app (with
    /// <paramref name="reason"/> for the file saved).
    /// </summary>
    private static async Task<int> RestartOnSaveAsync(SessionOptions options, int pid, Stopwatch stopwatch, string note, Func<string, string> reason, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.ProjectFile))!;
        var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        void Changed(string path)
        {
            var relative = Path.GetRelativePath(directory, path);
            var excluded = relative.Split(Path.DirectorySeparatorChar).SkipLast(1).Any(part => part is "bin" or "obj" or ".git" or ".vs");
            if (!excluded && (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".resw", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".appxmanifest", StringComparison.OrdinalIgnoreCase) || HotReloadSession.IsAsset(path)))
            {
                saved.TrySetResult(relative);
            }
        }
        watcher.Changed += (_, e) => Changed(e.FullPath);
        watcher.Created += (_, e) => Changed(e.FullPath);
        watcher.Deleted += (_, e) => Changed(e.FullPath);
        watcher.Renamed += (_, e) => Changed(e.FullPath);
        watcher.EnableRaisingEvents = true;
        Console.WriteLine($"[watch] ready ({stopwatch.ElapsedMilliseconds} ms): saved files rebuild and restart the app (PID {pid})");
        Console.WriteLine($"[watch] note: {note}");
        try
        {
            var file = await saved.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            // Let the editor finish writing (and any files saved with it).
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[watch] restart {file}: {reason(file)} (0 ms)");
            return RestartExitCode;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    private static bool IsWatched(HotReloadSession session, string path) => session.IsBuildFile(path) ||
        (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".resw", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".appxmanifest", StringComparison.OrdinalIgnoreCase) ||
         HotReloadSession.IsAsset(path)) && session.IsWatched(path);

    /// <summary>
    /// Waits (up to about 2 seconds) until each file's size and write time hold still for 50 ms, and an empty source file
    /// for a further 500 ms: a save is often a truncate followed by a write, and a busy machine can deliver the truncate
    /// first. A file that really was emptied is processed after that.
    /// </summary>
    private static async Task WaitUntilWrittenAsync(IReadOnlyCollection<string> files, CancellationToken cancellationToken)
    {
        static (long Length, DateTime Written) Stamp(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, default);
            }
            catch (IOException)
            {
                return (-2, DateTime.UtcNow);
            }
        }
        var deadline = DateTime.UtcNow.AddSeconds(2);
        var stamps = files.ToDictionary(f => f, Stamp, StringComparer.OrdinalIgnoreCase);
        var emptySince = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var settled = true;
            foreach (var file in files)
            {
                var now = Stamp(file);
                if (now != stamps[file])
                {
                    stamps[file] = now;
                    settled = false;
                }
                if (now.Length == 0 && (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
                {
                    var since = emptySince.TryGetValue(file, out var first) ? first : emptySince[file] = DateTime.UtcNow;
                    settled &= DateTime.UtcNow - since >= TimeSpan.FromMilliseconds(500);
                }
            }
            if (settled)
            {
                return;
            }
        }
    }

    private static string Hash(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (FileNotFoundException)
            {
                return "";
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(30);
            }
        }
    }
}
