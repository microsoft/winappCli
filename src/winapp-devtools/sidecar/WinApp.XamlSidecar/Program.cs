// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace WinApp.XamlSidecar;

/// <summary>
/// SPIKE (hot reload option A): runs next to 'dotnet watch' under 'winapp run --watch' for XAML apps. On a .xaml save it
/// runs the WinUI XAML compiler (kept warm), sends the .xbf to the in-app agent, and writes the generated C# into obj for
/// dotnet watch to apply; the agent rebuilds the page once the update landed. It prints one [watch] line per save.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? throw new ArgumentException($"{name} is required");
        var project = Path.GetFullPath(Arg("--project"));
        RegisterSdkRoslyn(Path.GetDirectoryName(project)!);
        return Run(project, Arg("--pipe"), Arg("--rsp"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string project, string pipe, string rsp) => new Sidecar(project, pipe, rsp).RunAsync().GetAwaiter().GetResult();

    /// <summary>Roslyn (public API) from the SDK that builds the app, so the app's source generators load as they do in its build.</summary>
    private static void RegisterSdkRoslyn(string projectDirectory)
    {
        var dotnet = Environment.ProcessPath!;
        var start = new ProcessStartInfo(dotnet, "--version") { WorkingDirectory = projectDirectory, RedirectStandardOutput = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        var version = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        var bincore = Path.Combine(Path.GetDirectoryName(dotnet)!, "sdk", version, "Roslyn", "bincore");
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(bincore, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }
}

internal sealed class Sidecar(string project, string pipeName, string rspArgument)
{
    private string rspPath = rspArgument;

    // "auto": the newest compiler-argument file a build recorded under obj.
    private string FindRsp() => rspArgument != "auto" ? Path.GetFullPath(rspArgument)
        : Directory.Exists(Path.Combine(_projectDirectory, "obj"))
            ? Directory.EnumerateFiles(Path.Combine(_projectDirectory, "obj"), "winapp-csc.rsp", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? ""
            : "";

    private readonly string _projectDirectory = Path.GetDirectoryName(project)!;
    private readonly string? _log = Environment.GetEnvironmentVariable("WINAPP_XAML_SIDECAR_LOG");
    private readonly ConcurrentDictionary<string, DateTime> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _replies = new();
    private readonly object _writeLock = new();
    private XamlCompilerHost? _xaml;
    private Task _ready = Task.CompletedTask;
    private string _intermediate = "";
    private readonly HashSet<string> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private Version? _localVersion;
    private AppImage? _image;
    private byte[]? _imageBytes;
    private bool _imageStale;
    private StreamWriter? _agent;
    private bool _agentXaml;
    private int _hellos;
    private int _nextId;
    private string? _unavailable;

    // C# saves: reported when the agent says an update landed.
    private DateTime? _csSaved;
    private Timer? _csTimer;
    private volatile TaskCompletionSource<string[]>? _nextUpdate;
    private volatile bool _xamlInFlight;
    private DateTime _lastUpdate;
    private volatile bool _restartAnnounced;

    private void Log(string text)
    {
        if (_log is not null)
        {
            lock (_log)
            {
                File.AppendAllText(_log, $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
            }
        }
    }

    private static void Say(string line)
    {
        lock (typeof(Sidecar))
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }

    public async Task<int> RunAsync()
    {
        _ = Task.Run(ServeAgentsAsync);
        await StartCompilerAsync(firstTime: true).ConfigureAwait(false);
        if (Environment.GetEnvironmentVariable("WINAPP_XAML_SIDECAR_SELFTEST") is { Length: > 0 } selfTest)
        {
            // Offline check of the compile path (no app): compile one saved XAML file and report what would be written.
            await _ready.ConfigureAwait(false);
            for (var round = 0; round < 2; round++)
            {
                var clock = Stopwatch.StartNew();
                var file = Path.GetFullPath(selfTest);
                var p1 = await _xaml!.Pass1Async([file], CancellationToken.None).ConfigureAwait(false);
                var t1 = clock.ElapsedMilliseconds;
                var stems = new[] { Path.ChangeExtension(Relative(file), null) };
                var c1 = GeneratedFiles.Changes(p1.GeneratedText, _xaml.OutputDirectory, _intermediate, stems, false);
                var img = _image!.Emit(c1.ToDictionary(x => x.ObjPath, x => x.Text, StringComparer.OrdinalIgnoreCase), out var ie);
                var t2 = clock.ElapsedMilliseconds;
                var p2 = await _xaml.Pass2Async([file], img, false, CancellationToken.None).ConfigureAwait(false);
                var all = p1.GeneratedText.Concat(p2.GeneratedText).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Last().Value);
                var ch = GeneratedFiles.Changes(all, _xaml.OutputDirectory, _intermediate, stems, false);
                Say($"selftest round {round}: pass1 {t1} ms ({Failed(p1) ?? "ok"}), image {t2 - t1} ms [{_image.LastTiming}] ({ie ?? img.Length + " bytes"}), pass2 {clock.ElapsedMilliseconds - t2} ms ({Failed(p2) ?? "ok"}), xbf {p2.GeneratedXbf.Count}, changes: {string.Join(", ", ch.Select(x => Path.GetFileName(x.ObjPath) + (x.CodeChanged ? " [code]" : " [checksum]")))}");
            }
            return 0;
        }
        using var watcher = new FileSystemWatcher(_projectDirectory) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        void Changed(string path)
        {
            if ((path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) &&
                !path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) && !path.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            {
                _changed.TryAdd(path, DateTime.UtcNow);
            }
        }
        watcher.Changed += (_, e) => Changed(e.FullPath);
        watcher.Created += (_, e) => Changed(e.FullPath);
        watcher.Deleted += (_, e) => Changed(e.FullPath);
        watcher.Renamed += (_, e) => { Changed(e.OldFullPath); Changed(e.FullPath); };
        watcher.EnableRaisingEvents = true;
        while (true)
        {
            await Task.Delay(50).ConfigureAwait(false);
            if (_changed.IsEmpty || DateTime.UtcNow - _changed.Values.Max() < TimeSpan.FromMilliseconds(150))
            {
                continue;
            }
            var batch = _changed.ToArray();
            foreach (var (key, _) in batch)
            {
                _changed.TryRemove(key, out _);
            }
            var started = batch.Min(b => b.Value);
            try
            {
                await ProcessAsync(batch.Select(b => b.Key).ToList(), started).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Say($"[watch] error the XAML side-car failed: {ex.GetBaseException().Message} ({Ms(started)} ms)");
                Log(ex.ToString());
            }
        }
    }

    private static long Ms(DateTime started) => (long)(DateTime.UtcNow - started).TotalMilliseconds;

    private string Relative(string path) => Path.GetRelativePath(_projectDirectory, path);

    // ---- the XAML compiler ----

    private async Task StartCompilerAsync(bool firstTime)
    {
        // dotnet watch's build records the compiler arguments (rsp) and the XAML compiler's inputs (input.json).
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (!File.Exists(rspPath = FindRsp()) || !File.ReadLines(rspPath).Any(l => l.EndsWith("XamlTypeInfo.g.cs", StringComparison.OrdinalIgnoreCase)))
        {
            if (DateTime.UtcNow > deadline)
            {
                _unavailable = "the build didn't record its compiler arguments";
                return;
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
        var args = File.ReadAllLines(rspPath);
        var typeInfo = args.First(a => !a.StartsWith('/') && a.EndsWith("XamlTypeInfo.g.cs", StringComparison.OrdinalIgnoreCase));
        _intermediate = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(_projectDirectory, typeInfo)))!;
        var inputJson = Path.Combine(_intermediate, "input.json");
        var exe = XamlCompilerHost.FindAppPackageCompiler(args);
        if (exe is null || !File.Exists(inputJson))
        {
            _unavailable = exe is null ? "this WinUI package's XAML compiler has no --serve" : "the XAML compiler's inputs (input.json) weren't found";
            return;
        }
        using (var json = JsonDocument.Parse(File.ReadAllText(inputJson)))
        {
            _inputs.Clear();
            foreach (var list in new[] { "XamlApplications", "XamlPages" })
            {
                if (json.RootElement.TryGetProperty(list, out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        _inputs.Add(Path.GetFullPath(Path.Combine(_projectDirectory, item.GetProperty("ItemSpec").GetString()!)));
                    }
                }
            }
            if (json.RootElement.TryGetProperty("LocalAssembly", out var local) &&
                (local.ValueKind == JsonValueKind.Array ? local.EnumerateArray().FirstOrDefault() : local) is { ValueKind: JsonValueKind.Object } localItem &&
                localItem.TryGetProperty("FullPath", out var localPath) && localPath.GetString() is { } localFile && File.Exists(localFile))
            {
                _localVersion = System.Reflection.AssemblyName.GetAssemblyName(localFile).Version;
            }
        }
        _xaml?.Dispose();
        var output = Path.Combine(Path.GetTempPath(), "winapp-xaml-sidecar", $"{Environment.ProcessId}-{DateTime.UtcNow.Ticks}");
        _xaml = XamlCompilerHost.Start(exe, inputJson, output, _intermediate, _projectDirectory);
        _image = AppImage.Load(rspPath, _projectDirectory, _localVersion);
        _imageStale = true;
        var host = _xaml;
        var clock = Stopwatch.StartNew();
        _ready = Task.Run(async () =>
        {
            await host.Pass1Async(null, CancellationToken.None).ConfigureAwait(false);
            await host.Pass2Async(null, null, typeInfo: true, CancellationToken.None).ConfigureAwait(false);
            Log($"compiler warm in {clock.ElapsedMilliseconds} ms ({exe})");
            // The first image is slow (source generators load): built now, so the first C# + XAML save doesn't pay it.
            var warm = Stopwatch.StartNew();
            _image.Emit(new Dictionary<string, string>(), out var warmError);
            Log($"app image warm in {warm.ElapsedMilliseconds} ms{(warmError is null ? "" : ": " + warmError)}");
        });
    }

    // ---- one save ----

    private async Task ProcessAsync(List<string> files, DateTime started)
    {
        var xaml = files.Where(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)).ToList();
        var cs = files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
        Log($"batch: {string.Join(", ", files.Select(Relative))}");
        if (cs.Count > 0)
        {
            _imageStale = true;
        }
        if (xaml.Count == 0)
        {
            WaitForCSharp(started);
            return;
        }
        if (_agent is null || !_agentXaml)
        {
            Say($"[watch] restart {Relative(xaml[0])}: XAML hot reload isn't available ({_unavailable ?? "the app isn't connected"}); it takes effect when the app restarts ({Ms(started)} ms)");
            return;
        }
        _xamlInFlight = true;
        try
        {
            await ProcessXamlAsync(xaml.Where(File.Exists).ToList(), xaml.Where(f => !File.Exists(f)).ToList(), cs.Count > 0, started).ConfigureAwait(false);
        }
        finally
        {
            _xamlInFlight = false;
        }
    }

    private void WaitForCSharp(DateTime started)
    {
        if (_lastUpdate >= started)
        {
            // dotnet watch applied it before this save settled here.
            Say($"[watch] applied C# through dotnet watch ({Ms(started)} ms)");
            return;
        }
        _csSaved = started;
        _csTimer?.Dispose();
        _csTimer = new Timer(_ =>
        {
            if (_csSaved == started)
            {
                _csSaved = null;
                Say($"[watch] unchanged dotnet watch applied no update for this save within 30 s (see its output above) ({Ms(started)} ms)");
            }
        }, null, 30_000, Timeout.Infinite);
    }

    private async Task ProcessXamlAsync(List<string> xaml, List<string> deleted, bool codeSaved, DateTime started)
    {
        await _ready.ConfigureAwait(false);
        if (deleted.Count > 0)
        {
            Log($"deleted: {string.Join(", ", deleted.Select(Relative))}");
            if (await UpdateInputsAsync([]).ConfigureAwait(false) is { } removeError)
            {
                Log(removeError);
            }
        }
        if (xaml.Count == 0)
        {
            // A deleted page: dotnet watch applies removing its class; its compiled markup stays with the running app.
            Say($"[watch] applied {Relative(deleted[0])} deleted ({Ms(started)} ms)");
            return;
        }
        foreach (var path in xaml)
        {
            try
            {
                XDocument.Parse(GeneratedFiles.ReadShared(path));
            }
            catch (System.Xml.XmlException ex)
            {
                Say($"[watch] error {Relative(path)}({ex.LineNumber},{ex.LinePosition}): {ex.Message.Split(" Line ")[0]} ({Ms(started)} ms)");
                return;
            }
        }
        var added = xaml.Where(p => !_inputs.Contains(p)).ToList();
        if (added.Count > 0 && await UpdateInputsAsync(added).ConfigureAwait(false) is { } addError)
        {
            Say($"[watch] error {addError} ({Ms(started)} ms)");
            return;
        }
        var clock = Stopwatch.StartNew();
        var pass1 = await _xaml!.Pass1Async(xaml, CancellationToken.None).ConfigureAwait(false);
        if (pass1.Diagnostics.Any(d => d.Code == "WMC9999"))
        {
            // The compiler's state file in obj was briefly locked (seen once under dotnet watch): tried again.
            Log("WMC9999 on pass 1; retrying");
            await Task.Delay(200).ConfigureAwait(false);
            pass1 = await _xaml.Pass1Async(xaml, CancellationToken.None).ConfigureAwait(false);
        }
        if (Failed(pass1) is { } error1)
        {
            Say($"[watch] error {error1} ({Ms(started)} ms)");
            return;
        }
        var stems = xaml.Select(p => Path.ChangeExtension(Relative(p), null)).ToList();
        var pass1Changes = GeneratedFiles.Changes(pass1.GeneratedText, _xaml.OutputDirectory, _intermediate, stems, typeInfo: false);
        var pass1Ms = clock.ElapsedMilliseconds;
        if (_imageStale || added.Count > 0)
        {
            // As hr-latest: only C# saved since the last image makes it stale. A new x:Name field from pass 1 isn't needed
            // to check x:Bind paths to the app's own members.
            var overrides = pass1Changes.ToDictionary(c => c.ObjPath, c => c.Text, StringComparer.OrdinalIgnoreCase);
            var image = _image!.Emit(overrides, out var imageError);
            if (imageError is not null)
            {
                Say($"[watch] error {imageError} ({Ms(started)} ms)");
                return;
            }
            _imageBytes = image;
            _imageStale = false;
        }
        var imageMs = clock.ElapsedMilliseconds - pass1Ms;
        var pass2 = await _xaml.Pass2Async(xaml, _imageBytes, typeInfo: added.Count > 0, CancellationToken.None).ConfigureAwait(false);
        if (pass2.Diagnostics.Any(d => d.Code == "WMC9999"))
        {
            Log("WMC9999 on pass 2; retrying");
            await Task.Delay(200).ConfigureAwait(false);
            pass2 = await _xaml.Pass2Async(xaml, _imageBytes, typeInfo: added.Count > 0, CancellationToken.None).ConfigureAwait(false);
        }
        if (Failed(pass2) is { } error2)
        {
            Say($"[watch] error {error2} ({Ms(started)} ms)");
            return;
        }
        var typeInfo = added.Count > 0;
        if (pass2.XamlTypeInfoMayBeStale && !typeInfo)
        {
            pass2 = await _xaml.Pass2Async(xaml, _imageBytes, typeInfo: true, CancellationToken.None).ConfigureAwait(false);
            if (Failed(pass2) is { } error3)
            {
                Say($"[watch] error {error3} ({Ms(started)} ms)");
                return;
            }
            typeInfo = true;
        }
        var compileMs = clock.ElapsedMilliseconds;
        var generated = pass1.GeneratedText.Concat(pass2.GeneratedText).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
        var changes = GeneratedFiles.Changes(generated, _xaml.OutputDirectory, _intermediate, stems, typeInfo);
        var expectUpdate = changes.Any(c => c.CodeChanged);

        var items = new JsonArray();
        foreach (var path in xaml)
        {
            var xbfPath = Path.ChangeExtension(Path.Combine(_xaml.OutputDirectory, Relative(path)), ".xbf");
            if (!pass2.GeneratedXbf.TryGetValue(xbfPath, out var xbf))
            {
                Say($"[watch] restart {Relative(path)}: the XAML compiler produced no .xbf for it; it takes effect when the app restarts ({Ms(started)} ms)");
                return;
            }
            var root = XDocument.Parse(GeneratedFiles.ReadShared(path)).Root!;
            var cls = root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value;
            var kind = root.Name.LocalName == "Application" ? "application"
                : cls is not null ? "component"
                : Relative(path).EndsWith(@"Themes\Generic.xaml", StringComparison.OrdinalIgnoreCase) ? "generic" : "dictionary";
            items.Add(new JsonObject { ["uri"] = "ms-appx:///" + Relative(path).Replace('\\', '/'), ["kind"] = kind, ["class"] = cls, ["xbf"] = Convert.ToBase64String(xbf) });
        }
        var id = Interlocked.Increment(ref _nextId);
        var applied = Reply(id + 100_000);
        Log($"markup sending at {Ms(started)} ms");
        await Request(new JsonObject { ["op"] = "markup", ["id"] = id, ["items"] = items, ["expectUpdate"] = expectUpdate }, id).ConfigureAwait(false);
        Log($"markup set at {Ms(started)} ms");
        var writeMs = 0L;
        if (expectUpdate)
        {
            Log($"writing at {Ms(started)} ms");
            GeneratedFiles.Write(changes);
            if (added.Count > 0)
            {
                // E9b: dotnet watch reloaded the project when the new .xaml appeared, before these generated files existed,
                // and it doesn't watch obj for new files. Touching the project makes it load the project (and them) again.
                File.SetLastWriteTimeUtc(project, DateTime.UtcNow);
                Log($"touched {Path.GetFileName(project)} for new generated files");
            }
            writeMs = Ms(started);
            // dotnet watch may apply the files (and the developer's C# of the same save, if it hasn't yet) as one update or several.
            var expected = changes.Count(c => c.CodeChanged) + (codeSaved && _lastUpdate < started ? 1 : 0);
            Send(new JsonObject { ["op"] = "written", ["id"] = id, ["expectedUpdates"] = expected });
        }
        if (await Task.WhenAny(applied, Task.Delay(45_000)).ConfigureAwait(false) != applied)
        {
            Say($"[watch] error {Relative(xaml[0])}: the app didn't confirm the reload within 45 s ({Ms(started)} ms)");
            return;
        }
        if (applied.IsFaulted)
        {
            // dotnet watch stopped the app: the generated C# was a change it can't apply ("Restart is needed" above).
            _restartAnnounced = true;
            Say($"[watch] restart {Relative(xaml[0])}: dotnet watch restarts the app for this change (its reason is above) ({Ms(started)} ms)");
            return;
        }
        var result = applied.Result;
        var errors = result["errors"]!.AsArray().Select(e => (string)e!).ToList();
        var notes = result["notes"]!.AsArray().Select(e => (string)e!).ToList();
        Log($"timing: pass1 {pass1Ms} ms, image {imageMs} ms, pass2 {compileMs - pass1Ms - imageMs} ms, written at {writeMs} ms, trigger {result["trigger"]}, rebuild {result["rebuildMs"]} ms, total {Ms(started)} ms; generated changed: {string.Join(", ", changes.Select(c => Path.GetFileName(c.ObjPath) + (c.CodeChanged ? "" : " (checksum only, held back)")))}");
        if (errors.Count > 0)
        {
            Say($"[watch] error {errors[0]} ({Ms(started)} ms)");
            foreach (var more in errors.Skip(1))
            {
                Say($"  {more}");
            }
            return;
        }
        Say($"[watch] applied {string.Join(", ", xaml.Select(Relative))}{(expectUpdate ? " (with generated C# through dotnet watch)" : "")} ({Ms(started)} ms)");
        foreach (var note in notes)
        {
            Say($"  note: {note}");
        }
    }

    private string? Failed(XamlCompileOutput output) =>
        output.Success && !output.Diagnostics.Any(d => d.IsError) ? null
            : output.Diagnostics.Any(d => d.IsError)
                ? string.Join(Environment.NewLine + "  ", output.Diagnostics.Where(d => d.IsError).Take(3).Select(d =>
                    $"{(d.File is { Length: > 0 } f ? Relative(Path.GetFullPath(Path.Combine(_projectDirectory, f))) : "")}{(d.Line > 0 ? $"({d.Line},{d.Column})" : "")}: {d.Code}: {d.Message}"))
                : "the XAML compiler failed without saying why";

    /// <summary>
    /// New or deleted pages: the compiler takes a copy of the build's inputs that lists the pages on disk now (API v3 reload).
    /// </summary>
    private async Task<string?> UpdateInputsAsync(List<string> added)
    {
        var inputJson = Path.Combine(_intermediate, "input.json");
        var json = JsonNode.Parse(File.ReadAllText(inputJson))!.AsObject();
        var pages = json["XamlPages"]!.AsArray();
        var template = pages.FirstOrDefault()?.DeepClone().AsObject() ?? [];
        // Deleted pages leave the compiler's inputs (their generated code stays in obj, so the running app keeps them).
        foreach (var gone in pages.Where(p => !File.Exists(Path.GetFullPath(Path.Combine(_projectDirectory, (string)p!["ItemSpec"]!)))).ToList())
        {
            _inputs.Remove(Path.GetFullPath(Path.Combine(_projectDirectory, (string)gone!["ItemSpec"]!)));
            pages.Remove(gone);
        }
        foreach (var path in added.Concat(_inputs.Where(File.Exists).Where(p => !pages.Any(q => Path.GetFullPath(Path.Combine(_projectDirectory, (string)q!["ItemSpec"]!)).Equals(p, StringComparison.OrdinalIgnoreCase)) && !p.EndsWith("App.xaml", StringComparison.OrdinalIgnoreCase))).Distinct().ToList())
        {
            var item = template.DeepClone().AsObject();
            item["ItemSpec"] = Relative(path);
            if (item.ContainsKey("FullPath"))
            {
                item["FullPath"] = path;
            }
            foreach (var key in new[] { "Link", "TargetPath", "RelativeDir", "Filename", "Identity" })
            {
                item.Remove(key);
            }
            pages.Add(item);
        }
        var current = Path.Combine(_xaml!.OutputDirectory, $"input-{DateTime.UtcNow.Ticks}.json");
        File.WriteAllText(current, json.ToJsonString());
        var reload = await _xaml.ReloadAsync(current, CancellationToken.None).ConfigureAwait(false);
        if (reload is null || !reload.Success)
        {
            return $"{(added.Count > 0 ? Relative(added[0]) : "pages")}: the XAML compiler couldn't take the changed page list: {string.Join("; ", reload?.Diagnostics.Select(d => d.Message) ?? ["no reload support"])}";
        }
        foreach (var path in added)
        {
            _inputs.Add(path);
        }
        return null;
    }

    // ---- the in-app agent ----

    private async Task ServeAgentsAsync()
    {
        while (true)
        {
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync().ConfigureAwait(false);
            _ = Task.Run(() => ReadAgentAsync(pipe));
        }
    }

    private async Task ReadAgentAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var message = JsonNode.Parse(line)!.AsObject();
                switch ((string?)message["op"])
                {
                    case "hello":
                        await OnHelloAsync(message, writer).ConfigureAwait(false);
                        break;
                    case "markupSet":
                        _replies.TryRemove((int)message["id"]!, out var set);
                        set?.TrySetResult(message);
                        break;
                    case "applied":
                        _replies.TryRemove((int)message["id"]! + 100_000, out var done);
                        done?.TrySetResult(message);
                        break;
                    case "updated":
                        var types = message["types"]!.AsArray().Select(t => (string)t!).ToArray();
                        _lastUpdate = DateTime.UtcNow;
                        Log($"update landed: {string.Join(", ", types)}");
                        Interlocked.Exchange(ref _nextUpdate, null)?.TrySetResult(types);
                        if (!_xamlInFlight && _csSaved is { } saved)
                        {
                            _csSaved = null;
                            _csTimer?.Dispose();
                            Say($"[watch] applied C# through dotnet watch ({string.Join(", ", types.Select(t => t[(t.LastIndexOf('.') + 1)..]).Distinct().Take(4))}) ({Ms(saved)} ms)");
                        }
                        break;
                }
            }
            // The app went away (a restart, or a crash): saves wait for the next app rather than writing into a closed pipe.
            lock (_writeLock)
            {
                if (ReferenceEquals(_agent, writer))
                {
                    _agent = null;
                    // dotnet watch is about to rebuild: its XAML compiler replaces the hot reload state files in obj, which
                    // the warm compiler service holds (WMC9999 "Unable to remove the file to be replaced"). Started again
                    // when the new app connects.
                    _xaml?.Kill();
                    _xaml = null;
                    Log("app disconnected; compiler service stopped for the rebuild");
                    var hellos = _hellos;
                    _ = Task.Delay(20_000).ContinueWith(_ =>
                    {
                        if (_hellos == hellos && _agent is null)
                        {
                            // The rebuild failed (seen: WMC9999 in the build's XAML compiler, a state file in obj still held). A
                            // project change makes dotnet watch build again; once, so a real compile error isn't retried forever.
                            Log("no app 20 s after it stopped; touching the project so dotnet watch builds again");
                            File.SetLastWriteTimeUtc(project, DateTime.UtcNow);
                        }
                    }, TaskScheduler.Default);
                }
            }
            _csSaved = null;
            _csTimer?.Dispose();
            if (_replies.IsEmpty)
            {
                // Said now, before dotnet watch rebuilds: the save that caused it (a rude C# edit) or a crash gets its outcome.
                // A XAML save in flight says it itself.
                Say("[watch] restart the app stopped; dotnet watch rebuilds and restarts it (its reason is above)");
            }
            foreach (var pending in _replies.Values)
            {
                pending.TrySetException(new IOException("the app disconnected"));
            }
        }
    }

    private async Task OnHelloAsync(JsonObject message, StreamWriter writer)
    {
        var restarted = Interlocked.Increment(ref _hellos) > 1;
        _csSaved = null;
        _csTimer?.Dispose();
        lock (_writeLock)
        {
            _agent = writer;
            _agentXaml = (bool)message["xaml"]!;
        }
        if (restarted)
        {
            // A new app process: dotnet watch rebuilt it (a rude edit, or a relaunch), so obj holds a fresh build's output.
            await StartCompilerAsync(firstTime: false).ConfigureAwait(false);
        }
        Say($"[watch] ready (PID {message["pid"]}){(_agentXaml ? ", XAML hot reload on" : "")}");
        if (!_agentXaml)
        {
            Say($"  note: XAML changes take effect when the app restarts: {(string?)message["unavailable"]}");
        }
    }

    private Task<JsonObject> Reply(int id)
    {
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _replies[id] = reply;
        return reply.Task;
    }

    private async Task<JsonObject> Request(JsonObject message, int id)
    {
        var reply = Reply(id);
        Send(message);
        if (await Task.WhenAny(reply, Task.Delay(15_000)).ConfigureAwait(false) != reply)
        {
            throw new TimeoutException("the app didn't take the markup within 15 s");
        }
        return reply.Result;
    }

    private void Send(JsonObject message)
    {
        lock (_writeLock)
        {
            _agent?.WriteLine(message.ToJsonString());
        }
    }
}
