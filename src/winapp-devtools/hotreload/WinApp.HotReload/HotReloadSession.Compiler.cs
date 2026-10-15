// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using WinApp.HotReload.Protocol;

namespace WinApp.HotReload;

/// <summary>
/// The "compiler" pipeline (WINAPP_HOT_RELOAD_PIPELINE=compiler): a saved XAML file is compiled by the WinUI XAML compiler
/// itself, kept warm in its own process. Its generated C# replaces the build's in the workspace and goes to the app as a
/// .NET hot reload delta like any code change; its .xbf goes to WinUI, and InitializeComponent runs again, so the app
/// runs exactly the code and markup a build of the saved files would produce. Nothing is rewritten.
/// </summary>
internal sealed partial class HotReloadSession
{
    /// <summary>"rewrite" (the XAML is rewritten and wired by winapp) or "compiler".</summary>
    public static string PipelineName { get; } = (Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_PIPELINE") is { Length: > 0 } pipeline ? pipeline : "rewrite").ToLowerInvariant();

    /// <summary>How live pages show a save on the compiler pipeline: inplace (the default), recreate or shadow.</summary>
    public static string PageUpdateName { get; } = (Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_PAGE_UPDATE") ?? "").ToLowerInvariant();

    public static bool UseCompiler => PipelineName == "compiler";

    private XamlCompilerHost? _xaml;
    private Task _xamlReady = Task.CompletedTask;

    /// <summary>Why XAML saves restart the app on this pipeline (no compiler service, no compiler inputs); null when they don't.</summary>
    private string? _xamlUnavailable;

    /// <summary>The XAML compiler's output folder in obj (where the build put XamlTypeInfo.g.cs and the .g.cs files).</summary>
    private string _xamlIntermediate = "";

    /// <summary>The XAML files the compiler was given by the build, by full path.</summary>
    private HashSet<string> _xamlInputs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>App.xaml's x:Key values, as the app has them (launched with, or last reloaded).</summary>
    private readonly Dictionary<string, HashSet<string>> _applicationKeys = new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> Keys(XElement root) =>
        root.DescendantsAndSelf().Select(e => e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>The app's assembly (metadata only) for pass 2, when its C# changed since the build; null reads the build's.</summary>
    private byte[]? _xamlImage;
    private bool _xamlImageStale;

    /// <summary>The compiler started again (new references) and hasn't compiled every page yet.</summary>
    private bool _xamlFullCompilePending;

    /// <summary>The XAML compiler's C#: Page.g.cs (pass 2) and the pass 1 part, Page.i.g.cs (hot reload builds) or Page.g.i.cs.</summary>
    private static bool IsGenerated(string path) =>
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsPass1Part(string path) =>
        path.EndsWith(".i.g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Pages\Foo" for Pages\Foo.g.cs, Pages\Foo.i.g.cs and Pages\Foo.g.i.cs.</summary>
    private static string GeneratedStem(string relative) =>
        IsPass1Part(relative) ? relative[..^".i.g.cs".Length] : relative[..^".g.cs".Length];

    /// <summary>The documents' text as the running binary was built from, pinned: a build may overwrite the files in obj.</summary>
    private static DocumentInfo Pinned(DocumentInfo document)
    {
        if (document.FilePath is not { } path || !IsGenerated(path) || !File.Exists(path))
        {
            return document;
        }
        using var stream = File.OpenRead(path);
        var text = SourceText.From(stream, checksumAlgorithm: SourceHashAlgorithm.Sha256);
        return document.WithTextLoader(TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), path)));
    }

    /// <summary>The XAML compiler, started before the session exists (see CreateAsync).</summary>
    private sealed class XamlStart
    {
        public XamlCompilerHost? Host;
        public string Intermediate = "";
        public string? Unavailable;
        public HashSet<string> Inputs = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<string>> ApplicationKeys = new(StringComparer.OrdinalIgnoreCase);
        public Task<(XamlCompileOutput Pass1, XamlCompileOutput Pass2)?> Warm = Task.FromResult<(XamlCompileOutput, XamlCompileOutput)?>(null);
    }

    /// <summary>
    /// Starts the XAML compiler on the inputs the app's build gave it (input.json, next to XamlTypeInfo.g.cs) and warms it
    /// up in the background with a full compile, as the build ran it: that loads the compiler's references once and
    /// records which types XamlTypeInfo has, so a later page compile can tell when it needs regenerating.
    /// </summary>
    private static XamlStart StartXamlCompiler(string[] cscArgs, string projectDirectory, string markupDirectory)
    {
        var start = new XamlStart();
        var typeInfo = cscArgs.Select(a => a.Trim('"')).FirstOrDefault(a => a.EndsWith("XamlTypeInfo.g.cs", StringComparison.OrdinalIgnoreCase));
        start.Intermediate = typeInfo is null ? "" : Path.GetDirectoryName(Path.GetFullPath(Path.Combine(projectDirectory, typeInfo)))!;
        var inputs = start.Intermediate.Length > 0 ? Path.Combine(start.Intermediate, "input.json") : "";
        if (!File.Exists(inputs))
        {
            start.Unavailable = "the XAML compiler's inputs from the app's build (input.json next to XamlTypeInfo.g.cs) weren't found";
            return start;
        }
        if (XamlCompilerHost.Locate() is not { } exe)
        {
            start.Unavailable = $"the XAML compiler service wasn't found (set {XamlCompilerHost.LocationVariable} to a folder with XamlCompiler.exe that supports --serve)";
            return start;
        }
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(inputs));
            foreach (var list in new[] { "XamlApplications", "XamlPages" })
            {
                if (json.RootElement.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("ItemSpec", out var spec) && spec.GetString() is { } file)
                        {
                            var full = Path.GetFullPath(Path.Combine(projectDirectory, file));
                            start.Inputs.Add(full);
                            if (list == "XamlApplications" && File.Exists(full))
                            {
                                start.ApplicationKeys[full] = Keys(XDocument.Load(full).Root!);
                            }
                        }
                    }
                }
            }
            // Where the build writes: the compiler's hot reload state (connection ID ledger) lives there.
            start.Host = XamlCompilerHost.Start(exe, inputs, null, start.Intermediate, projectDirectory);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Xml.XmlException)
        {
            start.Unavailable = "the XAML compiler service couldn't start: " + ex.Message;
            return start;
        }
        var host = start.Host;
        start.Warm = Task.Run<(XamlCompileOutput, XamlCompileOutput)?>(async () =>
        {
            var pass1 = await host.Pass1Async(null, CancellationToken.None).ConfigureAwait(false);
            var pass2 = await host.Pass2Async(null, null, typeInfo: true, CancellationToken.None).ConfigureAwait(false);
            return (pass1, pass2);
        });
        return start;
    }

    /// <summary>
    /// After a project file change brought new references (a package, a project): the compiler loads its references once,
    /// so it starts again with the build's inputs and the workspace's references, and warms up again.
    /// </summary>
    private void RestartXamlCompilerWithCurrentReferences()
    {
        if (_xaml is null || _xamlIntermediate.Length == 0)
        {
            return;
        }
        var inputs = Path.Combine(_xamlIntermediate, "input.json");
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(inputs))!.AsObject();
            var built = json["ReferenceAssemblies"]?.AsArray() ?? [];
            var byPath = built.OfType<System.Text.Json.Nodes.JsonObject>()
                .Where(item => item["FullPath"]?.GetValue<string>() is { Length: > 0 })
                .GroupBy(item => item["FullPath"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var template = byPath.Values.FirstOrDefault();
            var references = new System.Text.Json.Nodes.JsonArray();
            foreach (var path in Project.MetadataReferences.OfType<PortableExecutableReference>().Select(r => r.FilePath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var item = byPath.TryGetValue(path, out var known) ? known.DeepClone().AsObject() : template?.DeepClone().AsObject() ?? [];
                item["ItemSpec"] = path;
                item["FullPath"] = path;
                references.Add(item);
            }
            json["ReferenceAssemblies"] = references;
            var current = Path.Combine(_markupDirectory, $"input-{DateTime.UtcNow.Ticks}.json");
            File.WriteAllText(current, json.ToJsonString());
            var exe = XamlCompilerHost.Locate() ?? throw new InvalidOperationException("the XAML compiler service wasn't found");
            _xaml.Dispose();
            _xaml = XamlCompilerHost.Start(exe, current, null, _xamlIntermediate, _projectDirectory);
            // Its full compile waits for the next XAML save: by then that save's code is in, and the image it reads is current
            // (the build's assembly on disk lacks types added while the app runs).
            _xamlImageStale = true;
            _xamlFullCompilePending = true;
            _xamlReady = Task.CompletedTask;
            Log($"XAML compiler restarted with {references.Count} references");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _xamlUnavailable = "the XAML compiler service couldn't start again with the project's new references: " + ex.Message;
        }
    }

    private void UseXamlCompiler(XamlStart start)
    {
        _xaml = start.Host;
        _xamlIntermediate = start.Intermediate;
        _xamlUnavailable = start.Unavailable;
        _xamlInputs = start.Inputs;
        foreach (var (path, keys) in start.ApplicationKeys)
        {
            _applicationKeys[path] = keys;
        }
        _xamlReady = CheckWarmUpAsync(start.Warm);
    }

    private async Task CheckWarmUpAsync(Task<(XamlCompileOutput Pass1, XamlCompileOutput Pass2)?> warm)
    {
        try
        {
            if (await warm.ConfigureAwait(false) is not var (pass1, pass2))
            {
                return;
            }
            if (!pass1.Success || !pass2.Success)
            {
                // Usually a file saved while the app started (the compile reads what's on disk now): each save's own
                // compile reports errors, and the next full compile records the type information.
                Log("the XAML compiler's warm-up compile failed: " +
                    string.Join("; ", pass1.Diagnostics.Concat(pass2.Diagnostics).Where(d => d.IsError).Take(3).Select(FormatXaml)));
                return;
            }
            // The service's output should match the build's byte for byte; a difference becomes a change in the first update.
            var differs = new List<string>();
            foreach (var (path, text) in pass1.GeneratedText.Concat(pass2.GeneratedText).Where(p => IsGenerated(p.Key)))
            {
                var document = _committed.GetProject(_projectId)!.Documents.FirstOrDefault(d => string.Equals(d.FilePath, InIntermediate(path), StringComparison.OrdinalIgnoreCase));
                if (document is null)
                {
                    continue;
                }
                var built = (await document.GetTextAsync().ConfigureAwait(false)).ToString().TrimStart('\uFEFF').TrimEnd().Split('\n');
                var served = text.TrimStart('\uFEFF').TrimEnd().Split('\n');
                var line = Enumerable.Range(0, Math.Max(built.Length, served.Length)).FirstOrDefault(i => i >= built.Length || i >= served.Length || built[i].TrimEnd() != served[i].TrimEnd(), -1);
                if (line >= 0)
                {
                    differs.Add($"{Path.GetFileName(path)} line {line + 1}: build '{(line < built.Length ? built[line].Trim() : "")}' service '{(line < served.Length ? served[line].Trim() : "")}'");
                }
            }
            Log($"XAML compiler warm ({pass1.ElapsedMs:0} + {pass2.ElapsedMs:0} ms); differs from the build: {(differs.Count == 0 ? "nothing" : string.Join(", ", differs))}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _xamlUnavailable = "the XAML compiler service stopped: " + ex.Message;
        }
    }    /// <summary>A path in the compiler's output folder, as the same file in the build's obj folder.</summary>
    private string InIntermediate(string outputPath) =>
        Path.GetFullPath(Path.Combine(_xamlIntermediate, Path.GetRelativePath(_xaml!.OutputDirectory, outputPath)));

    private string FormatXaml(XamlCompilerDiagnostic d) =>
        $"{(d.File is { Length: > 0 } file ? Relative(Path.GetFullPath(Path.Combine(_projectDirectory, file))) : Path.GetFileName(_options.ProjectFile))}" +
        $"{(d.Line > 0 ? $"({d.Line},{d.Column})" : "")}: {(d.IsError ? "error" : "warning")} {d.Code}: {d.Message}";

    private static void Log(string text)
    {
        if (Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_LOG") is { Length: > 0 } log)
        {
            File.AppendAllText(log + ".helper", $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
        }
    }

    /// <summary>What compiling a save's XAML gives the update.</summary>
    private sealed record XamlCompiled(List<MarkupPayload> Markup, List<string> Classes);

    /// <summary>
    /// Compiles the saved XAML files with the XAML compiler and puts its generated C# in the workspace. Returns the
    /// markup to send, or the save's result when it can't apply (a compile error, a change the running app can't take).
    /// </summary>
    private async Task<(XamlCompiled? Compiled, SaveResult? Result)> CompileXamlAsync(IReadOnlyList<string> xaml, bool codeChanged, CancellationToken cancellationToken)
    {
        _xamlImageStale |= codeChanged;
        var compiled = new XamlCompiled([], []);
        // A member the markup may bind to was removed or renamed: a build would compile every page against it again.
        var removedMembers = codeChanged && _xaml is not null && _xamlUnavailable is null ? await RemovedMemberNamesAsync(cancellationToken).ConfigureAwait(false) : [];
        if (xaml.Count == 0 && removedMembers.Count == 0)
        {
            return (compiled, null);
        }
        await _xamlReady.ConfigureAwait(false);
        if (_xamlUnavailable is not null || _xaml is null)
        {
            return xaml.Count == 0 ? (compiled, null)
                : (null, new SaveResult(Outcome.Restart, $"{Relative(xaml[0])}: {_xamlUnavailable ?? "the XAML compiler service isn't running"}; XAML changes take effect when the app restarts"));
        }
        if (xaml.FirstOrDefault(p => !_xamlInputs.Contains(p)) is { } added)
        {
            // A new page saved before its code-behind: a rebuild would fail the same way until the class exists.
            var newClass = XDocument.Parse(await ReadSharedAsync(added, cancellationToken).ConfigureAwait(false)).Root?
                .Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value;
            if (newClass is not null && (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!.GetTypeByMetadataName(newClass) is null)
            {
                return (null, new SaveResult(Outcome.Waiting, $"{Relative(added)}: waiting for its class {newClass} to be written", [added]));
            }
            return (null, new SaveResult(Outcome.Restart, $"{Relative(added)} is new since the app was built: a new XAML file takes effect when the app is rebuilt"));
        }
        foreach (var path in xaml)
        {
            try
            {
                XDocument.Parse(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (System.Xml.XmlException ex)
            {
                // A half-typed tag: the compiler would only say the same thing less precisely.
                return (null, new SaveResult(Outcome.Error, XmlError(path, ex)));
            }
        }

        var pass1 = xaml.Count > 0 ? await _xaml.Pass1Async(xaml, cancellationToken).ConfigureAwait(false) : null;
        if (pass1 is not null && Failed(pass1) is { } pass1Error)
        {
            return (null, pass1Error);
        }
        if (pass1 is not null && await UseGeneratedAsync(pass1, xaml, typeInfo: false, cancellationToken).ConfigureAwait(false) is { } fieldChange)
        {
            return (null, fieldChange);
        }

        // Pass 2 reads the app's own types (x:Bind paths, event handlers): after a C# change, from the current code.
        if (_xamlImageStale)
        {
            var compilation = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
            using var image = new MemoryStream();
            var emit = compilation.Emit(image, options: new EmitOptions(metadataOnly: true, includePrivateMembers: true), cancellationToken: cancellationToken);
            if (!emit.Success)
            {
                return (null, new SaveResult(Outcome.Error, string.Join(Environment.NewLine + "  ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(3).Select(Format))));
            }
            _xamlImage = image.ToArray();
            _xamlImageStale = false;
        }
        if (_xamlFullCompilePending)
        {
            // A restarted compiler: every page once (pass 1, then pass 2 with XamlTypeInfo) against the current code.
            var all1 = await _xaml.Pass1Async(null, cancellationToken).ConfigureAwait(false);
            var all2 = await _xaml.Pass2Async(null, _xamlImage, typeInfo: true, cancellationToken).ConfigureAwait(false);
            if ((Failed(all1) ?? Failed(all2)) is { } restartedError)
            {
                return (null, restartedError);
            }
            if (await UseGeneratedAsync(all1, xaml, typeInfo: false, cancellationToken).ConfigureAwait(false) is { } restartedChange)
            {
                return (null, restartedChange);
            }
            await UseGeneratedAsync(all2, xaml, typeInfo: true, cancellationToken).ConfigureAwait(false);
            _xamlFullCompilePending = false;
        }
        if (removedMembers.Count > 0)
        {
            // Checked only: x:Bind to a member this save removed fails the build (WMC1110), so it fails here too,
            // rather than binding to the member the running app still has.
            var check = await _xaml.Pass2Async(null, _xamlImage, typeInfo: false, cancellationToken).ConfigureAwait(false);
            if (Failed(check) is { } bindingError)
            {
                return (null, bindingError);
            }
        }
        if (xaml.Count == 0)
        {
            return (compiled, null);
        }
        var pass2 = await _xaml.Pass2Async(xaml, _xamlImage, typeInfo: false, cancellationToken).ConfigureAwait(false);
        if (Failed(pass2) is { } pass2Error)
        {
            return (null, pass2Error);
        }
        if (pass2.XamlTypeInfoMayBeStale)
        {
            // The pages use a type or member XamlTypeInfo doesn't have yet: regenerated as a build does (the service
            // replays the other pages' type information from its cache).
            var full = await _xaml.Pass2Async(xaml, _xamlImage, typeInfo: true, cancellationToken).ConfigureAwait(false);
            if (Failed(full) is { } fullError)
            {
                return (null, fullError);
            }
            await UseGeneratedAsync(full, xaml, typeInfo: true, cancellationToken).ConfigureAwait(false);
        }
        await UseGeneratedAsync(pass2, xaml, typeInfo: false, cancellationToken).ConfigureAwait(false);

        foreach (var path in xaml)
        {
            var xbf = Path.ChangeExtension(Path.Combine(_xaml.OutputDirectory, Path.GetRelativePath(_projectDirectory, path)), ".xbf");
            if (!pass2.GeneratedXbf.TryGetValue(xbf, out var bytes))
            {
                return (null, new SaveResult(Outcome.Restart, $"{Relative(path)}: the XAML compiler produced no .xbf for it; it takes effect when the app restarts"));
            }
            var root = XDocument.Parse(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false)).Root!;
            var className = root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value;
            var kind = root.Name.LocalName == "Application" ? MarkupKind.Application
                : className is not null ? MarkupKind.Component
                : Relative(path).Replace('/', '\\').EndsWith(@"Themes\Generic.xaml", StringComparison.OrdinalIgnoreCase) ? MarkupKind.Generic
                : MarkupKind.Dictionary;
            var payload = new MarkupPayload { Kind = kind, Uri = UriFor(path), Xbf = bytes, Manifest = new ComponentManifest { ClassName = className ?? "" } };
            if (kind == MarkupKind.Application)
            {
                var keys = Keys(root);
                if (_applicationKeys.TryGetValue(path, out var before))
                {
                    payload.DroppedKeys = [.. before.Except(keys)];
                }
                _applicationKeys[path] = keys;
            }
            compiled.Markup.Add(payload);
            _sources[UriFor(path)] = path;
            if (kind == MarkupKind.Component)
            {
                compiled.Classes.Add(className!);
            }
        }
        Log($"XAML compiled: pass 1 {pass1.ElapsedMs:0} ms, pass 2 {pass2.ElapsedMs:0} ms{(pass2.XamlTypeInfoMayBeStale ? ", XamlTypeInfo regenerated" : "")}");
        return (compiled, null);
    }

    private SaveResult? Failed(XamlCompileOutput output) =>
        output.Success && !output.Diagnostics.Any(d => d.IsError) ? null
            : new SaveResult(Outcome.Error, output.Diagnostics.Any(d => d.IsError)
                ? string.Join(Environment.NewLine + "  ", output.Diagnostics.Where(d => d.IsError).Take(3).Select(FormatXaml))
                : "the XAML compiler failed without saying why");

    /// <summary>
    /// The compiler's generated C# as the workspace's documents (the build's, in obj). Only the files generated from
    /// <paramref name="pages"/> (and XamlTypeInfo, when <paramref name="typeInfo"/>) are taken: the service may return every
    /// page's, and the others are the same as the app has. A named element whose type changed is refused: edit and
    /// continue never compares the .g.i.cs files that declare those fields (it treats them as design-time only), so it
    /// wouldn't report that the running app can't change a field's type.
    /// </summary>
    private async Task<SaveResult?> UseGeneratedAsync(XamlCompileOutput output, IReadOnlyCollection<string>? pages, bool typeInfo, CancellationToken cancellationToken)
    {
        var stems = pages?.Select(p => Path.ChangeExtension(Path.GetRelativePath(_projectDirectory, p), null)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (outputPath, generated) in output.GeneratedText.Where(p => IsGenerated(p.Key)))
        {
            var relative = Path.GetRelativePath(_xaml!.OutputDirectory, outputPath);
            var isTypeInfo = Path.GetFileName(relative).Equals("XamlTypeInfo.g.cs", StringComparison.OrdinalIgnoreCase);
            var stem = GeneratedStem(relative);
            if (isTypeInfo ? !typeInfo : stems is not null && !stems.Contains(stem))
            {
                continue;
            }
            var path = InIntermediate(outputPath);
            var text = SourceText.From(WithoutRedundantStaticInitializers(generated.TrimStart('\uFEFF')), Encoding.UTF8, SourceHashAlgorithm.Sha256);
            var ids = _solution.GetDocumentIdsWithFilePath(path);
            if (ids.IsEmpty)
            {
                _solution = _solution.AddDocument(DocumentId.CreateNewId(_projectId), Path.GetFileName(path), text, filePath: path);
                continue;
            }
            var before = await _solution.GetDocument(ids[0])!.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (before.ContentEquals(text))
            {
                continue;
            }
            if (IsPass1Part(path) && FieldTypeChange(before.ToString(), text.ToString()) is { } change)
            {
                return new SaveResult(Outcome.Restart, $"{Relative(XamlFor(path))}: {change}, and the running app can't change the type of a field; it takes effect when the app restarts");
            }
            _solution = ids.Aggregate(_solution, (s, id) => s.WithDocumentText(id, text));
        }
        return null;
    }

    /// <summary>"x:Name 'Later' changes from TextBlock to TextBox", for the first named field whose type changes.</summary>
    internal static string? FieldTypeChange(string before, string after)
    {
        static Dictionary<string, string> Fields(string text) => CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables.Select(v => (v.Identifier.ValueText, f.Declaration.Type.ToString())))
            .GroupBy(f => f.Item1).ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.Ordinal);
        var old = Fields(before);
        foreach (var (name, type) in Fields(after))
        {
            if (old.TryGetValue(name, out var was) && was != type)
            {
                return $"x:Name '{name}' changes from {Short(was)} to {Short(type)}";
            }
        }
        return null;
        static string Short(string type) => type[(type.LastIndexOf('.') + 1)..];
    }

    /// <summary>
    /// "private static bool isobj9TextDisabled = false;" becomes "...isobj9TextDisabled;", which is the same: Roslyn 5.9
    /// fails (NullReferenceException in DefinitionMap.GetPreviousMethodHandle) on an update that adds an initialized static
    /// field to a type an earlier update added. Until the XAML compiler stops emitting the redundant initializer.
    /// </summary>
    private static string WithoutRedundantStaticInitializers(string text) =>
        Regex.Replace(text, @"(private static bool isobj\w+Disabled) = false;", "$1;");

    /// <summary>The XAML file a generated file in obj was generated from (Pages\Foo.g.cs: Pages\Foo.xaml).</summary>
    private string XamlFor(string generatedPath) =>
        Path.Combine(_projectDirectory, GeneratedStem(Path.GetRelativePath(_xamlIntermediate, generatedPath)) + ".xaml");

    private bool IsXamlGenerated(string? path) =>
        path is not null && _xamlIntermediate.Length > 0 && IsGenerated(path) && path.StartsWith(_xamlIntermediate, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A rude edit in plain words. In the XAML compiler's code it is said against the XAML file: what the compiled code
    /// for the change does that the running app can't (ENC0033 "Deleting field 'obj18'" is "it deletes field 'obj18'").
    /// </summary>
    private string RudeEditReason(IReadOnlyList<Diagnostic> rude)
    {
        var first = rude[0];
        var path = first.Location.SourceTree?.FilePath;
        if (!IsXamlGenerated(path))
        {
            return Format(first);
        }
        var message = first.GetMessage();
        var what = Regex.Replace(message, @"\s*requires restarting the application\.?$", "");
        var subject = Regex.Match(what, @"^\w+\s+(.*)$") is { Success: true } m ? m.Groups[1].Value : what;
        var plain = first.Id switch
        {
            "ENC0020" => $"renames code the XAML compiler generated for it ({subject}), and a running app can't rename code",
            "ENC0033" => $"removes code the XAML compiler generated for it ({subject}), and a running app can't remove code",
            "ENC0009" => "changes the type of a field the XAML compiler generated for it, and a running app can't change a field's type",
            _ => what.Length > 0 ? char.ToLowerInvariant(what[0]) + what[1..] : message,
        };
        var more = rude.Count > 1 ? $"; {rude.Count - 1} more like it" : "";
        return $"{Relative(XamlFor(path!))}: this change {plain} ({first.Id}{more}); it takes effect when the app restarts";
    }
}
