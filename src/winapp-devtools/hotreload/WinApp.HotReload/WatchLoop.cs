// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace WinApp.HotReload;

/// <summary>
/// Watches the app's files and turns saves into one outcome line each: <c>[watch] applied|unchanged|not applied|waiting|error|restart</c>.
/// A save is handled once the files settle (no change for 100 ms, each changed file stable for 100 ms). An error or a
/// restart is printed only after 2 more seconds of quiet: an editor's rename or an agent writing files one by one passes
/// through states that don't compile, and only the final one counts. A file still being written doesn't parse or
/// compile either, so that wait also covers a slow writer.
/// </summary>
internal sealed class WatchLoop : IDisposable
{
    public const int RestartExitCode = 3;
    private static readonly string[] Extensions = [".cs", ".xaml", ".csproj", ".resw", ".appxmanifest"];
    private static readonly string[] BuildFiles = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];
    private static readonly string[] Excluded = ["bin", "obj", ".git", ".vs", "node_modules"];
    internal static TimeSpan Quiet = TimeSpan.FromMilliseconds(100);
    internal static TimeSpan Stable = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);
    internal static TimeSpan QuietBeforeFailure = TimeSpan.FromSeconds(2);

    private readonly List<string> _roots;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Channel<string> _changes = Channel.CreateUnbounded<string>();
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts watching right away, so saves made while the engine loads aren't missed.</summary>
    /// <param name="asBuilt">For a file, the copy of it the running app was compiled from, if the build kept one.</param>
    public WatchLoop(IEnumerable<string> roots, Func<string, string?> asBuilt)
    {
        _roots = [.. roots.Where(Directory.Exists)];
        foreach (var root in _roots)
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, e) => Enqueue(e.FullPath);
            watcher.Created += (_, e) => Enqueue(e.FullPath);
            watcher.Deleted += (_, e) => Enqueue(e.FullPath);
            watcher.Renamed += (_, e) => { Enqueue(e.OldFullPath); Enqueue(e.FullPath); };
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(IsWatched))
            {
                // Compared with what the app was compiled from: a file saved since then is a change to handle first.
                var built = asBuilt(file) is { } copy && File.Exists(copy) ? Hash(copy) : null;
                var now = Hash(file);
                lock (_hashes)
                {
                    _hashes[file] = built ?? now;
                }
                if (built is not null && built != now)
                {
                    _changes.Writer.TryWrite(file);
                }
            }
        }
    }

    public bool IsWatched(string path)
    {
        var name = Path.GetFileName(path);
        if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && !BuildFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        var root = _roots.FirstOrDefault(r => ProjectGraph.IsUnder(path, r));
        return root is not null && !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).SkipLast(1)
            .Any(part => Excluded.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private void Enqueue(string path)
    {
        if (IsWatched(path))
        {
            _changes.Writer.TryWrite(path);
        }
    }

    /// <summary>Handles saves until the app exits (returns 0) or a save needs a restart (returns <see cref="RestartExitCode"/>).</summary>
    public async Task<int> RunAsync(Save save, CancellationToken cancellationToken)
    {
        // The files of a save that failed are handled again with the next save, so a fix doesn't drop the rest of it.
        var retry = new List<string>();
        try
        {
            while (true)
            {
                var batch = await NextBatchAsync(cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    continue;
                }
                batch.AddRange(retry.Where(f => !batch.Contains(f, StringComparer.OrdinalIgnoreCase)));
                var clock = Stopwatch.StartNew();
                SaveResult result;
                while (true)
                {
                    result = await HandleAsync(save, batch, cancellationToken).ConfigureAwait(false);
                    if (result.Outcome is not (Outcome.Error or Outcome.Waiting or Outcome.Restart) || !await MoreChangesAsync(batch, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                var word = result.Outcome == Outcome.NotApplied ? "not applied" : result.Outcome.ToString().ToLowerInvariant();
                Console.WriteLine($"[watch] {word} {result.Detail} ({clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s)");
                foreach (var note in result.Notes ?? [])
                {
                    Console.WriteLine($"  note: {note}");
                }
                if (result.Outcome == Outcome.Restart)
                {
                    return RestartExitCode;
                }
                retry = result.Outcome is Outcome.Error or Outcome.Waiting ? batch : [];
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static async Task<SaveResult> HandleAsync(Save save, List<string> batch, CancellationToken cancellationToken)
    {
        try
        {
            return await save.RunAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // .NET's hot reload compiler can fail on a change it accepts; a rebuild applies it.
            Console.Error.WriteLine(ex);
            return new SaveResult(Outcome.Restart, $"{string.Join(", ", batch.Select(Path.GetFileName))}: .NET hot reload failed on this change " +
                $"({ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}); it takes effect when the app restarts");
        }
    }

    /// <summary>Waits <see cref="QuietBeforeFailure"/> for more saves; true (with them added to the batch) when there were some.</summary>
    private async Task<bool> MoreChangesAsync(List<string> batch, CancellationToken cancellationToken)
    {
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        quiet.CancelAfter(QuietBeforeFailure);
        try
        {
            await _changes.Reader.WaitToReadAsync(quiet.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        var more = await NextBatchAsync(cancellationToken).ConfigureAwait(false);
        batch.AddRange(more.Where(f => !batch.Contains(f, StringComparer.OrdinalIgnoreCase)));
        return true;
    }

    /// <summary>The next settled save: the files whose content changed.</summary>
    private async Task<List<string>> NextBatchAsync(CancellationToken cancellationToken)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { await _changes.Reader.ReadAsync(cancellationToken).ConfigureAwait(false) };
        var stamps = new Dictionary<string, ((long, DateTime) Stamp, DateTime Since)>(StringComparer.OrdinalIgnoreCase);
        var lastEvent = DateTime.UtcNow;
        while (true)
        {
            while (_changes.Reader.TryRead(out var path))
            {
                files.Add(path);
                lastEvent = DateTime.UtcNow;
            }
            var now = DateTime.UtcNow;
            var settled = now - lastEvent >= Quiet;
            foreach (var file in files)
            {
                var stamp = Stamp(file);
                if (!stamps.TryGetValue(file, out var seen) || seen.Stamp != stamp)
                {
                    stamps[file] = (stamp, now);
                }
                settled &= now - stamps[file].Since >= Stable;
            }
            if (settled)
            {
                break;
            }
            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
        lock (_hashes)
        {
            return [.. files.Where(file =>
            {
                var hash = File.Exists(file) ? Hash(file) : "";
                var changed = _hashes.TryGetValue(file, out var previous) ? previous != hash : hash.Length > 0;
                _hashes[file] = hash;
                return changed;
            })];
        }
    }

    private static (long, DateTime) Stamp(string path)
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

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
    }
}
