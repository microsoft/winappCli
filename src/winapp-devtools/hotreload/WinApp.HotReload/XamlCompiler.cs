// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinApp.HotReload.Protocol;

namespace WinApp.HotReload;

/// <summary>What compiling a save's XAML gave: the generated C# (by path in obj) and the markup for the app, or the error.</summary>
internal sealed record XamlResult(string? Error, IReadOnlyDictionary<string, string> Generated, IReadOnlyList<MarkupPayload> Markup)
{
    public static XamlResult Failed(string error) => new(error, new Dictionary<string, string>(), []);
}

/// <summary>
/// WinUI's own XAML compiler, kept warm in its own process (XamlCompiler.exe --serve, in WinUI packages with hot
/// reload support): the compiler the build runs, compiling only the files a save changed. It reads the inputs the
/// app's build gave it (input.json, next to XamlTypeInfo.g.cs) and writes where the build does, which is also where it
/// keeps what makes each save's code fit the running app (stable connection IDs, removed names kept). Its errors point
/// at the XAML line, and C# errors in its generated code are mapped back to the XAML line that caused them.
/// </summary>
internal sealed class XamlCompiler : IDisposable
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly string _exe;
    private string _inputs;
    private readonly string _projectDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private Task _warm = Task.CompletedTask;
    private int _nextId;

    private XamlCompiler(string exe, string inputs, string projectDirectory, Dictionary<string, string> links)
    {
        _exe = exe;
        _inputs = inputs;
        Intermediate = Path.GetDirectoryName(inputs)!;
        _projectDirectory = projectDirectory;
        _files = links;
    }

    private readonly Dictionary<string, string> _files;

    /// <summary>The app's XAML files, by full path, with the path the app loads them by (relative, '/').</summary>
    public IReadOnlyDictionary<string, string> Files => _files;

    /// <summary>A XAML file the build didn't compile (new since): the service compiles it like the others.</summary>
    public void Add(string file) => _files.TryAdd(file, Path.GetRelativePath(_projectDirectory, file).Replace('\\', '/'));

    /// <summary>
    /// After a project file save brought new references (a package, a project): the service starts again on the
    /// build's inputs with these references, so markup can use their types.
    /// </summary>
    public void UseReferences(IReadOnlyList<string> references)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Intermediate, "input.json")))!.AsObject();
        var built = (json["ReferenceAssemblies"]?.AsArray() ?? []).OfType<System.Text.Json.Nodes.JsonObject>().ToList();
        if (references.Count == 0 || built.Count == 0)
        {
            return;
        }
        var items = new System.Text.Json.Nodes.JsonArray();
        foreach (var path in references.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var item = (built.FirstOrDefault(b => string.Equals((string?)b["FullPath"], path, StringComparison.OrdinalIgnoreCase)) ?? built[0]).DeepClone().AsObject();
            item["ItemSpec"] = path;
            item["FullPath"] = path;
            items.Add(item);
        }
        json["ReferenceAssemblies"] = items;
        _inputs = Path.Combine(Intermediate, "input.hotreload.json");
        File.WriteAllText(_inputs, json.ToJsonString());
        Stop();
        _warm = WarmUpAsync();
    }

    /// <summary>The obj folder the compiler writes to (where XamlTypeInfo.g.cs is).</summary>
    public string Intermediate { get; }

    /// <summary>
    /// The compiler of the app's WinUI package when it can run as a service, started and warming up; null when it
    /// can't (a WinUI without hot reload support), with the reason when it should have.
    /// </summary>
    public static XamlCompiler? Start(string[] compilerArguments, string projectDirectory, out string? unavailable)
    {
        unavailable = null;
        var exe = FindServiceCompiler(compilerArguments);
        if (exe is null)
        {
            return null;
        }
        // A source file argument, not an option naming it (/embed:...\XamlTypeInfo.g.cs).
        var typeInfo = compilerArguments.Select(a => a.Trim('"')).FirstOrDefault(a => !a.StartsWith('/') && !a.StartsWith('-') &&
            a.EndsWith("XamlTypeInfo.g.cs", StringComparison.OrdinalIgnoreCase));
        var inputs = typeInfo is null ? "" : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(projectDirectory, typeInfo)))!, "input.json");
        if (!File.Exists(inputs))
        {
            unavailable = "the XAML compiler's inputs from the app's build (input.json next to XamlTypeInfo.g.cs) weren't found";
            return null;
        }
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var json = JsonDocument.Parse(File.ReadAllText(inputs)))
        {
            foreach (var list in new[] { "XamlApplications", "XamlPages" })
            {
                if (json.RootElement.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray().Where(i => i.TryGetProperty("ItemSpec", out _)))
                    {
                        var spec = item.GetProperty("ItemSpec").GetString()!;
                        var link = item.TryGetProperty("Link", out var l) && l.GetString() is { Length: > 0 } linked ? linked : spec;
                        files[Path.GetFullPath(Path.Combine(projectDirectory, spec))] = link.Replace('\\', '/');
                    }
                }
            }
        }
        var compiler = new XamlCompiler(exe, inputs, projectDirectory, files);
        compiler._warm = compiler.WarmUpAsync();
        return compiler;
    }

    /// <summary>
    /// From the app's compiler references: the package with Microsoft.WinUI.dll ships its XAML compiler in
    /// tools\net472. Only one that runs as a service counts (asked for --serve without arguments, it prints its usage).
    /// </summary>
    private static string? FindServiceCompiler(IEnumerable<string> compilerArguments)
    {
        foreach (var reference in compilerArguments.Where(a => a.StartsWith("/reference:", StringComparison.OrdinalIgnoreCase) &&
            a.Trim('"').EndsWith("Microsoft.WinUI.dll", StringComparison.OrdinalIgnoreCase)))
        {
            for (var directory = Path.GetDirectoryName(reference["/reference:".Length..].Trim('"')); directory is not null; directory = Path.GetDirectoryName(directory))
            {
                var exe = Path.Combine(directory, "tools", "net472", "XamlCompiler.exe");
                if (File.Exists(exe))
                {
                    return Serves(exe) ? exe : null;
                }
            }
        }
        return null;

        static bool Serves(string exe)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(exe, "--serve") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(10_000);
                return (output.Result + error).Contains("--serve", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>A full compile, as the build ran it, while nobody waits: it loads the compiler's references once.</summary>
    private Task WarmUpAsync() => Task.Run(async () =>
    {
        try
        {
            await SendAsync(new ServeRequest { Pass = 1 }, CancellationToken.None).ConfigureAwait(false);
            await SendAsync(new ServeRequest { Pass = 2, RegenerateTypeInfo = true }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // The first save starts the compiler again and reports what went wrong.
            Console.Error.WriteLine($"XAML compiler warm-up failed: {ex.Message}");
        }
    });

    /// <summary>
    /// Compiles <paramref name="xaml"/> (full paths) as a build would: pass 1 (the fields and InitializeComponent the
    /// app's code compiles against), then pass 2 (.g.cs and .xbf) against <paramref name="appImage"/>, the app's types
    /// now, and XamlTypeInfo again when the pages use a type or member it doesn't describe yet.
    /// </summary>
    public async Task<XamlResult> CompileAsync(IReadOnlyList<string> xaml, byte[]? appImage, CancellationToken cancellationToken)
    {
        await _warm.ConfigureAwait(false);
        var files = xaml.ToList();
        var pass1 = await SendAsync(new ServeRequest { Pass = 1, Files = files }, cancellationToken).ConfigureAwait(false);
        if (Failed(pass1) is { } error1)
        {
            return XamlResult.Failed(error1);
        }
        var image = appImage is null ? null : Convert.ToBase64String(appImage);
        var pass2 = await SendAsync(new ServeRequest { Pass = 2, Files = files, LocalAssemblyImage = image }, cancellationToken).ConfigureAwait(false);
        if (Failed(pass2) is { } error2)
        {
            return XamlResult.Failed(error2);
        }
        var typeInfo = pass2.XamlTypeInfoMayBeStale
            ? await SendAsync(new ServeRequest { Pass = 2, Files = files, LocalAssemblyImage = image, RegenerateTypeInfo = true }, cancellationToken).ConfigureAwait(false)
            : null;
        if (typeInfo is not null && Failed(typeInfo) is { } error3)
        {
            return XamlResult.Failed(error3);
        }
        var stems = files.Select(f => Path.ChangeExtension(Path.GetRelativePath(_projectDirectory, f), null)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, text) in new[] { pass1, pass2, typeInfo }.OfType<ServeResponse>().SelectMany(r => r.GeneratedText ?? []))
        {
            var relative = Path.GetRelativePath(Intermediate, path);
            var isTypeInfo = Path.GetFileName(relative).Equals("XamlTypeInfo.g.cs", StringComparison.OrdinalIgnoreCase);
            // The service may return every page's code; the other pages' is what the app already runs.
            if (isTypeInfo ? typeInfo is not null : relative.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) && stems.Contains(Stem(relative)))
            {
                generated[path] = text.TrimStart('\uFEFF');
            }
        }
        var markup = new List<MarkupPayload>();
        foreach (var file in files)
        {
            var xbfPath = Path.ChangeExtension(Path.Combine(Intermediate, Path.GetRelativePath(_projectDirectory, file)), ".xbf");
            var xbf = (pass2.GeneratedXbf ?? []).FirstOrDefault(p => string.Equals(p.Key, xbfPath, StringComparison.OrdinalIgnoreCase)).Value;
            if (xbf is null)
            {
                return XamlResult.Failed($"{Relative(file)}: the XAML compiler produced no .xbf for it");
            }
            var root = XDocument.Parse(await RoslynEngine.ReadSharedAsync(file, cancellationToken).ConfigureAwait(false)).Root!;
            var className = root.Attribute(XName.Get("Class", XamlNamespace))?.Value;
            markup.Add(new MarkupPayload
            {
                Uri = "ms-appx:///" + Files[file],
                Kind = root.Name.LocalName == "Application" ? "application"
                    : className is not null ? "component"
                    : Files[file].EndsWith("Themes/Generic.xaml", StringComparison.OrdinalIgnoreCase) ? "generic"
                    : "dictionary",
                ClassName = className,
                Xbf = Convert.FromBase64String(xbf),
            });
        }
        return new XamlResult(null, generated, markup);
    }

    /// <summary>"Views\ContactsPage" for Views\ContactsPage.g.cs and Views\ContactsPage.i.g.cs.</summary>
    private static string Stem(string relative) =>
        relative.EndsWith(".i.g.cs", StringComparison.OrdinalIgnoreCase) ? relative[..^".i.g.cs".Length]
            : relative.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ? relative[..^".g.i.cs".Length]
            : relative[..^".g.cs".Length];

    /// <summary>
    /// The names a XAML file gives elements (x:Name, Name) outside templates: those the generated code has fields for.
    /// The compiler keeps a field whose name was removed (the running app can't lose a field); code that still uses it
    /// would fail to build, so it fails here too.
    /// </summary>
    public static HashSet<string> Names(XDocument document) =>
        document.Descendants()
            .Where(e => !e.Ancestors().Any(a => a.Name.LocalName.EndsWith("Template", StringComparison.Ordinal)))
            .Select(e => e.Attribute(XName.Get("Name", XamlNamespace))?.Value ?? e.Attribute("Name")?.Value)
            .OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// For an error in the compiler's generated C# (a handler or x:Bind path the code doesn't have yet), the XAML line
    /// that caused it: the generated code marks each element's code with "// Page.xaml line N".
    /// </summary>
    public string? XamlLocation(string generatedPath, int line)
    {
        if (!File.Exists(generatedPath) || XamlLine(File.ReadAllText(generatedPath), line) is not { } xamlLine)
        {
            return null;
        }
        var stem = Stem(Path.GetRelativePath(Intermediate, generatedPath));
        var xaml = Files.Keys.FirstOrDefault(f => Path.ChangeExtension(Path.GetRelativePath(_projectDirectory, f), null).Equals(stem, StringComparison.OrdinalIgnoreCase));
        return xaml is null ? null : $"{Relative(xaml)}({xamlLine})";
    }

    /// <summary>The XAML line the generated code at <paramref name="line"/> (1-based) was made from.</summary>
    internal static int? XamlLine(string generated, int line)
    {
        var lines = generated.Split('\n');
        for (var i = Math.Min(line, lines.Length) - 1; i >= 0; i--)
        {
            if (Regex.Match(lines[i], @"//\s*[^\s/]+\.xaml line (?<line>\d+)") is { Success: true } match)
            {
                return int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return null;
    }

    /// <summary>Stops the service: a build must not run while it holds the compiler's state in obj. The next compile starts it again.</summary>
    public void Stop()
    {
        _gate.Wait();
        try
        {
            StopProcess();
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? Failed(ServeResponse response) =>
        response.Success && !(response.Diagnostics ?? []).Any(d => d.IsError) ? null
            : (response.Diagnostics ?? []).Any(d => d.IsError)
                ? string.Join(Environment.NewLine + "  ", response.Diagnostics!.Where(d => d.IsError).Take(3).Select(Format))
                : "the XAML compiler failed without saying why";

    private string Format(ServeDiagnostic d) =>
        $"{(d.File is { Length: > 0 } file ? Relative(Path.GetFullPath(Path.Combine(_projectDirectory, file))) : "XAML")}" +
        $"{(d.Line > 0 ? $"({d.Line},{d.Column})" : "")}: {d.Code}: {d.Message}";

    private string Relative(string path) => Path.GetRelativePath(_projectDirectory, path);

    private async Task<ServeResponse> SendAsync(ServeRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = _process is { HasExited: false } running ? running : StartProcess();
            request.Id = ++_nextId;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"the XAML compiler stopped (exit code {(process.HasExited ? process.ExitCode : 0)})");
                // The compiler may print other lines; responses are the JSON objects with this request's ID.
                if (line.StartsWith('{') && JsonSerializer.Deserialize<ServeResponse>(line) is { } response && response.Id == request.Id)
                {
                    return response;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Process StartProcess()
    {
        StopProcess();
        var start = new ProcessStartInfo(_exe)
        {
            WorkingDirectory = _projectDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add("--serve");
        start.ArgumentList.Add(_inputs);
        _process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {_exe}");
        _process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) { Console.Error.WriteLine($"XamlCompiler: {line}"); } };
        _process.BeginErrorReadLine();
        return _process;
    }

    private void StopProcess()
    {
        if (_process is not { } process)
        {
            return;
        }
        _process = null;
        try
        {
            if (!process.HasExited)
            {
                // An empty line asks the service to exit.
                process.StandardInput.WriteLine();
                if (!process.WaitForExit(2000))
                {
                    process.Kill();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }
        process.Dispose();
    }

    public void Dispose()
    {
        StopProcess();
        _gate.Dispose();
    }

    private sealed class ServeRequest
    {
        public int Id { get; set; }
        public int Pass { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Files { get; set; }
        public bool RegenerateTypeInfo { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LocalAssemblyImage { get; set; }
    }

    private sealed class ServeDiagnostic
    {
        public bool IsError { get; set; }
        public string? Code { get; set; }
        public string? File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string? Message { get; set; }
    }

    private sealed class ServeResponse
    {
        public int Id { get; set; }
        public bool Success { get; set; }
        public bool XamlTypeInfoMayBeStale { get; set; }
        public Dictionary<string, string>? GeneratedText { get; set; }
        public Dictionary<string, string>? GeneratedXbf { get; set; }
        public List<ServeDiagnostic>? Diagnostics { get; set; }
    }
}
