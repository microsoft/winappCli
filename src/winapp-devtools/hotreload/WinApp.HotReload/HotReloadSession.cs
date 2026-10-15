// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using WinApp.HotReload.Protocol;

namespace WinApp.HotReload;

internal sealed record SessionOptions(string ProjectFile, string CscArgsFile, string TargetsFile, string PipeName, string Token);

internal enum Outcome { Applied, Waiting, Error, Restart, Unchanged }

/// <param name="StillFailing">Files of an otherwise applied save whose errors remain: they are sent again with the next save.</param>
internal sealed record SaveResult(Outcome Outcome, string Detail, IReadOnlyList<string>? StillFailing = null);

/// <summary>
/// Owns the Roslyn view of the running app and turns each batch of saved files into one live update:
/// an edit-and-continue delta for C#, runtime-parseable markup for XAML, and the instances to rebuild.
/// </summary>
internal sealed partial class HotReloadSession
{
    private const string ExplicitInterfaceCapability = "AddExplicitInterfaceImplementation";
    private static readonly string[] ExcludedDirectories = ["bin", "obj", ".git", ".vs", "packages", "node_modules"];
    private readonly SessionOptions _options;
    private readonly string _projectDirectory;
    private readonly AgentClient _agent;
    private readonly AdhocWorkspace _workspace;
    private readonly ProjectId _projectId;
    private readonly EncService _enc;
    private readonly string _baseDirectory;
    private readonly string _markupDirectory;
    private readonly string[] _cscArgs;
    private readonly Dictionary<string, XamlComponent> _components = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DocumentId> _generated = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ghosts = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Files moved (a folder renamed) while the app runs, to the document that has their original path.</summary>
    private readonly Dictionary<string, DocumentId> _moved = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Class library projects the app references (in folders under the app's or elsewhere), by folder, with their assembly name.</summary>
    private readonly Dictionary<string, string> _libraries = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The referenced projects outside the app's folder (a solution's sibling projects); watched like it.</summary>
    private readonly List<string> _externalLibraries = [];
    /// <summary>When each library's build output was picked up: earlier saves are part of it.</summary>
    private readonly Dictionary<string, DateTime> _librariesBuilt = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Assemblies copied next to the running app since the last update it received.</summary>
    private readonly List<string> _newAssemblies = [];
    private Solution _solution;
    private Solution _committed;
    private bool _newTypesOnly;
    private bool _xamlReload;
    /// <summary>The app's WinUI forgets what it read about types an update changed (see HotReloadResponse.InvalidatesTypes).</summary>
    private bool _runtimeInvalidatesTypes;
    private readonly string? _launchTargets;
    private readonly string[]? _launchProperties;

    private HotReloadSession(SessionOptions options, AgentClient agent, AdhocWorkspace workspace, ProjectId projectId, Solution solution,
        EncService enc, string baseDirectory, string[] cscArgs)
    {
        _options = options;
        _projectDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ProjectFile))!;
        _agent = agent;
        _workspace = workspace;
        _projectId = projectId;
        _solution = _committed = solution;
        _enc = enc;
        _baseDirectory = baseDirectory;
        _cscArgs = cscArgs;
        _markupDirectory = MarkupDirectory(options.PipeName);
        _launchTargets = File.Exists(options.TargetsFile) ? File.ReadAllText(options.TargetsFile) : null;
        _launchProperties = File.Exists(options.CscArgsFile + ".props") ? File.ReadAllLines(options.CscArgsFile + ".props") : null;
        Directory.CreateDirectory(_markupDirectory);
        // Lets winapp tell this folder from one a killed session left behind.
        File.WriteAllText(Path.Combine(_markupDirectory, "owner.pid"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public string ProjectDirectory => _projectDirectory;

    /// <summary>Why the running app can't reload XAML although its WinUI could (see HotReloadResponse.XamlReloadUnavailable).</summary>
    public string? XamlReloadUnavailable { get; private init; }

    /// <summary>An optimized build (-c Release): the runtime refuses code updates to it, so every save restarts the app.</summary>
    public static bool IsOptimizedBuild(string cscArgsFile) =>
        File.Exists(cscArgsFile) && ReadCompilerArguments(cscArgsFile).Any(a => a is "/optimize+" or "-optimize+" or "/optimize" or "-optimize" or "/o+" or "-o+");

    /// <summary>What to say once when the app is up, when not every save can apply live.</summary>
    public string? LaunchNote => XamlReloadUnavailable is { } reason ? $"{reason} Saving a .xaml file restarts the app; C# edits still apply live." : null;

    /// <summary>The folders whose files are watched: the app's, and those of referenced projects outside it.</summary>
    public IReadOnlyList<string> WatchRoots
    {
        get
        {
            if (!_librariesFound)
            {
                FindLibraries();
            }
            return [_projectDirectory, .. _externalLibraries];
        }
    }

    public static async Task<HotReloadSession> CreateAsync(SessionOptions options, CancellationToken cancellationToken)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ProjectFile))!;
        var cscArgs = ReadCompilerArguments(options.CscArgsFile);
        // Started first: its warm-up (the app's references, a full compile) overlaps the app's start and the workspace's.
        var xaml = UseCompiler ? StartXamlCompiler(cscArgs, projectDirectory, MarkupDirectory(options.PipeName)) : null;
        var agent = await AgentClient.ConnectAsync(options.PipeName, options.Token, cancellationToken).ConfigureAwait(false);
        var hello = await agent.SendAsync(new HotReloadRequest { Op = "hello" }, cancellationToken).ConfigureAwait(false);
        if (!hello.Ok)
        {
            throw new InvalidOperationException("The app refused hot reload: " + hello.Error);
        }

        var assemblies = MefHostServices.DefaultAssemblies.Concat(
            new[] { "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features", "Microsoft.CodeAnalysis.ExternalAccess.HotReload" }.Select(Assembly.Load));
        var workspace = new AdhocWorkspace(MefHostServices.Create(assemblies));
        var info = CreateProjectInfo(workspace, options.ProjectFile, cscArgs);
        info = info.WithCompilationOutputInfo(info.CompilationOutputInfo.WithAssemblyPath(SnapshotBaseline(info.CompilationOutputInfo.AssemblyPath!, options.PipeName)));
        info = info.WithDocuments(info.Documents.Select(document => AsBuilt(document, options.CscArgsFile, projectDirectory)));
        if (UseCompiler)
        {
            // The XAML compiler's output replaces the generated documents; the baseline keeps the text the running binary
            // was built from, which a build may overwrite in obj.
            info = info.WithDocuments(info.Documents.Select(Pinned));
        }
        var solution = workspace.AddProject(info).Solution;
        var capabilities = (hello.Capabilities ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
        var newTypeCapabilities = capabilities.Add(ExplicitInterfaceCapability);
        HotReloadSession? session = null;
        var enc = new EncService(solution, info.Id, () => session?._newTypesOnly == true ? newTypeCapabilities : capabilities);
        await enc.StartAsync(solution, cancellationToken).ConfigureAwait(false);

        session = new HotReloadSession(options, agent, workspace, info.Id, solution, enc, hello.BaseDirectory ?? "", cscArgs) { _xamlReload = hello.XamlReload, XamlReloadUnavailable = hello.XamlReloadUnavailable, _runtimeInvalidatesTypes = hello.InvalidatesTypes };
        await session.LoadComponentsAsync(cancellationToken).ConfigureAwait(false);
        if (xaml is not null)
        {
            session.UseXamlCompiler(xaml);
        }
        session._warmUp = Task.Run(() => session.WarmUpAsync(cancellationToken), cancellationToken);
        return session;
    }

    private static string MarkupDirectory(string pipeName) => Path.Combine(Path.GetTempPath(), "winapp-hotreload", pipeName);

    /// <summary>
    /// A copy of the assembly and PDB the running app was built from: edit-and-continue diffs against them, and another
    /// build (an IDE, a second terminal) may overwrite the originals in obj while the app runs.
    /// </summary>
    private static string SnapshotBaseline(string assembly, string pipeName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "winapp-hotreload", pipeName, "baseline");
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, Path.GetFileName(assembly));
        try
        {
            File.Copy(assembly, copy, overwrite: true);
            if (File.Exists(Path.ChangeExtension(assembly, ".pdb")))
            {
                File.Copy(Path.ChangeExtension(assembly, ".pdb"), Path.ChangeExtension(copy, ".pdb"), overwrite: true);
                PointToOwnPdb(copy);
            }
            return copy;
        }
        catch (IOException)
        {
            return assembly;
        }
    }

    /// <summary>
    /// Edit and continue opens the baseline's PDB at the path the assembly records (obj\...\App.pdb) before it looks next
    /// to the assembly, and a build while the app runs overwrites that one. The copy records the copied PDB instead
    /// (or, when that path is longer than the room the build left, a path that doesn't exist, which falls back to it).
    /// </summary>
    internal static void PointToOwnPdb(string assemblyCopy)
    {
        var bytes = File.ReadAllBytes(assemblyCopy);
        using (var reader = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes)))
        {
            foreach (var entry in reader.ReadDebugDirectory().Where(e => e.Type == System.Reflection.PortableExecutable.DebugDirectoryEntryType.CodeView))
            {
                // "RSDS", GUID, age, then the path (UTF-8, NUL-terminated) in the rest of the entry.
                const int header = 4 + 16 + 4;
                var room = entry.DataSize - header - 1;
                if (entry.DataPointer <= 0 || room <= 0)
                {
                    continue;
                }
                var own = Encoding.UTF8.GetBytes(Path.ChangeExtension(assemblyCopy, ".pdb"));
                if (own.Length > room)
                {
                    own = Encoding.UTF8.GetBytes(Path.Combine(Path.GetPathRoot(assemblyCopy)!, "~winapp-moved", Path.GetFileNameWithoutExtension(assemblyCopy) + ".pdb"));
                }
                if (own.Length > room)
                {
                    continue;
                }
                Array.Clear(bytes, entry.DataPointer + header, entry.DataSize - header);
                own.CopyTo(bytes, entry.DataPointer + header);
            }
        }
        File.WriteAllBytes(assemblyCopy, bytes);
    }

    /// <summary>The compiler arguments of the build that produced the running binaries.</summary>
    private static string[] ReadCompilerArguments(string path)
    {
        var lines = File.ReadAllLines(path);
        var start = Array.FindLastIndex(lines, line => line == "/noconfig");
        return start > 0 ? lines[start..] : lines;
    }

    private static ProjectInfo CreateProjectInfo(Workspace workspace, string projectFile, string[] args)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        // The CsWinRT AOT generator names its helper classes by discovery order, so adding a type renames them
        // (a rude edit). They only matter for trimmed/AOT marshalling; a Debug app falls back to reflection.
        args = args.Where(a => !(a.StartsWith("/analyzer:", StringComparison.Ordinal) && a.Contains("WinRT.SourceGenerator", StringComparison.OrdinalIgnoreCase))).ToArray();
        var info = CommandLineProject.CreateProjectInfo(Path.GetFileNameWithoutExtension(projectFile), LanguageNames.CSharp, args, directory, workspace);
        var output = Path.GetFullPath(Path.Combine(directory, info.OutputFilePath!));
        var documents = info.Documents.DistinctBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        return info.WithFilePath(Path.GetFullPath(projectFile)).WithDocuments(documents)
            .WithCompilationOutputInfo(info.CompilationOutputInfo.WithAssemblyPath(output));
    }

    /// <summary>
    /// A source saved after the running binary was compiled is read from the copy the build took, so edit-and-continue
    /// diffs the saved file against what is actually running. The save itself is applied once the session is ready.
    /// </summary>
    private static DocumentInfo AsBuilt(DocumentInfo document, string compilerArgs, string projectDirectory)
    {
        if (document.FilePath is not { } path || File.GetLastWriteTimeUtc(path) <= File.GetLastWriteTimeUtc(compilerArgs))
        {
            return document;
        }
        var copy = Path.Combine(compilerArgs + ".src", Path.GetRelativePath(projectDirectory, path));
        if (!File.Exists(copy))
        {
            return document;
        }
        // From the file's bytes, so the checksum matches the one the compiler recorded; otherwise edit-and-continue
        // treats the document as out of sync and ignores its changes.
        using var stream = File.OpenRead(copy);
        var text = SourceText.From(stream, checksumAlgorithm: SourceHashAlgorithm.Sha256);
        return document.WithTextLoader(TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), path)));
    }

    private Project Project => _solution.GetProject(_projectId)!;

    /// <summary>See <see cref="HotReloadRequest.LaunchItems"/>: from the markup as the app launched with it.</summary>
    private readonly Dictionary<string, Dictionary<string, int>> _launchItems = new(StringComparer.Ordinal);

    private async Task LoadComponentsAsync(CancellationToken cancellationToken)
    {
        var compilation = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        var resolver = new CompilationTypeResolver(compilation);
        var objDirectory = Path.Combine(_projectDirectory, "obj") + Path.DirectorySeparatorChar;
        foreach (var path in EnumerateSources("*.xaml"))
        {
            var component = new XamlComponent(path, UriFor(path));
            try
            {
                var text = await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false);
                component.Latest = XamlTransform.Transform(text, resolver);
                if (component.ClassName is { } className && XamlTransform.CollectionItemCounts(text) is { Count: > 0 } counts)
                {
                    _launchItems[className] = counts;
                }
            }
            catch (XmlException)
            {
                continue;
            }
            if (component.Latest.RootTypeName == "Microsoft.UI.Xaml.Application")
            {
                _applicationSettings[path] = ApplicationSettings(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false));
            }
            if (component.ClassName is null || component.Latest.RootTypeName == "Microsoft.UI.Xaml.Application" ||
                compilation.GetTypeByMetadataName(component.ClassName) is not { } type)
            {
                continue;
            }
            var builtNames = component.Latest.Names.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var member in type.GetMembers())
            {
                var compiled = member.DeclaringSyntaxReferences.Any(r => r.SyntaxTree.FilePath.StartsWith(objDirectory, StringComparison.OrdinalIgnoreCase));
                // Only x:Name fields: the compiler also generates others (for example 'Bindings') that never go stale.
                if (compiled && member is IFieldSymbol field && builtNames.Contains(field.Name))
                {
                    component.FrozenFields.Add(field.Name);
                }
                if (compiled && member.Name == "InitializeComponent")
                {
                    component.HasCompiledPart = true;
                }
            }
            _components[path] = component;
        }
    }

    private IEnumerable<string> EnumerateSources(string pattern) =>
        Directory.EnumerateFiles(_projectDirectory, pattern, SearchOption.AllDirectories).Where(IsSource);

    private static readonly string[] AssetExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".json", ".txt", ".ttf", ".otf", ".mp3", ".mp4", ".wav", ".html"];

    /// <summary>Content the app shows through an Image (which the agent reloads), rather than reads itself.</summary>
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg"];

    /// <summary>Files the build typically copies next to the app (project Content), outside bin and obj.</summary>
    public static bool IsAsset(string path) => AssetExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>A file of the app project itself (not of a project in a folder under it).</summary>
    public bool IsSource(string path) => IsWatched(path) && NestedProjectDirectory(path) is null;

    /// <summary>App files and files of class libraries in folders under the app's or referenced from elsewhere.</summary>
    public bool IsWatched(string path) => _externalLibraries.Prepend(_projectDirectory).Any(root =>
        Path.GetRelativePath(root, path) is var relative && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative) &&
        !relative.Split(Path.DirectorySeparatorChar).SkipLast(1).Any(part => ExcludedDirectories.Contains(part, StringComparer.OrdinalIgnoreCase)));

    /// <summary>The folder of the project, other than the app, that <paramref name="path"/> belongs to; null for app files.</summary>
    private string? NestedProjectDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (_externalLibraries.FirstOrDefault(l => full.StartsWith(l + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) is { } external)
        {
            return external;
        }
        var root = _projectDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetRelativePath(root, path).StartsWith("..", StringComparison.Ordinal))
        {
            return null;
        }
        for (var directory = Path.GetDirectoryName(Path.GetFullPath(path)); directory is not null && directory.Length > root.Length; directory = Path.GetDirectoryName(directory))
        {
            if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.csproj").Any())
            {
                return directory;
            }
        }
        return null;
    }

    /// <summary>The app's Compile and Page items, as of the last project file refresh.</summary>
    private List<string> _appItems = [];
    private readonly HashSet<string> _nestedNoted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A referenced project in a folder under the app's is also picked up by the app's own default file patterns, so its
    /// next real build fails with duplicate types (CS0111) unless the app project excludes that folder.
    /// </summary>
    private void NoteNestedLibraries()
    {
        foreach (var library in _appItems.Select(NestedProjectDirectory).OfType<string>().Where(l => !_externalLibraries.Contains(l)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(l => Directory.EnumerateFiles(l, "*.csproj").Any(p => Project.MetadataReferences.OfType<PortableExecutableReference>().Any(r =>
                string.Equals(Path.GetFileNameWithoutExtension(r.FilePath), Path.GetFileNameWithoutExtension(p), StringComparison.OrdinalIgnoreCase))))
            .Where(_nestedNoted.Add))
        {
            var folder = Path.GetRelativePath(_projectDirectory, library);
            _pendingNotes.Add($"{folder} is a project inside the app's folder, so the app's next build also compiles its files (CS0111): add <Compile Remove=\"{folder}\\**\" />, <Page Remove=\"{folder}\\**\" /> and <None Remove=\"{folder}\\**\" /> to {Path.GetFileName(_options.ProjectFile)}, or keep the library in a folder next to the app");
        }
    }

    /// <summary>The project a referenced assembly was built by: the folder with a project file whose bin or obj holds it.</summary>
    private string? LibraryProjectDirectory(string assembly)
    {
        for (var directory = Path.GetDirectoryName(Path.GetFullPath(assembly)); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var relative = Path.GetRelativePath(directory, assembly);
            if (!relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // The app itself, or a folder that contains it, is not a library of it.
            var containsApp = !Path.GetRelativePath(directory, _projectDirectory).StartsWith("..", StringComparison.Ordinal);
            return !containsApp && Directory.EnumerateFiles(directory, "*.csproj").Any() ? directory : null;
        }
        return null;
    }

    /// <summary>Referenced class libraries built from a project: the project references resolve to their build output.</summary>
    private bool _librariesFound;

    private List<string> FindLibraries()
    {
        _librariesFound = true;
        var added = new List<string>();
        foreach (var reference in Project.MetadataReferences.OfType<PortableExecutableReference>())
        {
            if (reference.FilePath is { } file && LibraryProjectDirectory(file) is { } library && _libraries.TryAdd(library, Path.GetFileNameWithoutExtension(file)))
            {
                if (Path.GetRelativePath(_projectDirectory, library).StartsWith("..", StringComparison.Ordinal))
                {
                    _externalLibraries.Add(library);
                }
                // Found right after a build that included it (the launch, or the reference being added).
                _librariesBuilt[library] = DateTime.UtcNow;
                added.Add(library);
            }
        }
        return added;
    }

    /// <summary>
    /// A library's XAML for the running app, at its ms-appx:///Assembly/ URI. Its C# is compiled into an assembly the app
    /// already loaded, so code edits need a restart (null is returned with the reason).
    /// </summary>
    private async Task<(MarkupPayload? Payload, SaveResult? Result)> LibraryMarkupAsync(string path, string library, CompilationTypeResolver resolver, CancellationToken cancellationToken)
    {
        var assembly = _libraries[library];
        if (!path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            return (null, new SaveResult(Outcome.Restart, $"{Relative(path)}: {assembly} code changes take effect when the app restarts"));
        }
        if (!File.Exists(path))
        {
            return (null, null);
        }
        var uri = $"ms-appx:///{assembly}/{Path.GetRelativePath(library, path).Replace('\\', '/')}";
        var (payload, error, transformed) = await MarkupForAsync(path, uri, resolver, cancellationToken).ConfigureAwait(false);
        // A UserControl or Page of the library: its compiled InitializeComponent loads the text, the manifest wires it.
        return error is null && payload is null
            ? (new MarkupPayload { Kind = MarkupKind.Component, Uri = uri, Xaml = transformed!.RuntimeXaml, Manifest = transformed.Manifest, LineMap = transformed.LineMap }, null)
            : (payload, error);
    }

    /// <summary>The payload for App.xaml or a resource dictionary (Generic.xaml included); no payload and no error for a component.</summary>
    private async Task<(MarkupPayload? Payload, SaveResult? Error, XamlTransformResult? Transformed)> MarkupForAsync(string path, string uri, CompilationTypeResolver resolver, CancellationToken cancellationToken)
    {
        XamlTransformResult transformed;
        try
        {
            transformed = XamlTransform.Transform(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false), resolver);
        }
        catch (XmlException ex)
        {
            return (null, new SaveResult(Outcome.Error, XmlError(path, ex)), null);
        }
        if (transformed.Errors.Count > 0)
        {
            return (null, new SaveResult(Outcome.Error, string.Join(Environment.NewLine + "  ", transformed.Errors.Select(e => Relative(path) + e))), transformed);
        }
        if (transformed.RootTypeName == "Microsoft.UI.Xaml.Application")
        {
            // Settings on <Application> itself (RequestedTheme, HighContrastAdjustment...) only apply when it starts.
            var settings = ApplicationSettings(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false));
            if (_applicationSettings.TryGetValue(path, out var before) && before != settings)
            {
                _pendingNotes.Add($"{Relative(path)}: settings on <Application> ({ChangedSettings(before, settings)}) only apply at startup: this change takes effect when the app restarts");
            }
            _applicationSettings[path] = settings;
            // App.xaml: only its resources can change while the app runs.
            var (resources, lineMap) = XamlTransform.ApplicationResources(transformed.RuntimeXaml, uri, transformed.LineMap);
            _sources[uri] = path;
            return (new MarkupPayload { Kind = MarkupKind.Application, Uri = uri, Xaml = resources, LineMap = lineMap }, null, transformed);
        }
        if (transformed.ClassName is not null)
        {
            return (null, null, transformed);
        }
        var generic = uri.EndsWith("/Themes/Generic.xaml", StringComparison.OrdinalIgnoreCase);
        foreach (var style in XDocument.Parse(transformed.RuntimeXaml).Descendants().Where(e => e.Name.LocalName == "Style"))
        {
            // The parser would fail on the whole dictionary; the type may simply not be written yet.
            if (((string?)style.Attribute("TargetType"))?.Split(':') is [var prefix, var name] && style.GetNamespaceOfPrefix(prefix) is { } ns && resolver.Resolve(ns.NamespaceName, name) is null)
            {
                return (null, new SaveResult(Outcome.Error, $"{Relative(path)}: Unknown type '{name}' in XML namespace '{ns.NamespaceName}'"), transformed);
            }
        }
        _sources[uri] = path;
        return (new MarkupPayload { Kind = generic ? MarkupKind.Generic : MarkupKind.Dictionary, Uri = uri, Xaml = transformed.RuntimeXaml, LineMap = transformed.LineMap }, null, transformed);
    }

    private string UriFor(string xamlPath) => "ms-appx:///" + Path.GetRelativePath(_projectDirectory, xamlPath).Replace('\\', '/');

    /// <summary>With WINAPP_HOT_RELOAD_LOG set: how long each step of a save took (in the log's .helper file).</summary>
    private void Phase(string name)
    {
        if (Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_LOG") is { Length: > 0 } log)
        {
            File.AppendAllText(log + ".helper", $"{DateTime.Now:HH:mm:ss.fff} {_phase.ElapsedMilliseconds,6} ms {name}{Environment.NewLine}");
        }
        _phase.Restart();
    }

    private readonly Stopwatch _phase = new();

    /// <summary>
    /// The first save would otherwise generate and check every component's partial and load the edit-and-continue
    /// baseline (seconds on a large app): done while the user looks at the app, and awaited by the first save.
    /// </summary>
    private Task _warmUp = Task.CompletedTask;

    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await UpdateGeneratedCodeAsync(cancellationToken).ConfigureAwait(false);
            await UpdatesAsync(cancellationToken).ConfigureAwait(false);
            Discard();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The first save does it again, and reports what went wrong.
        }
    }

    public async Task<SaveResult> ProcessAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        await _warmUp.ConfigureAwait(false);
        _phase.Restart();
        var batch = paths.Select(Relative).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.FirstOrDefault(p => p.EndsWith(".appxmanifest", StringComparison.OrdinalIgnoreCase)) is { } manifest)
        {
            // Identity, capabilities and extensions take effect when the package is registered again.
            return new SaveResult(Outcome.Restart, $"{Relative(manifest)} changes take effect when the app is registered again");
        }
        if (paths.FirstOrDefault(IsBuildFile) is { } buildFile)
        {
            // Build settings (constants, properties, package versions) apply to the whole compilation.
            return new SaveResult(Outcome.Restart, $"{Relative(buildFile)}: build settings take effect when the app is rebuilt");
        }
        if (paths.FirstOrDefault(p => p.EndsWith(".resw", StringComparison.OrdinalIgnoreCase)) is { } strings)
        {
            // Localized strings are compiled into the app's resources.pri, which the app reads at startup.
            return new SaveResult(Outcome.Restart, $"{Relative(strings)}: string resources take effect when the app restarts");
        }
        if (!_xamlReload && paths.Any(p => p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var path in paths.Where(p => p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && File.Exists(p)))
            {
                try
                {
                    XDocument.Parse(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false));
                }
                catch (XmlException ex)
                {
                    // Don't rebuild for a half-typed tag; the build would only fail.
                    return new SaveResult(Outcome.Error, XmlError(path, ex));
                }
            }
            return new SaveResult(Outcome.Restart, XamlReloadUnavailable is not null
                ? "XAML hot reload is not enabled for this app (a Debug build, or the EnableXamlHotReload project property set to true, for example -p EnableXamlHotReload=true, enables it): XAML changes take effect when the app restarts"
                : "this app's WinUI version can't reload XAML while it runs");
        }
        if (!_librariesFound)
        {
            // Libraries the app was built with.
            FindLibraries();
        }
        // Files of a library project under the app's folder: live once the app references it (until then it isn't built).
        var libraryPaths = paths.Where(p => NestedProjectDirectory(p) is not null).ToList();
        paths = paths.Except(libraryPaths, StringComparer.OrdinalIgnoreCase).ToList();
        // Images and other files the build copies next to the app: the project file decides which, so it's asked.
        var assets = paths.Where(IsAsset).ToList();
        paths = paths.Except(assets, StringComparer.OrdinalIgnoreCase).ToList();
        // Inputs of the app's source generators (AdditionalFiles): the generators read the new text.
        foreach (var path in assets.Concat(paths))
        {
            foreach (var document in Project.AdditionalDocuments.Where(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                _solution = File.Exists(path)
                    ? _solution.WithAdditionalDocumentText(document.Id, SourceText.From(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false), Encoding.UTF8, SourceHashAlgorithm.Sha256))
                    : _solution.RemoveAdditionalDocument(document.Id);
            }
        }
        var projectChanged = paths.Any(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (!projectChanged && assets.Count > 0 && assets.All(a => _contentTargets.ContainsKey(a) || !File.Exists(a)))
        {
            // Content the project already copies: copied straight over, without asking the build again.
            foreach (var asset in assets.Where(File.Exists))
            {
                CopyChangedContent(asset, _contentTargets[asset], compare: true);
            }
        }
        else if (projectChanged || assets.Count > 0)
        {
            var error = await RefreshReferencesAsync(projectChanged, assets, cancellationToken).ConfigureAwait(false);
            if (error is not null && paths.All(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) && libraryPaths.Count == 0)
            {
                return new SaveResult(Outcome.Error, error);
            }
            // The rest of the save still applies against the references the app has; the project file is tried
            // again when it changes.
            if (error is not null)
            {
                _pendingNotes.Add($"{error}; project file changes take effect once it succeeds");
            }
            else if (UseCompiler && projectChanged)
            {
                await RestartXamlCompilerWithCurrentReferencesAsync(cancellationToken).ConfigureAwait(false);
            }
            NoteNestedLibraries();
            foreach (var library in FindLibraries())
            {
                // Newly referenced, so just built from what is saved now: its code is current and all of its markup
                // is served as text, wherever the app's package layout stands. On the compiler pipeline its compiled
                // markup is served from the .pri deployed with it (request.Assemblies), as a build would merge it.
                libraryPaths.RemoveAll(p => string.Equals(NestedProjectDirectory(p), library, StringComparison.OrdinalIgnoreCase));
                if (!UseCompiler)
                {
                    libraryPaths.AddRange(Directory.EnumerateFiles(library, "*.xaml", SearchOption.AllDirectories).Where(IsWatched));
                }
            }
        }
        if (libraryPaths.FirstOrDefault(p => !p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && _librariesBuilt.TryGetValue(NestedProjectDirectory(p)!, out var built) &&
            (!File.Exists(p) || File.GetLastWriteTimeUtc(p) > built)) is { } libraryCode)
        {
            return new SaveResult(Outcome.Restart, $"{Relative(libraryCode)}: {_libraries[NestedProjectDirectory(libraryCode)!]} code changes take effect when the app restarts");
        }

        var deleted = new List<string>();
        // Files that exist first, so a moved file is matched to its document before the old path is seen as deleted.
        foreach (var path in paths.Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).OrderBy(p => File.Exists(p) ? 0 : 1))
        {
            var existing = Project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase))
                ?? (_moved.TryGetValue(path, out var movedId) ? Project.GetDocument(movedId) : null);
            if (existing is not null && _moved.ContainsValue(existing.Id) && !_moved.ContainsKey(path) && !File.Exists(path))
            {
                // The original path of a moved file: the document lives on under its new one.
                continue;
            }
            if (File.Exists(path))
            {
                var text = InjectInitializers(SourceText.From(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false), Encoding.UTF8, SourceHashAlgorithm.Sha256), path);
                _ghosts.Remove(path);
                if (existing is not null && string.Equals(existing.FilePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    // Back at its original path: any other path it was moved to is gone or a copy.
                    foreach (var alias in _moved.Where(m => m.Value == existing.Id).Select(m => m.Key).ToList())
                    {
                        _moved.Remove(alias);
                    }
                }
                if (existing is not null && (await existing.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString() == text.ToString())
                {
                    continue;
                }
                // A file moved with its folder (or renamed): the same code under a new path is the same document.
                var moved = existing is null ? await MovedDocumentAsync(path, text, cancellationToken).ConfigureAwait(false) : null;
                if (moved is not null)
                {
                    // Edit-and-continue matches documents to the running code by the path the build recorded, so the
                    // document keeps it and the new path is an alias.
                    foreach (var alias in _moved.Where(m => m.Value == moved.Id).Select(m => m.Key).ToList())
                    {
                        _moved.Remove(alias);
                    }
                    if (!string.Equals(moved.FilePath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        _moved[path] = moved.Id;
                    }
                    continue;
                }
                _solution = existing is null
                    ? _solution.AddDocument(DocumentId.CreateNewId(_projectId), Path.GetFileName(path), text, FoldersFor(path), path)
                    : _solution.WithDocumentText(existing.Id, text);
            }
            else if (existing is not null && !_ghosts.Contains(path))
            {
                if (_moved.Remove(path) && (File.Exists(existing.FilePath) || _moved.ContainsValue(existing.Id)))
                {
                    // A path the file was moved to, and it moved on (or back).
                    continue;
                }
                _solution = _solution.RemoveDocument(existing.Id);
                deleted.Add(path);
            }
        }
        // Files removed by an earlier save that didn't apply (it reported an error) are still deletions of this update.
        foreach (var document in _committed.GetProject(_projectId)!.Documents)
        {
            if (document.FilePath is { } removed && IsSource(removed) && !File.Exists(removed) && Project.GetDocument(document.Id) is null &&
                !_ghosts.Contains(removed) && !deleted.Contains(removed, StringComparer.OrdinalIgnoreCase))
            {
                deleted.Add(removed);
            }
        }

        Phase("files and references");
        if (UseCompiler && libraryPaths.FirstOrDefault(p => p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) is { } libraryXaml)
        {
            return new SaveResult(Outcome.Restart, $"{Relative(libraryXaml)}: XAML in a referenced library takes effect when the app restarts");
        }
        var resolver = UseCompiler ? null : new CompilationTypeResolver((await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!);
        var resources = new List<MarkupPayload>();
        var libraryTypes = new List<string>();
        foreach (var path in libraryPaths.Where(p => !UseCompiler && _libraries.ContainsKey(NestedProjectDirectory(p)!)))
        {
            var (payload, error) = await LibraryMarkupAsync(path, NestedProjectDirectory(path)!, resolver!, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return error;
            }
            if (payload is not null)
            {
                resources.Add(payload);
                if (payload.Kind == MarkupKind.Component)
                {
                    libraryTypes.Add(payload.Manifest.ClassName);
                }
            }
        }
        var savedXaml = paths.Where(p => p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
        if (UseCompiler && paths.FirstOrDefault(p => p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && !File.Exists(p) && _xamlInputs.Contains(p)) is { } removedXaml)
        {
            // A build fails while code still uses what the markup generated; that is the error to fix first.
            if (await DeletedMarkupUsageAsync(paths, cancellationToken).ConfigureAwait(false) is { } used)
            {
                return new SaveResult(Outcome.Error, used);
            }
            return new SaveResult(Outcome.Restart, $"{Relative(removedXaml)} was deleted: removing a XAML file takes effect when the app is rebuilt");
        }
        foreach (var path in UseCompiler ? [] : savedXaml)
        {
            var (payload, error, transformed) = await MarkupForAsync(path, UriFor(path), resolver!, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return error;
            }
            if (payload is not null)
            {
                resources.Add(payload);
                continue;
            }
            if (!_components.TryGetValue(path, out var component))
            {
                // Moved (a folder renamed): the class keeps the markup URI, compiled parts and fields it was built with.
                if (_components.Values.FirstOrDefault(c => c.ClassName is { } name && name == transformed!.ClassName && !File.Exists(c.XamlPath)) is { } moved)
                {
                    _components.Remove(moved.XamlPath);
                    moved.XamlPath = path;
                    component = _components[path] = moved;
                }
                else
                {
                    component = _components[path] = new XamlComponent(path, UriFor(path));
                }
            }
            component.Latest = transformed!;
            component.Pending = true;
        }

        // Checks that fail the save come before anything below records what this update adds (initializers, generated
        // code), so the save that fixes it still sends them.
        Phase("compilation and markup");
        if (deleted.Count > 0 && await DeletedTypeUsageAsync(deleted, cancellationToken).ConfigureAwait(false) is { } usage)
        {
            return new SaveResult(Outcome.Error, usage);
        }
        if (!UseCompiler && await DeletedMarkupUsageAsync(paths, cancellationToken).ConfigureAwait(false) is { } markupUsage)
        {
            return new SaveResult(Outcome.Error, markupUsage);
        }
        if (!UseCompiler && await RemovedMemberNamesAsync(cancellationToken).ConfigureAwait(false) is { Count: > 0 } removedNames &&
            await StaleBindingsAsync(removedNames, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            return new SaveResult(Outcome.Error, stale);
        }
        // Pending until an update that carries them is applied: a save that fails after this still leaves them to send.
        Phase("usage checks");
        // These look at the user's code only: a XAML-only save on the compiler pipeline has nothing for them.
        var userCodeChanged = !UseCompiler || deleted.Count > 0 || paths.Any(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && IsSource(p));
        foreach (var initializer in userCodeChanged ? await NewStaticInitializersAsync(cancellationToken).ConfigureAwait(false) : [])
        {
            if (!_pendingInitializers.Contains(initializer))
            {
                Log($"new static initializer {initializer}");
                _pendingInitializers.Add(initializer);
            }
        }
        Phase("static initializers");
        XamlCompiled? compiled = null;
        if (UseCompiler)
        {
            // The XAML compiler's own output: what a build of the saved files would compile, nothing rewritten.
            var codeChanged = paths.Any(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && IsSource(p));
            (compiled, var stopped) = await CompileXamlAsync(savedXaml, codeChanged, cancellationToken).ConfigureAwait(false);
            if (stopped is not null)
            {
                Discard();
                return stopped;
            }
        }
        // On the compiler pipeline only the constructor replay a reload in place runs (what the constructor does after
        // InitializeComponent: a frame it navigates, text it sets); the XAML compiler's output has the rest.
        var problems = await UpdateGeneratedCodeAsync(cancellationToken).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            return new SaveResult(Outcome.Error, string.Join(Environment.NewLine + "  ", problems));
        }
        if (compiled is { Classes.Count: > 0 } && await CodeBehindErrorsAsync(compiled.Classes, cancellationToken).ConfigureAwait(false) is { Count: > 0 } broken)
        {
            // Edit and continue reports errors only in the files a save changed: markup that removes what its unchanged
            // code-behind uses (a renamed x:Name) fails the build there (CS0103), so it fails here too.
            return new SaveResult(Outcome.Error, string.Join(Environment.NewLine + "  ", broken.Take(3).Select(Format)));
        }

        // Removed fields and properties stay declared up front: the compiler can't remove them from a loaded type, and
        // fails outright (rather than reporting a rude edit) for a removed partial property.
        Phase("generated code");
        if (userCodeChanged)
        {
            await KeepRemovedFieldsAsync(cancellationToken).ConfigureAwait(false);
            await EmitNewPartialPropertyInitializersAsync(cancellationToken).ConfigureAwait(false);
        }
        Phase("kept members");
        var result = await UpdatesAsync(cancellationToken).ConfigureAwait(false);
        if (result.Errors.Count == 0 && (result.RudeEdits.Count > 0 || result.NeedsRestart) && deleted.Count > 0)
        {
            // Types in a deleted file can't be unloaded. Once the rest of the code compiles without them,
            // keep them in the session so the running app is unaffected.
            Discard();
            foreach (var path in deleted)
            {
                foreach (var id in _committed.GetDocumentIdsWithFilePath(path))
                {
                    var old = _committed.GetDocument(id)!;
                    _solution = _solution.AddDocument(id, old.Name, await old.GetTextAsync(cancellationToken).ConfigureAwait(false), old.Folders, path);
                }
                _ghosts.Add(path);
            }
            result = await UpdatesAsync(cancellationToken).ConfigureAwait(false);
        }
        if (result.Errors.Count == 0 && result.RudeEdits.Any(d => d.Id is "ENC0020" or "ENC0033"))
        {
            // A field can't be removed from a loaded type. Renamed or deleted, it (or the property it backs) stays
            // declared (unused) for the session, attributes included, so what generators produce from it
            // ([ObservableProperty]) stays too.
            Discard();
            if (await KeepRemovedFieldsAsync(cancellationToken).ConfigureAwait(false))
            {
                result = await UpdatesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        if (result.Errors.Count > 0 && result.Errors.All(d => d.Id == "CS9346") && !await AddsExplicitImplementationToLoadedTypeAsync(cancellationToken).ConfigureAwait(false))
        {
            // CoreCLR can't add an explicit interface implementation to a loaded type, so it doesn't advertise the
            // capability. A type that is new in this update (for example an iterator's state machine) loads fine.
            Discard();
            _newTypesOnly = true;
            _enc.CapabilitiesChanged();
            try
            {
                result = await UpdatesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _newTypesOnly = false;
                _enc.CapabilitiesChanged();
            }
        }
        if (result.Errors.Count > 0 && result.Errors.All(d => d.Location == Location.None && d.GetMessage().Contains("restart", StringComparison.OrdinalIgnoreCase)))
        {
            // The code is valid but the runtime can't load it in place (for example a new iterator method).
            Discard();
            return new SaveResult(Outcome.Restart, Format(result.Errors[0]));
        }
        if (result.Errors.Count > 0)
        {
            Discard();
            return new SaveResult(Outcome.Error, string.Join(Environment.NewLine + "  ", result.Errors.Take(3).Select(Format)));
        }
        if (result.RudeEdits.Count > 0 || result.NeedsRestart)
        {
            Discard();
            var reason = result.RudeEdits.Count > 0 ? (UseCompiler ? RudeEditReason(result.RudeEdits) : Format(result.RudeEdits[0])) : "the change can't be applied to the running app";
            return new SaveResult(Outcome.Restart, reason);
        }
        if (_components.Values.FirstOrDefault(c => c.Pending && c.Latest is { Warnings.Count: > 0 }) is { } unsupported)
        {
            // Markup live reload can't express (no later save changes that): the rebuilt app compiles it.
            Discard();
            return new SaveResult(Outcome.Restart, $"{Relative(unsupported.XamlPath)} {unsupported.Latest!.Warnings[0]}");
        }

        var request = new HotReloadRequest { Op = "apply", Initializers = [.. _pendingInitializers], Assemblies = [.. _newAssemblies], Assets = [.. _changedAssets], LaunchItems = _launchItems };
        request.RefreshTypes.AddRange(libraryTypes);
        request.Deltas.AddRange(result.Deltas.Select(d => new DeltaPayload { ModuleId = d.ModuleId, Metadata = d.Metadata, IL = d.IL, Pdb = d.Pdb, UpdatedTypes = d.UpdatedTypes }));
        var applied = new List<string>();
        var waiting = new List<string>();
        Phase("edit and continue");
        var notes = await NotesAsync(compiled?.Classes ?? [], cancellationToken).ConfigureAwait(false);
        notes.AddRange(_pendingNotes);
        _pendingNotes.Clear();
        foreach (var component in _components.Values.Where(c => !c.Pending && c.MissingHandlers > 0 && c.Latest is not null))
        {
            // A handler the markup was waiting for now exists (on the page or, for {x:Bind ViewModel.Save}, elsewhere).
            component.Pending = (await MissingHandlersAsync(component, cancellationToken).ConfigureAwait(false)).Count() < component.MissingHandlers;
        }
        foreach (var component in _components.Values.Where(c => c.Pending && c.Latest is not null))
        {
            request.Markup.Add(new MarkupPayload { Uri = component.Uri, Xaml = component.Latest!.RuntimeXaml, Manifest = component.Latest.Manifest, LineMap = component.Latest.LineMap });
            _sources[component.Uri] = component.XamlPath;
            request.RefreshTypes.Add(component.ClassName!);
            waiting.AddRange(component.Latest.Functions.Where(f => component.InvalidFunctions.ContainsKey(f.Method))
                .Select(f => $"{Path.GetFileName(component.XamlPath)}: waiting for {f.Markup} to compile: {component.InvalidFunctions[f.Method]}"));
            var missing = (await MissingHandlersAsync(component, cancellationToken).ConfigureAwait(false)).ToList();
            component.MissingHandlers = missing.Count;
            waiting.AddRange(missing);
        }
        Phase("notes");
        await AddStructuralRefreshAsync(request, cancellationToken).ConfigureAwait(false);
        Phase("structural refresh");
        request.Markup.AddRange(resources);
        if (compiled is not null)
        {
            request.Markup.AddRange(compiled.Markup);
            request.RefreshTypes.AddRange(compiled.Classes.Where(c => !request.RefreshTypes.Contains(c)));
        }
        request.SavedFiles = batch;
        applied.AddRange(paths.Concat(assets).Concat(libraryPaths.Where(p => _libraries.ContainsKey(NestedProjectDirectory(p)!))).Select(Relative).Distinct(StringComparer.OrdinalIgnoreCase));

        if (request.Deltas.Count == 0 && request.Markup.Count == 0 && request.RefreshTypes.Count == 0 && request.RecreateTypes.Count == 0 && !request.RefreshAllPages && request.Assemblies.Count == 0 && request.Assets.Count == 0)
        {
            Commit();
            // A project file edit (for example a new package) took effect even though no code changed yet.
            var projectEdit = paths.Any(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) || assets.Count > 0;
            return new SaveResult(projectEdit ? Outcome.Applied : Outcome.Unchanged, Describe(applied.Count > 0 ? applied : batch, notes));
        }

        HotReloadResponse response;
        try
        {
            response = await _agent.SendAsync(request, cancellationToken).ConfigureAwait(false);
            Phase("app applied");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Discard();
            return new SaveResult(Outcome.Restart, "lost the connection to the app: " + ex.Message);
        }
        if (!response.Ok)
        {
            Discard();
            return new SaveResult(Outcome.Restart, response.Error ?? "the app could not apply the update");
        }
        Commit();
        _newAssemblies.Clear();
        _changedAssets.Clear();
        _pendingInitializers.Clear();
        if (response.Restarts.Count > 0)
        {
            // Applied, but the page update strategy found the app won't show it as a restart would.
            return new SaveResult(Outcome.Restart, string.Join(Environment.NewLine + "  ", response.Restarts.Select(SourcePaths)));
        }
        if (response.Errors.Count > 0)
        {
            // Code changes are in; the markup that failed is sent again with the next save. Files of this save that
            // applied are reported as such, with a line for each that still has an error (App.xaml broken by an
            // earlier save while a page is edited).
            var errors = response.Errors.Select(SourcePaths).ToList();
            bool Mentions(string error, string file) => error.StartsWith(file, StringComparison.OrdinalIgnoreCase) &&
                error.Length > file.Length && error[file.Length] is '(' or ':' or ' ';
            var failing = applied.Where(file => errors.Any(e => Mentions(e, file))).ToList();
            var fine = applied.Except(failing, StringComparer.OrdinalIgnoreCase).ToList();
            if (failing.Count == 0 || fine.Count == 0 || errors.Any(e => !failing.Any(file => Mentions(e, file))))
            {
                return new SaveResult(Outcome.Error, string.Join(Environment.NewLine + "  ", errors));
            }
            foreach (var component in _components.Values.Where(c => !failing.Contains(Relative(c.XamlPath), StringComparer.OrdinalIgnoreCase)))
            {
                component.Pending = false;
            }
            waiting.AddRange(response.Messages.Select(SourcePaths));
            var partial = Describe(fine, notes.Concat(response.Notes)) + string.Concat(failing.Select(file =>
                Environment.NewLine + $"  {file} still has an error: " + string.Join(" ", errors.Where(e => Mentions(e, file)))));
            return new SaveResult(waiting.Count > 0 ? Outcome.Waiting : Outcome.Applied,
                partial + string.Concat(waiting.Distinct().Select(w => Environment.NewLine + "  " + w)),
                failing.Select(file => Path.GetFullPath(Path.Combine(_projectDirectory, file))).ToList());
        }
        foreach (var component in _components.Values)
        {
            component.Pending = false;
        }
        waiting.AddRange(response.Messages.Select(SourcePaths));
        var detail = Describe(applied.Count > 0 ? applied : batch, notes.Concat(response.Notes));
        return waiting.Count > 0
            ? new SaveResult(Outcome.Waiting, detail + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", waiting.Distinct()))
            : new SaveResult(Outcome.Applied, detail);
    }

    /// <summary>The attributes on each App.xaml's root, as last seen.</summary>
    private readonly Dictionary<string, string> _applicationSettings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pendingNotes = [];
    private readonly HashSet<string> _notedSkips = new(StringComparer.Ordinal);

    /// <summary>"RequestedTheme=Light" lines for the root element's attributes, namespace declarations and directives aside.</summary>
    private static string ApplicationSettings(string xaml)
    {
        try
        {
            var root = XDocument.Parse(xaml).Root!;
            return string.Join("\n", root.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None)
                .Select(a => $"{a.Name.LocalName}={a.Value}").Order(StringComparer.Ordinal));
        }
        catch (XmlException)
        {
            return "";
        }
    }

    private static string ChangedSettings(string before, string after)
    {
        var old = before.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var now = after.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return string.Join(", ", old.Except(now).Concat(now.Except(old)).Select(s => s.Split('=')[0]).Distinct(StringComparer.Ordinal));
    }

    /// <summary>Statements in replayed constructors already noted as starting something that keeps running.</summary>
    private readonly HashSet<string> _notedStarts = new(StringComparer.Ordinal);

    /// <summary>
    /// What an "applied" doesn't tell: changes to code that only runs at startup (App.OnLaunched, the App
    /// constructor), and constructors replayed on reload that start timers or threads, which then run once more.
    /// </summary>
    /// <param name="reloaded">Classes whose markup the XAML compiler rebuilt this save (their constructors replay).</param>
    private async Task<List<string>> NotesAsync(IReadOnlyCollection<string> reloaded, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var oldProject = _committed.GetProject(_projectId)!;
        var compilation = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        var changes = Project.GetChanges(oldProject);
        foreach (var id in changes.GetChangedDocuments())
        {
            var document = _solution.GetDocument(id)!;
            if (document.FilePath is not { } path || !IsSource(path))
            {
                continue;
            }
            var tree = (await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false))!;
            var oldRoot = await oldProject.GetDocument(id)!.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = compilation.GetSemanticModel(tree);
            foreach (var type in tree.GetRoot(cancellationToken).DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(type, cancellationToken);
                var isApp = false;
                for (var t = symbol?.BaseType; t is not null; t = t.BaseType)
                {
                    isApp |= t.ToDisplayString() == "Microsoft.UI.Xaml.Application";
                }
                var oldType = oldRoot?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.ValueText == type.Identifier.ValueText);
                if (!isApp)
                {
                    // A component whose constructor takes arguments replays it without the statements that use them: a
                    // change to one of those takes effect at restart.
                    if (symbol is not null && _components.Values.Any(c => c.ClassName == symbol.ToDisplayString()) &&
                        GeneratedPartial.ReplayedConstructor([type]) is { Body: { } body } constructor)
                    {
                        var old = oldType is null ? null : GeneratedPartial.ReplayedConstructor([oldType])?.Body;
                        foreach (var index in GeneratedPartial.ArgumentStatements(constructor).Order())
                        {
                            var statement = body.Statements[index];
                            if (old is null || !old.Statements.Any(s => s.IsEquivalentTo(statement)))
                            {
                                notes.Add($"{Relative(statement.SyntaxTree.FilePath)}({statement.GetLocation().GetLineSpan().StartLinePosition.Line + 1}): " +
                                    $"`{statement.ToString().Split('\n')[0].Trim()}` uses {type.Identifier.ValueText}'s constructor arguments, which a reload doesn't have: this change takes effect when the app restarts");
                            }
                        }
                    }
                    continue;
                }
                foreach (var member in type.Members.Where(m => m is ConstructorDeclarationSyntax || (m is MethodDeclarationSyntax method && method.Identifier.ValueText == "OnLaunched")))
                {
                    var name = member is MethodDeclarationSyntax method ? method.Identifier.ValueText : type.Identifier.ValueText;
                    var before = oldType?.Members.FirstOrDefault(m => m.Kind() == member.Kind() && (m is MethodDeclarationSyntax o ? o.Identifier.ValueText == name : true));
                    if (before is null || !before.IsEquivalentTo(member))
                    {
                        notes.Add($"{type.Identifier.ValueText}.{name} only runs at startup: this change takes effect when the app restarts");
                    }
                }
            }
        }
        // Static initializers and constructors already ran: a changed one, in a file this save changed or in a
        // generator's output (a list built from attributes), takes effect at the next start.
        var oldTrees = (await oldProject.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!.SyntaxTrees
            .GroupBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!oldTrees.TryGetValue(tree.FilePath, out var old) || ReferenceEquals(old, tree) || old.GetText(cancellationToken).ContentEquals(tree.GetText(cancellationToken)))
            {
                continue;
            }
            var before = StaticInitializers(old.GetRoot(cancellationToken));
            foreach (var (name, initializer) in StaticInitializers(tree.GetRoot(cancellationToken)))
            {
                if (before.TryGetValue(name, out var was) && !was.IsEquivalentTo(initializer, topLevel: false))
                {
                    notes.Add(name.EndsWith(".cctor", StringComparison.Ordinal)
                        ? $"{name[..^6]}'s static constructor ran when the app started: this change takes effect when the app restarts"
                        : $"{name} is set once, when the app starts: this change takes effect when the app restarts");
                }
            }
        }
        foreach (var component in _components.Values.Where(c => (c.Pending || reloaded.Contains(c.ClassName ?? "")) && c.ClassName is not null && !c.ReplayDisabled))
        {
            if (compilation.GetTypeByMetadataName(component.ClassName!) is not { } type)
            {
                continue;
            }
            // The replayed constructor (see GeneratedPartial), without the statements that use its arguments.
            var parts = type.DeclaringSyntaxReferences.Select(r => r.GetSyntax(cancellationToken)).OfType<ClassDeclarationSyntax>().ToList();
            foreach (var constructor in GeneratedPartial.ReplayedConstructor(parts) is { } replayed ? [replayed] : Array.Empty<ConstructorDeclarationSyntax>())
            {
                var model = compilation.GetSemanticModel(constructor.SyntaxTree);
                var skipped = GeneratedPartial.ArgumentStatements(constructor).Select(i => constructor.Body!.Statements[i]).ToList();
                foreach (var invocation in constructor.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => !skipped.Any(s => s.Span.Contains(call.Span))))
                {
                    if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
                    {
                        continue;
                    }
                    var owner = method.ContainingType?.ToDisplayString() ?? "";
                    var starts = (method.Name == "Start" && (owner.EndsWith("Timer", StringComparison.Ordinal) || owner == "System.Threading.Thread")) ||
                        (method.Name is "Run" or "StartNew" && owner.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal));
                    // The call as one line (a lambda body can span many), with where it is.
                    var text = invocation.Expression.ToString() + "(" + (invocation.ArgumentList.Arguments.Count > 0 ? "..." : "") + ")";
                    var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    if (starts && _notedStarts.Add($"{component.ClassName}|{invocation}"))
                    {
                        notes.Add($"{Relative(invocation.SyntaxTree.FilePath)}({line}): {type.Name}'s constructor runs again on each reload, so {text} there starts another each time it runs");
                    }
                }
            }
        }
        return notes;
    }

    /// <summary>"Type.Field" (or "Type.cctor") to the initializer (or body) of each static member in a file.</summary>
    private static Dictionary<string, SyntaxNode> StaticInitializers(SyntaxNode root)
    {
        var result = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var name = string.Join(".", type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText));
            foreach (var member in type.Members.Where(m => m.Modifiers.Any(SyntaxKind.StaticKeyword) && !m.Modifiers.Any(SyntaxKind.ConstKeyword)))
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field:
                        foreach (var variable in field.Declaration.Variables.Where(v => v.Initializer is not null))
                        {
                            result.TryAdd($"{name}.{variable.Identifier.ValueText}", variable.Initializer!.Value);
                        }
                        break;
                    case PropertyDeclarationSyntax { Initializer: { } initializer } property:
                        result.TryAdd($"{name}.{property.Identifier.ValueText}", initializer.Value);
                        break;
                    case ConstructorDeclarationSyntax { Body: { } body }:
                        result.TryAdd($"{name}.cctor", body);
                        break;
                }
            }
        }
        return result;
    }

    /// <summary>The update for the current solution; a compiler crash becomes a <see cref="CompilerCrashException"/>.</summary>
    /// <summary>Compiler errors in the user's own files of these classes (their code-behind), not the generated ones.</summary>
    private async Task<List<Diagnostic>> CodeBehindErrorsAsync(IEnumerable<string> classes, CancellationToken cancellationToken)
    {
        var compilation = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        var obj = Path.Combine(_projectDirectory, "obj") + Path.DirectorySeparatorChar;
        var trees = classes.Select(compilation.GetTypeByMetadataName).OfType<INamedTypeSymbol>()
            .SelectMany(t => t.DeclaringSyntaxReferences.Select(r => r.SyntaxTree))
            .Where(t => IsSource(t.FilePath) && !t.FilePath.StartsWith(obj, StringComparison.OrdinalIgnoreCase)).Distinct();
        return [.. trees.SelectMany(t => compilation.GetSemanticModel(t).GetDiagnostics(cancellationToken: cancellationToken)).Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    /// <summary>Whether the only changes since the last update are comments, whitespace and directives (no code).</summary>
    private async Task<bool> OnlyTriviaChangedAsync(CancellationToken cancellationToken)
    {
        foreach (var project in _solution.GetChanges(_committed).GetProjectChanges())
        {
            if (project.GetAddedDocuments().Any() || project.GetRemovedDocuments().Any() || project.GetAddedMetadataReferences().Any() ||
                project.GetRemovedMetadataReferences().Any() || project.OldProject.ParseOptions != project.NewProject.ParseOptions ||
                project.OldProject.CompilationOptions != project.NewProject.CompilationOptions ||
                // A generator's input (an additional file, .editorconfig) or the generators themselves: its output can change.
                project.GetChangedAdditionalDocuments().Any() || project.GetAddedAdditionalDocuments().Any() || project.GetRemovedAdditionalDocuments().Any() ||
                project.GetChangedAnalyzerConfigDocuments().Any() || project.GetAddedAnalyzerConfigDocuments().Any() || project.GetRemovedAnalyzerConfigDocuments().Any() ||
                project.GetAddedAnalyzerReferences().Any() || project.GetRemovedAnalyzerReferences().Any())
            {
                return false;
            }
            foreach (var id in project.GetChangedDocuments())
            {
                var before = await project.OldProject.GetDocument(id)!.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                var after = await project.NewProject.GetDocument(id)!.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                if (before is null || after is null || !before.IsEquivalentTo(after, topLevel: false))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private async Task<EncResult> UpdatesAsync(CancellationToken cancellationToken)
    {
        if (!_solution.GetChanges(_committed).GetProjectChanges().Any() || await OnlyTriviaChangedAsync(cancellationToken).ConfigureAwait(false))
        {
            // Nothing to compile. Edit and continue would still take this solution as its last one, without a baseline
            // from its compilation, and the next update of a method added during the session then fails
            // (NullReferenceException in DefinitionMap.GetPreviousMethodHandle). It sees these edits with the next real one.
            return new EncResult("None", [], [], [], false);
        }
        try
        {
            return await _enc.GetUpdatesAsync(_solution, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_LOG") is { Length: > 0 } log)
            {
                var changes = Project.GetChanges(_committed.GetProject(_projectId)!);
                foreach (var id in changes.GetChangedDocuments().Concat(changes.GetAddedDocuments()))
                {
                    var old = _committed.GetDocument(id) is { } o ? (await o.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString() : "(added)";
                    File.AppendAllText(log + ".helper", $"--- {_solution.GetDocument(id)!.FilePath}{Environment.NewLine}{await _solution.GetDocument(id)!.GetTextAsync(cancellationToken).ConfigureAwait(false)}{Environment.NewLine}=== was{Environment.NewLine}{old}{Environment.NewLine}");
                }
            }
            Discard();
            throw new CompilerCrashException(ex);
        }
    }

    /// <summary>"A.xaml, B.cs" plus a "note:" line for each note. Files of a library the app doesn't reference say so.</summary>
    private string Describe(IEnumerable<string> files, IEnumerable<string> notes)
    {
        var list = files.ToList();
        var outside = list.Where(f => NestedProjectDirectory(Path.Combine(_projectDirectory, f)) is { } library && !_libraries.ContainsKey(library)).ToList();
        var text = string.Join(", ", SummarizeMoves(list));
        if (outside.Count > 0 && outside.Count == list.Count)
        {
            text += " (in a project the app doesn't reference yet)";
        }
        return text + string.Concat(notes.Select(n => Environment.NewLine + "  note: " + n));
    }

    /// <summary>
    /// A renamed folder shows up as every file at its old path and its new one: those become one "Pages\ -> Views\ (8 files)"
    /// entry. Anything else is listed as is.
    /// </summary>
    private List<string> SummarizeMoves(List<string> list)
    {
        bool Exists(string file) => File.Exists(Path.Combine(_projectDirectory, file));
        var moves = new Dictionary<(string From, string To), List<string>>();
        foreach (var gone in list.Where(f => !Exists(f)))
        {
            var oldParts = gone.Split(Path.DirectorySeparatorChar);
            foreach (var present in list.Where(f => Exists(f) && string.Equals(Path.GetFileName(f), Path.GetFileName(gone), StringComparison.OrdinalIgnoreCase)))
            {
                var newParts = present.Split(Path.DirectorySeparatorChar);
                var same = 0;
                while (same < Math.Min(oldParts.Length, newParts.Length) &&
                    string.Equals(oldParts[^(same + 1)], newParts[^(same + 1)], StringComparison.OrdinalIgnoreCase))
                {
                    same++;
                }
                if (same < oldParts.Length && same < newParts.Length)
                {
                    var key = (string.Join(Path.DirectorySeparatorChar, oldParts[..^same]) + Path.DirectorySeparatorChar,
                        string.Join(Path.DirectorySeparatorChar, newParts[..^same]) + Path.DirectorySeparatorChar);
                    (moves.TryGetValue(key, out var pair) ? pair : moves[key] = []).AddRange([gone, present]);
                    break;
                }
            }
        }
        var result = new List<string>();
        var summarized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((from, to), moved) in moves.Where(m => m.Value.Count >= 4))
        {
            result.Add($"{from} -> {to} ({moved.Count / 2} files)");
            summarized.UnionWith(moved);
        }
        result.AddRange(list.Where(f => !summarized.Contains(f)));
        return result;
    }

    /// <summary>"Type|Method" initializers for static fields added since the last update the app received.</summary>
    private readonly List<string> _pendingInitializers = [];

    /// <summary>
    /// Copies a content file next to the running app when it isn't there yet or (<paramref name="compare"/>) its bytes
    /// differ; images showing it reload.
    /// </summary>
    private void CopyChangedContent(string source, string target, bool compare)
    {
        var destination = Path.Combine(_baseDirectory, target);
        if (_baseDirectory.Length == 0 || !File.Exists(source) || (File.Exists(destination) && (!compare ||
            (new FileInfo(destination).Length == new FileInfo(source).Length && File.ReadAllBytes(destination).AsSpan().SequenceEqual(File.ReadAllBytes(source))))))
        {
            return;
        }
        if (CopyContent(source, destination))
        {
            _changedAssets.Add(target.Replace('\\', '/'));
            if (compare && !ImageExtensions.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase))
            {
                _pendingNotes.Add($"{Relative(source)} is copied next to the app; if the app reads it only when it starts, this change shows after a restart");
            }
        }
        else
        {
            _pendingNotes.Add($"{Relative(source)} could not be copied next to the running app; it takes effect when the app restarts");
        }
    }

    /// <summary>Content files replaced since the last update the app received.</summary>
    private readonly List<string> _changedAssets = [];

    /// <summary>
    /// Copies a content file over the one the app uses. The app may hold that file open (an image it shows), which
    /// blocks overwriting it but not renaming it, so the old file is moved aside first.
    /// </summary>
    private static bool CopyContent(string source, string destination)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                File.Copy(source, destination, overwrite: true);
                return true;
            }
            catch (IOException) when (File.Exists(destination))
            {
                var aside = destination + $".{Guid.NewGuid():N}.old";
                File.Move(destination, aside);
                File.Copy(source, destination);
                try
                {
                    File.Delete(aside);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still open; it goes with the next clean build.
                }
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Markup URIs the app knows, by the file they come from.</summary>
    private readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"ms-appx:///Pages/Home.xaml(13,40): ..." from the app becomes "Pages\Home.xaml(13,40): ...".</summary>
    private string SourcePaths(string message)
    {
        foreach (var (uri, path) in _sources.OrderByDescending(s => s.Key.Length))
        {
            message = message.Replace(uri, Relative(path), StringComparison.OrdinalIgnoreCase);
        }
        return message;
    }

    private void Commit()
    {
        try
        {
            _enc.Commit();
        }
        catch (TargetInvocationException)
        {
            // Nothing pending (for example a XAML-only save).
        }
        _committed = _solution;
    }

    private void Discard()
    {
        try
        {
            _enc.Discard();
        }
        catch (TargetInvocationException)
        {
            // Nothing pending.
        }
    }

    private static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private IEnumerable<string> FoldersFor(string path) =>
        Path.GetRelativePath(_projectDirectory, Path.GetDirectoryName(path)!).Split(Path.DirectorySeparatorChar).Where(p => p is not ("." or ""));

    /// <summary>
    /// How a path is shown: relative to the app's folder, or for a file of a project next to it (a solution's library),
    /// relative to the folder the two share ("SlnLib\Badge.xaml" rather than "..\SlnLib\Badge.xaml").
    /// </summary>
    private string Relative(string path)
    {
        var relative = Path.GetRelativePath(_projectDirectory, path);
        if (!relative.StartsWith("..", StringComparison.Ordinal))
        {
            return relative;
        }
        var shared = Path.GetDirectoryName(_projectDirectory.TrimEnd(Path.DirectorySeparatorChar));
        while (shared is not null && Path.GetRelativePath(shared, path).StartsWith("..", StringComparison.Ordinal))
        {
            shared = Path.GetDirectoryName(shared);
        }
        return shared is null ? path : Path.GetRelativePath(shared, path);
    }

    private static readonly string[] BuildFiles = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

    /// <summary>The folders above the app (and its libraries) whose Directory.Build files MSBuild imports.</summary>
    public IEnumerable<string> BuildFileFolders() => WatchRoots.SelectMany(root =>
    {
        var folders = new List<string>();
        for (var directory = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            folders.Add(directory);
        }
        return folders;
    }).Distinct(StringComparer.OrdinalIgnoreCase).Where(d => !IsWatched(Path.Combine(d, BuildFiles[0])));

    /// <summary>A Directory.Build.props/.targets or Directory.Packages.props the app's build (or a library's) imports.</summary>
    public bool IsBuildFile(string path) =>
        BuildFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) &&
        WatchRoots.Any(root => Path.GetDirectoryName(path) is { } folder &&
            (IsWatched(path) || !Path.GetRelativePath(folder, root).StartsWith("..", StringComparison.Ordinal)));

    private string Format(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        // Relative to the project, like the build prints it (generated code by name), so terminals can link it.
        var path = span.Path is { Length: > 0 } p && IsSource(p) ? Relative(p) : Path.GetFileName(span.Path ?? "");
        var file = path.Length > 0 ? $"{path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): " : "";
        return $"{file}{diagnostic.Id}: {diagnostic.GetMessage()}";
    }

    /// <summary>A file that isn't well-formed XML yet: where, what, and what usually causes it.</summary>
    private string XmlError(string path, XmlException ex)
    {
        var message = ex.Message;
        var position = message.IndexOf(" Line ", StringComparison.Ordinal);
        if (position > 0)
        {
            message = message[..position].TrimEnd('.', ' ') + ".";
        }
        return $"{Relative(path)}({ex.LineNumber},{ex.LinePosition}): {message} The file isn't well-formed XML: check for an unclosed quote, tag or comment before this point.";
    }

    private string GeneratedPath(string className) => Path.Combine(_projectDirectory, "obj", "winapp-hotreload", className + ".g.cs");

    /// <summary>
    /// Declares fields renamed or deleted since the last update again, in the user's own file, so the update only adds
    /// fields (a loaded type can't lose one). Attributes come along, so what generators produce from the field
    /// ([ObservableProperty]) stays too, and with it the partial methods the user implemented for it.
    /// </summary>
    private async Task<bool> KeepRemovedFieldsAsync(CancellationToken cancellationToken)
    {
        var before = (await _committed.GetProject(_projectId)!.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        var after = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var oldProject = _committed.GetProject(_projectId)!;
        var changed = Project.GetChanges(oldProject).GetChangedDocuments().Select(id => oldProject.GetDocument(id)?.FilePath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = new List<(string Type, string Key, string File, string Attributes, string Declaration, string Plain, int Index, string? After)>();
        foreach (var tree in before.SyntaxTrees.Where(t => changed.Contains(t.FilePath) && IsSource(t.FilePath)))
        {
            var model = before.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot(cancellationToken).DescendantNodes().OfType<TypeDeclarationSyntax>().Where(d => d.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax))
            {
                var name = model.GetDeclaredSymbol(declaration, cancellationToken) is { IsGenericType: false } oldType ? oldType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "") : null;
                if (name is null || after.GetTypeByMetadataName(name) is not { } newType)
                {
                    continue;
                }
                foreach (var variable in declaration.Members.OfType<FieldDeclarationSyntax>().Where(f => !f.Modifiers.Any(SyntaxKind.ConstKeyword)).SelectMany(f => f.Declaration.Variables))
                {
                    if (model.GetDeclaredSymbol(variable, cancellationToken) is not IFieldSymbol field || !newType.GetMembers(field.Name).IsEmpty || !File.Exists(tree.FilePath))
                    {
                        continue;
                    }
                    var attributes = field.GetAttributes().Where(a => a.AttributeClass is not null && a.ApplicationSyntaxReference is not null).Select(a =>
                        $"[{a.AttributeClass!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}{((AttributeSyntax)a.ApplicationSyntaxReference!.GetSyntax(cancellationToken)).ArgumentList}]");
                    var text = $"{SyntaxFacts.GetText(field.DeclaredAccessibility)} {(field.IsStatic ? "static " : "")}{(field.IsReadOnly ? "readonly " : "")}{(field.IsVolatile ? "volatile " : "")}" +
                        $"{field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {field.Name};";
                    kept.Add((name, "field:" + field.Name, tree.FilePath, string.Concat(attributes), text, text,
                        declaration.Members.IndexOf((MemberDeclarationSyntax)variable.Parent!.Parent!), Preceding(declaration, (MemberDeclarationSyntax)variable.Parent!.Parent!)));
                    files.Add(tree.FilePath);
                }
                // Auto-properties and partial property declarations ([ObservableProperty] public partial string Name { get; set; })
                // have a backing field too, and a generator's cached members for them; they stay declared the same way.
                foreach (var property in declaration.Members.OfType<PropertyDeclarationSyntax>())
                {
                    if (property.AccessorList is not { } accessors || accessors.Accessors.Any(a => a.Body is not null || a.ExpressionBody is not null) ||
                        property.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword) || m.IsKind(SyntaxKind.ExternKeyword)) ||
                        !newType.GetMembers(property.Identifier.ValueText).IsEmpty || !File.Exists(tree.FilePath))
                    {
                        continue;
                    }
                    // As written (line breaks included), so the members after it keep their lines.
                    var full = property.WithInitializer(null).WithSemicolonToken(SyntaxFactory.MissingToken(SyntaxKind.SemicolonToken));
                    var bare = property.WithAttributeLists(default).WithInitializer(null).WithSemicolonToken(default).WithoutTrivia();
                    kept.Add((name, "property:" + property.Identifier.ValueText, tree.FilePath, "", full.ToFullString(),
                        bare.WithModifiers(SyntaxFactory.TokenList(bare.Modifiers.Where(m => !m.IsKind(SyntaxKind.PartialKeyword)))).ToFullString(),
                        declaration.Members.IndexOf(property), Preceding(declaration, property)));
                    files.Add(tree.FilePath);
                }
            }
        }
        if (kept.Count == 0)
        {
            return false;
        }
        foreach (var withAttributes in new[] { true, false })
        {
            foreach (var (type, key, file, attributes, declaration, plain, index, preceding) in kept)
            {
                // Without its attributes a partial property has no implementation, so it becomes a plain auto-property.
                Injected(type)[key] = new InjectedMember(key[(key.IndexOf(':') + 1)..], key.StartsWith("field:", StringComparison.Ordinal) ? -1 : -2,
                    withAttributes ? attributes + declaration : plain, file, index, preceding);
            }
            await ReinjectAsync(files, cancellationToken).ConfigureAwait(false);
            var compilation = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
            if (!withAttributes || !compilation.SyntaxTrees.Where(t => files.Contains(t.FilePath))
                .Any(t => compilation.GetSemanticModel(t).GetDiagnostics(cancellationToken: cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error)))
            {
                break;
            }
            // An attribute refers to something renamed with it; the plain field still keeps the type compatible.
        }

        var current = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        foreach (var type in kept.Select(k => k.Type).Distinct())
        {
            // The generator declares partial methods for the kept field again (OnUserNameChanged); the implementation the
            // user renamed with the field stays as it was, so the method is unchanged rather than removed.
            if (current.GetTypeByMetadataName(type) is not { } newType || before.GetTypeByMetadataName(type) is not { } old)
            {
                continue;
            }
            foreach (var method in newType.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsPartialDefinition && m.PartialImplementationPart is null))
            {
                if (old.GetMembers(method.Name).OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Select(p => p.Type).SequenceEqual(method.Parameters.Select(p => p.Type), SymbolEqualityComparer.Default))
                        ?.PartialImplementationPart?.DeclaringSyntaxReferences.FirstOrDefault() is { } reference &&
                    reference.GetSyntax(cancellationToken) is MethodDeclarationSyntax implementation && File.Exists(reference.SyntaxTree.FilePath))
                {
                    Injected(type)[$"method:{method.Name}/{method.Parameters.Length}"] = new InjectedMember(method.Name, method.Parameters.Length, implementation.WithoutTrivia().ToFullString(),
                        reference.SyntaxTree.FilePath, (implementation.Parent as TypeDeclarationSyntax)?.Members.IndexOf(implementation) ?? -1);
                    files.Add(reference.SyntaxTree.FilePath);
                }
            }
        }
        await ReinjectAsync(files, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The compiler leaves a loaded type's constructors as they were when only a partial property with an initializer
    /// is added ([ObservableProperty] public partial string Weather { get; set; } = "sunny";), so new instances would
    /// start without it. A session-only initialized field added with it makes the constructors update, initializer included.
    /// </summary>
    private async Task EmitNewPartialPropertyInitializersAsync(CancellationToken cancellationToken)
    {
        var before = (await _committed.GetProject(_projectId)!.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        var oldProject = _committed.GetProject(_projectId)!;
        var changed = Project.GetChanges(oldProject).GetChangedDocuments().Select(id => Project.GetDocument(id)?.FilePath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in Project.Documents.Where(d => d.FilePath is { } p && changed.Contains(p) && IsSource(p)))
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            foreach (var declaration in root!.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(d => d.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax))
            {
                var name = GeneratedPartial.NamespaceOf(declaration) is { Length: > 0 } ns ? ns + "." + declaration.Identifier.ValueText : declaration.Identifier.ValueText;
                if (before.GetTypeByMetadataName(name) is not { } oldType)
                {
                    continue;
                }
                foreach (var property in declaration.Members.OfType<PropertyDeclarationSyntax>()
                    .Where(p => p.Initializer is not null && p.Modifiers.Any(SyntaxKind.PartialKeyword) && oldType.GetMembers(p.Identifier.ValueText).IsEmpty))
                {
                    var field = "__winappInitialize" + property.Identifier.ValueText;
                    if (Injected(name).TryAdd("ctor:" + property.Identifier.ValueText, new InjectedMember(field, -1, $"private bool {field} = true;", document.FilePath!)))
                    {
                        files.Add(document.FilePath!);
                    }
                }
            }
        }
        await ReinjectAsync(files, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-reads files from disk and adds the session's members to them.</summary>
    private async Task ReinjectAsync(IEnumerable<string> files, CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            var text = InjectInitializers(SourceText.From(await ReadSharedAsync(file, cancellationToken).ConfigureAwait(false), Encoding.UTF8, SourceHashAlgorithm.Sha256), file);
            foreach (var id in _solution.GetDocumentIdsWithFilePath(file))
            {
                _solution = _solution.WithDocumentText(id, text);
            }
        }
    }

    /// <summary>
    /// The document a new file was moved from: one whose file is gone (or deleted in this batch) and whose text is the
    /// same. A moved file with edits is a new document.
    /// </summary>
    private async Task<Document?> MovedDocumentAsync(string path, SourceText text, CancellationToken cancellationToken)
    {
        // Back at a path a document has: moved back.
        if (Project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase)) is { } original &&
            _moved.ContainsValue(original.Id))
        {
            return original;
        }
        foreach (var document in Project.Documents.Where(d => d.FilePath is { } p && IsSource(p) && (!File.Exists(p) || _moved.ContainsValue(d.Id)) &&
            string.Equals(Path.GetFileName(p), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
        {
            if ((await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString() == text.ToString())
            {
                return document;
            }
        }
        return null;
    }

    /// <summary>Keeps each component's generated partial in step with its XAML and constructor, then validates names.</summary>
    private async Task<List<string>> UpdateGeneratedCodeAsync(CancellationToken cancellationToken)
    {
        var userTrees = new List<SyntaxTree>();
        foreach (var document in Project.Documents.Where(d => d.FilePath is { } path && IsSource(path)))
        {
            userTrees.Add((await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false))!);
        }
        var classes = IndexClasses(userTrees);
        foreach (var component in _components.Values)
        {
            // Re-check every function each save: a C# edit may have made one compile.
            component.InvalidFunctions.Clear();
            // Likewise the replay: the constructor may only have referred to a type that didn't exist yet.
            component.ReplayDisabled = false;
            // Statements only a constructor can run stay left out while the constructor is unchanged (finding them
            // again costs another compilation of the whole project).
            var constructor = component.ClassName is null ? null : GeneratedPartial.ReplayedConstructor(classes.GetValueOrDefault(component.ClassName) ?? [])?.ToString();
            if (constructor != component.SkippedFor)
            {
                component.SkippedStatements.Clear();
                component.SkippedFor = constructor;
            }
        }
        // Worked out on a copy: trying variants (with and without the constructor replay) must not leave a document that
        // is textually unchanged but a new version, which the compiler then emits as an edit of every method in it.
        var working = _solution;
        // Only partials that can have a new problem are checked (binding every one is seconds on a large app): one whose
        // text changes, one whose class changed, and, after a code edit, one with x:Bind functions (their code may call
        // what changed).
        var changedFiles = Project.GetChanges(_committed.GetProject(_projectId)!).GetChangedDocuments().Concat(Project.GetChanges(_committed.GetProject(_projectId)!).GetAddedDocuments())
            .Select(id => _solution.GetDocument(id)?.FilePath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var codeChanged = changedFiles.Any(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && IsSource(p));
        var check = new HashSet<string>(StringComparer.Ordinal);
        Phase("generated: index");
        for (var pass = 0; pass < 8; pass++)
        {
            foreach (var component in _components.Values.Where(c => c.ClassName is not null))
            {
                var parts = classes.GetValueOrDefault(component.ClassName!) ?? [];
                var text = GeneratedPartial.Create(component, parts, !component.ReplayDisabled, markup: !UseCompiler);
                var source = SourceText.From(text, Encoding.UTF8, SourceHashAlgorithm.Sha256);
                if ((pass == 0 && (parts.Any(p => changedFiles.Contains(p.SyntaxTree.FilePath)) || (codeChanged && component.Latest?.Functions.Count > 0))) ||
                    !_generated.TryGetValue(component.ClassName!, out var committedId) ||
                    _solution.GetDocument(committedId) is not { } committed || (await committed.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString() != text)
                {
                    check.Add(component.ClassName!);
                }
                if (_generated.TryGetValue(component.ClassName!, out var id))
                {
                    if ((await working.GetDocument(id)!.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString() != text)
                    {
                        working = working.WithDocumentText(id, source);
                    }
                }
                else
                {
                    id = DocumentId.CreateNewId(_projectId);
                    var path = GeneratedPath(component.ClassName!);
                    working = working.AddDocument(id, Path.GetFileName(path), source, filePath: path);
                    _generated[component.ClassName!] = id;
                }
            }

            Phase("generated: partials");
            var compilation = (await working.GetProject(_projectId)!.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
            Phase("generated: compilation");
            var retry = false;
            foreach (var component in _components.Values.Where(c => c.ClassName is not null && check.Contains(c.ClassName)))
            {
                var tree = (await working.GetDocument(_generated[component.ClassName!])!.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false))!;
                foreach (var error in compilation.GetSemanticModel(tree).GetDiagnostics(cancellationToken: cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error))
                {
                    var method = tree.GetRoot(cancellationToken).FindNode(error.Location.SourceSpan).AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
                    if (method is not null && method.StartsWith(XamlTransform.FunctionPrefix, StringComparison.Ordinal))
                    {
                        // An x:Bind function that doesn't compile yet (e.g. the method it calls isn't written yet).
                        retry |= component.InvalidFunctions.TryAdd(method.EndsWith("_Back", StringComparison.Ordinal) ? method[..^"_Back".Length] : method, error.GetMessage());
                    }
                    else if (!component.ReplayDisabled)
                    {
                        // The constructor body does something only a constructor may (e.g. assign a get-only property):
                        // that statement is left out of the replay, or the whole replay when the error is elsewhere.
                        var replay = tree.GetRoot(cancellationToken).DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == GeneratedPartial.ReplayMethod);
                        var index = replay?.Body?.Statements.IndexOf(s => s.Span.Contains(error.Location.SourceSpan)) ?? -1;
                        if (index >= 0 && component.SkippedStatements.Add(index))
                        {
                            retry = true;
                        }
                        else if (index < 0)
                        {
                            component.ReplayDisabled = true;
                            retry = true;
                        }
                    }
                }
            }
            Phase($"generated: diagnostics ({check.Count})");
            if (!retry)
            {
                foreach (var component in _components.Values.Where(c => c.Pending && c.ClassName is not null && !c.ReplayDisabled))
                {
                    var constructor = GeneratedPartial.ReplayedConstructor(classes.GetValueOrDefault(component.ClassName!) ?? []);
                    var arguments = constructor is null ? [] : GeneratedPartial.ArgumentStatements(constructor);
                    foreach (var index in component.SkippedStatements.Union(arguments).Order())
                    {
                        if (constructor?.Body?.Statements.ElementAtOrDefault(index) is { } statement &&
                            _notedSkips.Add($"{component.ClassName}|{statement}"))
                        {
                            var first = statement.ToString().Split('\n')[0].Trim();
                            var why = arguments.Contains(index)
                                ? $"`{first}` uses the constructor's arguments"
                                : $"only a constructor can run `{first}`";
                            _pendingNotes.Add($"{Relative(statement.SyntaxTree.FilePath)}({statement.GetLocation().GetLineSpan().StartLinePosition.Line + 1}): " +
                                $"{why}, so a reload of {component.ClassName![(component.ClassName.LastIndexOf('.') + 1)..]} replays its constructor without it");
                        }
                    }
                }
                await AdoptGeneratedAsync(working, cancellationToken).ConfigureAwait(false);
                return StaleNameReferences(compilation, userTrees, cancellationToken);
            }
        }
        await AdoptGeneratedAsync(working, cancellationToken).ConfigureAwait(false);
        return [];
    }

    /// <summary>Takes the generated documents whose text really changed from <paramref name="working"/>.</summary>
    private async Task AdoptGeneratedAsync(Solution working, CancellationToken cancellationToken)
    {
        foreach (var id in _generated.Values)
        {
            var updated = working.GetDocument(id)!;
            if (_solution.GetDocument(id) is not { } current)
            {
                _solution = _solution.AddDocument(id, updated.Name, await updated.GetTextAsync(cancellationToken).ConfigureAwait(false), filePath: updated.FilePath);
            }
            else if ((await current.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString() is var before &&
                (await updated.GetTextAsync(cancellationToken).ConfigureAwait(false)) is var after && before != after.ToString())
            {
                _solution = _solution.WithDocumentText(id, after);
            }
        }
    }

    /// <summary>Class declarations by full name, found without walking into member bodies.</summary>
    private static Dictionary<string, List<ClassDeclarationSyntax>> IndexClasses(IEnumerable<SyntaxTree> trees)
    {
        var index = new Dictionary<string, List<ClassDeclarationSyntax>>(StringComparer.Ordinal);
        foreach (var tree in trees)
        {
            foreach (var declaration in tree.GetRoot().DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax).OfType<ClassDeclarationSyntax>())
            {
                var name = GeneratedPartial.NamespaceOf(declaration) is { } ns ? ns + "." + declaration.Identifier.ValueText : declaration.Identifier.ValueText;
                (index.TryGetValue(name, out var parts) ? parts : index[name] = []).Add(declaration);
            }
        }
        return index;
    }


    /// <summary>Code that still uses an x:Name the XAML no longer declares would not build; report it the same way.</summary>
    private List<string> StaleNameReferences(Compilation compilation, List<SyntaxTree> userTrees, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        foreach (var component in _components.Values.Where(c => c.ClassName is not null))
        {
            var stale = component.StaleNames.ToHashSet(StringComparer.Ordinal);
            if (stale.Count == 0)
            {
                continue;
            }
            foreach (var tree in userTrees)
            {
                var root = tree.GetRoot(cancellationToken);
                var identifiers = root.DescendantNodes().OfType<IdentifierNameSyntax>().Where(id => stale.Contains(id.Identifier.ValueText)).ToList();
                if (identifiers.Count == 0)
                {
                    continue;
                }
                var model = compilation.GetSemanticModel(tree);
                foreach (var identifier in identifiers)
                {
                    if (model.GetSymbolInfo(identifier, cancellationToken).Symbol is IFieldSymbol field && field.ContainingType.ToDisplayString() == component.ClassName)
                    {
                        var line = identifier.GetLocation().GetLineSpan().StartLinePosition;
                        problems.Add($"{Path.GetFileName(tree.FilePath)}({line.Line + 1},{line.Character + 1}): '{field.Name}' is no longer an x:Name in {Path.GetFileName(component.XamlPath)}");
                    }
                }
            }
        }
        return problems;
    }

    private async Task<IEnumerable<string>> MissingHandlersAsync(XamlComponent component, CancellationToken cancellationToken)
    {
        var compilation = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        var type = compilation.GetTypeByMetadataName(component.ClassName!);
        static ISymbol? Member(ITypeSymbol? owner, string name)
        {
            for (; owner is not null; owner = owner.BaseType)
            {
                if (owner.GetMembers(name).FirstOrDefault() is { } member)
                {
                    return member;
                }
            }
            return null;
        }
        // Event bindings name a method on an object the path leads to ({x:Bind ViewModel.Increment}).
        ITypeSymbol? Target(EventWiring e) => (e.Path ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Aggregate((ITypeSymbol?)type, (owner, part) => Member(owner, part) switch { IPropertySymbol p => p.Type, IFieldSymbol f => f.Type, _ => null });
        return component.Latest!.Manifest.Events
            .Where(e => type is null || Member(Target(e), e.Handler) is null)
            .Select(e => e.Waiting(component.ClassName![(component.ClassName!.LastIndexOf('.') + 1)..]));
    }

    /// <summary>
    /// Static fields with initializers added to an existing XAML class (typically a new DependencyProperty): their
    /// initializer is emitted as a method the agent calls once, since the static constructor has already run.
    /// </summary>
    private async Task<List<string>> NewStaticInitializersAsync(CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var before = (await _committed.GetProject(_projectId)!.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        var after = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        foreach (var component in _components.Values.Where(c => c.ClassName is not null))
        {
            if (before.GetTypeByMetadataName(component.ClassName!) is not { } oldType || after.GetTypeByMetadataName(component.ClassName!) is not { } newType)
            {
                continue;
            }
            foreach (var field in newType.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsStatic && !f.IsConst && !f.IsImplicitlyDeclared))
            {
                if (!oldType.GetMembers(field.Name).IsEmpty || component.StaticInits.ContainsKey(field.Name))
                {
                    continue;
                }
                var declarator = field.DeclaringSyntaxReferences.Select(r => r.GetSyntax(cancellationToken)).OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(d => d.Initializer is not null && IsSource(d.SyntaxTree.FilePath));
                if (declarator is not null)
                {
                    component.StaticInits[field.Name] = (declarator.Initializer!.Value.ToString(), field.Type.IsReferenceType);
                    result.Add($"{component.ClassName}|{GeneratedPartial.InitPrefix}{field.Name}");
                }
            }
        }

        // Any other type that was already loaded (a control with a new DependencyProperty): the initializer method goes
        // into the class itself, in the session's copy of its file, since it may not be partial.
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A static field can only have been added in a file this save changed (or a generator output that changed).
        var changes = Project.GetChanges(_committed.GetProject(_projectId)!);
        var changedPaths = changes.GetChangedDocuments().Concat(changes.GetAddedDocuments()).Select(id => _solution.GetDocument(id)?.FilePath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in after.SyntaxTrees.Where(t => IsSource(t.FilePath) && changedPaths.Contains(t.FilePath)))
        {
            var model = after.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot(cancellationToken).DescendantNodes().OfType<TypeDeclarationSyntax>().Where(d => d.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax))
            {
                if (model.GetDeclaredSymbol(declaration, cancellationToken) is not { IsGenericType: false } newType)
                {
                    continue;
                }
                var name = newType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
                if (_components.Values.Any(c => c.ClassName == name) || before.GetTypeByMetadataName(name) is not { } oldType)
                {
                    continue;
                }
                foreach (var declarator in declaration.Members.OfType<FieldDeclarationSyntax>().Where(f => f.Modifiers.Any(SyntaxKind.StaticKeyword) && !f.Modifiers.Any(SyntaxKind.ConstKeyword))
                    .SelectMany(f => f.Declaration.Variables).Where(v => v.Initializer is not null && oldType.GetMembers(v.Identifier.ValueText).IsEmpty))
                {
                    if (Injected(name).TryAdd("init:" + declarator.Identifier.ValueText, new InjectedMember(GeneratedPartial.InitPrefix + declarator.Identifier.ValueText, 0,
                        GeneratedPartial.StaticInitializer(declarator.Identifier.ValueText, declarator.Initializer!.Value.ToString(),
                            (model.GetDeclaredSymbol(declarator, cancellationToken) as IFieldSymbol)?.Type.IsReferenceType ?? true), tree.FilePath)))
                    {
                        result.Add($"{name}|{GeneratedPartial.InitPrefix}{declarator.Identifier.ValueText}");
                        touched.Add(tree.FilePath);
                    }
                }
            }
        }
        await ReinjectAsync(touched, cancellationToken).ConfigureAwait(false);

        // Static fields a source generator added to one of its classes the app already loaded (MVVM Toolkit's cached
        // PropertyChangedEventArgs for a new property). Generated code is fully qualified, so a class of the session's
        // own runs the initializer.
        var documentPaths = Project.Documents.Select(d => d.FilePath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generatedBefore = before.SyntaxTrees.Where(t => !documentPaths.Contains(t.FilePath)).GroupBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var tree in after.SyntaxTrees.Where(t => !documentPaths.Contains(t.FilePath) &&
            !(generatedBefore.TryGetValue(t.FilePath, out var old) && (ReferenceEquals(old, t) || old.GetText(cancellationToken).ContentEquals(t.GetText(cancellationToken))))))
        {
            var model = after.GetSemanticModel(tree);
            foreach (var declarator in tree.GetRoot(cancellationToken).DescendantNodes().OfType<FieldDeclarationSyntax>()
                .Where(f => f.Modifiers.Any(SyntaxKind.StaticKeyword) && !f.Modifiers.Any(SyntaxKind.ConstKeyword)).SelectMany(f => f.Declaration.Variables).Where(v => v.Initializer is not null))
            {
                if (model.GetDeclaredSymbol(declarator, cancellationToken) is not IFieldSymbol { DeclaredAccessibility: not Accessibility.Private } field ||
                    field.ContainingType is not { DeclaredAccessibility: not Accessibility.Private, IsGenericType: false } owner ||
                    before.GetTypeByMetadataName(owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "")) is not { } oldOwner ||
                    !oldOwner.GetMembers(field.Name).IsEmpty)
                {
                    continue;
                }
                var target = $"{owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{field.Name}";
                if (!_generatedInits.ContainsKey(target))
                {
                    var method = $"Init{_generatedInits.Count}";
                    _generatedInits[target] = $"internal static void {method}() {{ " +
                        (field.Type.IsReferenceType ? $"if ((object){target} is not null) return; " : "") +
                        $"global::System.Runtime.CompilerServices.Unsafe.AsRef(in {target}) = {declarator.Initializer!.Value}; }}";
                    result.Add($"{GeneratedInitsClass}|{method}");
                }
            }
        }
        if (_generatedInits.Count > 0)
        {
            var source = SourceText.From($"internal static class {GeneratedInitsClass}\n{{\n{string.Join("\n", _generatedInits.Values)}\n}}\n", Encoding.UTF8, SourceHashAlgorithm.Sha256);
            if (_generatedInitsId is null)
            {
                _generatedInitsId = DocumentId.CreateNewId(_projectId);
                _solution = _solution.AddDocument(_generatedInitsId, GeneratedInitsClass + ".g.cs", source, filePath: GeneratedPath(GeneratedInitsClass));
            }
            else if ((await _solution.GetDocument(_generatedInitsId)!.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString() != source.ToString())
            {
                _solution = _solution.WithDocumentText(_generatedInitsId, source);
            }
        }
        return result;
    }

    private const string GeneratedInitsClass = "WinAppHotReloadGeneratedInitializers";
    private readonly Dictionary<string, string> _generatedInits = new(StringComparer.Ordinal);
    private DocumentId? _generatedInitsId;

    /// <summary>A member the session adds to a class in one file: a method, a field when Parameters is -1, a property when -2.</summary>
    /// <param name="Index">Where the member was among the class's members (-1: last); the compiler matches members by position too.</param>
    /// <param name="After">The member it followed: it goes back right after it, so members kept over several saves keep their order.</param>
    private sealed record InjectedMember(string Name, int Parameters, string Text, string File, int Index = -1, string? After = null);

    /// <summary>The name of the member declared before <paramref name="member"/>, if any.</summary>
    private static string? Preceding(TypeDeclarationSyntax declaration, MemberDeclarationSyntax member) =>
        declaration.Members.IndexOf(member) is > 0 and var index ? MemberName(declaration.Members[index - 1]) : null;

    private static string? MemberName(MemberDeclarationSyntax member) => member switch
    {
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        BaseFieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        EventDeclarationSyntax e => e.Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    /// Members the session adds to user classes, by type: initializers for static fields added to loaded, non-XAML types
    /// (see NewStaticInitializersAsync), and removed fields with the partial methods implemented for them (see
    /// KeepRemovedFieldsAsync). They live only in the session's copy of the files.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, InjectedMember>> _injectedInits = new(StringComparer.Ordinal);

    private Dictionary<string, InjectedMember> Injected(string type) =>
        _injectedInits.TryGetValue(type, out var members) ? members : _injectedInits[type] = new(StringComparer.Ordinal);

    /// <summary>Adds the session's members to the classes in a file, unless the file now declares them itself.</summary>
    private SourceText InjectInitializers(SourceText text, string path)
    {
        if (_injectedInits.Count == 0)
        {
            return text;
        }
        var root = CSharpSyntaxTree.ParseText(text, (CSharpParseOptions?)Project.ParseOptions).GetRoot();
        var targets = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(d => d.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax && _injectedInits.ContainsKey(MetadataName(d))).ToList();
        if (targets.Count == 0)
        {
            return text;
        }
        bool Declares(TypeDeclarationSyntax declaration, InjectedMember member) => member.Parameters == -2
            ? declaration.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.ValueText == member.Name)
            : member.Parameters < 0
            ? declaration.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables).Any(v => v.Identifier.ValueText == member.Name)
            : declaration.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == member.Name && m.ParameterList.Parameters.Count == member.Parameters && (m.Body ?? (SyntaxNode?)m.ExpressionBody) is not null);
        var injected = false;
        root = root.ReplaceNodes(targets, (_, declaration) =>
        {
            var members = _injectedInits[MetadataName(declaration)].Values
                .Where(i => string.Equals(i.File, path, StringComparison.OrdinalIgnoreCase) && !Declares(declaration, i)).ToList();
            injected |= members.Count > 0;
            var list = declaration.Members;
            // Right after the member each one followed (which may itself be kept, so as those go in); by position otherwise.
            var pending = members.OrderBy(i => i.Index < 0 ? int.MaxValue : i.Index).ToList();
            while (pending.Count > 0)
            {
                var anchored = pending.FirstOrDefault(m => m.After is { } after && list.Any(existing => MemberName(existing) == after));
                var member = anchored ?? pending.FirstOrDefault(m => m.After is null || !pending.Any(other => other != m && other.Name == m.After)) ?? pending[0];
                pending.Remove(member);
                var syntax = SyntaxFactory.ParseMemberDeclaration(member.Text)!;
                if (anchored is not null)
                {
                    var at = list.IndexOf(list.Last(existing => MemberName(existing) == member.After)) + 1;
                    list = list.Insert(at, syntax);
                }
                else
                {
                    list = member.Index >= 0 && member.Index <= list.Count ? list.Insert(member.Index, syntax) : list.Add(syntax);
                }
            }
            return declaration.WithMembers(list);
        });
        return injected ? SourceText.From(root.ToFullString(), Encoding.UTF8, SourceHashAlgorithm.Sha256) : text;

        static string MetadataName(TypeDeclarationSyntax declaration) =>
            GeneratedPartial.NamespaceOf(declaration) is { Length: > 0 } ns ? ns + "." + declaration.Identifier.ValueText : declaration.Identifier.ValueText;
    }

    /// <summary>
    /// A deleted page's class is still declared by what the XAML compiler generated for it in obj, so code that uses it
    /// still compiles here, though the next build fails (CS0246). The first such use is reported until it's gone.
    /// </summary>
    private async Task<string?> DeletedTypeUsageAsync(List<string> deleted, CancellationToken cancellationToken)
    {
        var oldProject = _committed.GetProject(_projectId)!;
        var compilation = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        foreach (var path in deleted.Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var document in oldProject.Documents.Where(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                foreach (var declaration in root!.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(d => d.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax))
                {
                    var name = GeneratedPartial.NamespaceOf(declaration) is { Length: > 0 } ns ? ns + "." + declaration.Identifier.ValueText : declaration.Identifier.ValueText;
                    if (compilation.GetTypeByMetadataName(name) is not { } type)
                    {
                        // Gone altogether: the compiler reports its uses itself.
                        continue;
                    }
                    foreach (var tree in compilation.SyntaxTrees.Where(t => IsSource(t.FilePath) && !string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                    {
                        var model = compilation.GetSemanticModel(tree);
                        foreach (var identifier in (await tree.GetRootAsync(cancellationToken).ConfigureAwait(false)).DescendantNodes().OfType<IdentifierNameSyntax>()
                            .Where(i => i.Identifier.ValueText == declaration.Identifier.ValueText))
                        {
                            var symbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
                            if (SymbolEqualityComparer.Default.Equals(symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor ? ctor.ContainingType : symbol, type))
                            {
                                var position = identifier.GetLocation().GetLineSpan().StartLinePosition;
                                return $"{Relative(tree.FilePath)}({position.Line + 1},{position.Character + 1}): uses {declaration.Identifier.ValueText}, whose file {Relative(path)} was deleted; " +
                                    "the running app keeps it, but the next build fails (CS0246) until the use is removed or the file is restored";
                            }
                        }
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Members (properties, fields, methods) the code declared before this save and no longer does.</summary>
    private async Task<HashSet<string>> RemovedMemberNamesAsync(CancellationToken cancellationToken)
    {
        static IEnumerable<string> Names(SyntaxNode? root) => root?.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax)
            .OfType<MemberDeclarationSyntax>().SelectMany(m => m switch
            {
                PropertyDeclarationSyntax p => [p.Identifier.ValueText],
                MethodDeclarationSyntax method => [method.Identifier.ValueText],
                BaseFieldDeclarationSyntax field => field.Declaration.Variables.Select(v => v.Identifier.ValueText),
                _ => [],
            }) ?? [];
        var oldProject = _committed.GetProject(_projectId)!;
        var changes = Project.GetChanges(oldProject);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in changes.GetChangedDocuments().Concat(changes.GetRemovedDocuments()))
        {
            if (oldProject.GetDocument(id) is not { FilePath: { } path } old || !IsSource(path))
            {
                continue;
            }
            var before = Names(await old.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false));
            var after = Project.GetDocument(id) is { } current ? Names(await current.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal) : [];
            removed.UnionWith(before.Where(name => !after.Contains(name)));
        }
        return removed;
    }

    /// <summary>
    /// x:Bind paths checked against the code as the user wrote it. A property renamed or deleted in C# stays declared
    /// for the running app (see KeepRemovedFieldsAsync), so markup that still binds it would load with an empty value
    /// and fail the next build (WMC1110); it's reported until the markup follows.
    /// </summary>
    private async Task<string?> StaleBindingsAsync(HashSet<string> removedNames, CancellationToken cancellationToken)
    {
        var project = Project;
        // Files the session added kept members to: as on disk.
        foreach (var file in _injectedInits.Values.SelectMany(m => m.Values).Where(m => m.Parameters is -1 or -2).Select(m => m.File).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file))
            {
                continue;
            }
            var text = SourceText.From(await ReadSharedAsync(file, cancellationToken).ConfigureAwait(false), Encoding.UTF8, SourceHashAlgorithm.Sha256);
            foreach (var id in project.Solution.GetDocumentIdsWithFilePath(file))
            {
                project = project.Solution.WithDocumentText(id, text).GetProject(_projectId)!;
            }
        }
        var resolver = new CompilationTypeResolver((await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!);
        var errors = new List<string>();
        foreach (var path in _components.Keys.Where(File.Exists))
        {
            // Only markup that mentions a name the code no longer declares can bind to it.
            var xaml = await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false);
            if (!removedNames.Any(name => xaml.Contains(name, StringComparison.Ordinal)))
            {
                continue;
            }
            XamlTransformResult transformed;
            try
            {
                transformed = XamlTransform.Transform(xaml, resolver);
            }
            catch (XmlException)
            {
                continue;
            }
            errors.AddRange(transformed.Errors.Where(e => e.Contains("Invalid binding path", StringComparison.Ordinal)).Select(e => Relative(path) + e));
        }
        return errors.Count == 0 ? null
            : string.Join(Environment.NewLine + "  ", errors.Take(3)) + Environment.NewLine + "  the code no longer has what this markup binds: the running app keeps it until the markup is updated, but the next build fails";
    }

    /// <summary>
    /// A page's markup deleted while its class stays: the code still uses what the XAML compiler generated from it
    /// (InitializeComponent, x:Name fields), so the next build fails (CS0103). The first such use is reported.
    /// </summary>
    private async Task<string?> DeletedMarkupUsageAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var compilation = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        var obj = Path.Combine(_projectDirectory, "obj") + Path.DirectorySeparatorChar;
        foreach (var path in paths.Where(p => p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && !File.Exists(p)))
        {
            if (!_components.TryGetValue(path, out var component) || component.ClassName is null || compilation.GetTypeByMetadataName(component.ClassName) is not { } type ||
                // Moved (a folder renamed), not deleted: the class has its markup elsewhere.
                _components.Values.Any(c => c != component && c.ClassName == component.ClassName && File.Exists(c.XamlPath)) ||
                paths.Any(p => p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && File.Exists(p) && DeclaresClass(p, component.ClassName)))
            {
                continue;
            }
            var userTrees = type.DeclaringSyntaxReferences.Select(r => r.SyntaxTree).Where(t => IsSource(t.FilePath) && !t.FilePath.StartsWith(obj, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
            if (userTrees.Count == 0)
            {
                // Only the markup declared the class (its code-behind is gone too): code elsewhere that names it.
                foreach (var tree in compilation.SyntaxTrees.Where(t => IsSource(t.FilePath) && !t.FilePath.StartsWith(obj, StringComparison.OrdinalIgnoreCase)))
                {
                    var model = compilation.GetSemanticModel(tree);
                    foreach (var identifier in (await tree.GetRootAsync(cancellationToken).ConfigureAwait(false)).DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.ValueText == type.Name))
                    {
                        if (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol, type))
                        {
                            var position = identifier.GetLocation().GetLineSpan().StartLinePosition;
                            return $"{Relative(tree.FilePath)}({position.Line + 1},{position.Character + 1}): uses {type.Name}, whose markup {Relative(path)} was deleted; " +
                                "the running app keeps it, but the next build fails (CS0246) until the use is removed or the file is restored";
                        }
                    }
                }
            }
            foreach (var tree in userTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var identifier in (await tree.GetRootAsync(cancellationToken).ConfigureAwait(false)).DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (model.GetSymbolInfo(identifier, cancellationToken).Symbol is { } symbol && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, type) &&
                        symbol.DeclaringSyntaxReferences.Length > 0 && symbol.DeclaringSyntaxReferences.All(r => r.SyntaxTree.FilePath.StartsWith(obj, StringComparison.OrdinalIgnoreCase)))
                    {
                        var position = identifier.GetLocation().GetLineSpan().StartLinePosition;
                        return $"{Relative(tree.FilePath)}({position.Line + 1},{position.Character + 1}): uses {identifier.Identifier.ValueText}, generated from {Relative(path)}, which was deleted; " +
                            "the running app keeps it, but the next build fails (CS0103) until the use is removed or the file is restored";
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Whether the markup file's root declares <paramref name="className"/> (x:Class), read without parsing it.</summary>
    private static bool DeclaresClass(string xamlPath, string className)
    {
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(xamlPath), $@"\bx:Class\s*=\s*[""']{System.Text.RegularExpressions.Regex.Escape(className)}[""']");
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Names of the methods the app's markup calls as x:Bind functions (<c>{x:Bind local:Fmt.Shout(Name)}</c>).</summary>
    private HashSet<string> BindingFunctionNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in WatchRoots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories).Where(IsWatched))
            {
                foreach (System.Text.RegularExpressions.Match bind in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"\{x:Bind\s[^}]*"))
                {
                    foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(bind.Value, @"(\w+)\s*\("))
                    {
                        names.Add(call.Groups[1].Value);
                    }
                }
            }
        }
        return names;
    }

    /// <summary>
    /// Explicit interface members added to a type that already existed (and so is loaded): the one shape behind
    /// CS9346 that the runtime really can't apply.
    /// </summary>
    private async Task<bool> AddsExplicitImplementationToLoadedTypeAsync(CancellationToken cancellationToken)
    {
        var oldProject = _committed.GetProject(_projectId)!;
        var oldCompilation = (await oldProject.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        var changes = Project.GetChanges(oldProject);
        foreach (var id in changes.GetChangedDocuments().Concat(changes.GetAddedDocuments()))
        {
            var before = oldProject.GetDocument(id) is { } o ? StructuralChange.ExplicitImplementations(await o.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)) : [];
            var after = StructuralChange.ExplicitImplementations(await _solution.GetDocument(id)!.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false));
            if (after.Except(before).Any(member => oldCompilation.GetTypeByMetadataName(member.TypeMetadataName) is not null))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Edits beyond a method body only show up once the UI is rebuilt. A XAML class is re-initialized in place (its
    /// constructor body replays; the instance, its view model and fields survive) unless its field initializers changed,
    /// which only a new instance picks up. A change to how other classes are constructed re-creates every hosted page,
    /// so view models built by those pages are constructed again.
    /// </summary>
    private async Task AddStructuralRefreshAsync(HotReloadRequest request, CancellationToken cancellationToken)
    {
        var componentClasses = _components.Values.Select(c => c.ClassName).Where(n => n is not null).ToHashSet(StringComparer.Ordinal);
        var oldProject = _committed.GetProject(_projectId)!;
        var changes = Project.GetChanges(oldProject);
        var ids = changes.GetChangedDocuments().Concat(changes.GetAddedDocuments()).Concat(changes.GetRemovedDocuments());
        HashSet<string>? functionNames = null;
        foreach (var id in ids)
        {
            var path = _solution.GetDocument(id)?.FilePath ?? oldProject.GetDocument(id)?.FilePath;
            if (path is null || !IsSource(path))
            {
                continue;
            }
            var oldRoot = oldProject.GetDocument(id) is { } o ? await o.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) : null;
            var newRoot = _solution.GetDocument(id) is { } n ? await n.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) : null;
            var before = StructuralChange.Signatures(oldRoot);
            var after = StructuralChange.Signatures(newRoot);
            var stateBefore = StructuralChange.Construction(oldRoot);
            var stateAfter = StructuralChange.Construction(newRoot);
            var gettersBefore = StructuralChange.Getters(oldRoot);
            foreach (var (getter, body) in StructuralChange.Getters(newRoot))
            {
                if (gettersBefore.TryGetValue(getter, out var old) && old != body && !request.ChangedProperties.Contains(getter))
                {
                    request.ChangedProperties.Add(getter);
                    request.RefreshBindings = true;
                }
            }
            var methodsBefore = StructuralChange.Methods(oldRoot);
            foreach (var (method, (body, converter)) in StructuralChange.Methods(newRoot))
            {
                if (!request.RefreshBindings && methodsBefore.TryGetValue(method, out var old) && old.Body != body &&
                    (converter || (functionNames ??= BindingFunctionNames()).Contains(method[(method.IndexOf('|') + 1)..])))
                {
                    request.RefreshBindings = true;
                }
            }
            foreach (var type in before.Keys.Union(after.Keys))
            {
                if (before.GetValueOrDefault(type) == after.GetValueOrDefault(type) || !before.ContainsKey(type))
                {
                    // Unchanged, or new: a new class has no live instances to rebuild.
                    continue;
                }
                var constructionChanged = stateBefore.GetValueOrDefault(type) != stateAfter.GetValueOrDefault(type);
                if (componentClasses.Contains(type))
                {
                    var target = constructionChanged && StructuralChange.InitializersChanged(oldRoot, newRoot, type) ? request.RecreateTypes : request.RefreshTypes;
                    if (!target.Contains(type))
                    {
                        target.Add(type);
                    }
                }
                else if (constructionChanged)
                {
                    request.RefreshAllPages = true;
                }
            }
        }
    }
    /// <summary>Content the build copies next to the app, by source file, with its path there (as of the last refresh).</summary>
    private readonly Dictionary<string, string> _contentTargets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs MSBuild on the app project; returns the failure (its first errors), or null.</summary>
    private async Task<string?> MSBuildAsync(IEnumerable<string> arguments, string what, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { WorkingDirectory = _projectDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode == 0)
        {
            return null;
        }
        var log = (await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false)).Split('\n')
            .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Select(l => l.Trim()).Distinct().Take(3);
        return $"{Path.GetFileName(_options.ProjectFile)}: {what} failed: {string.Join(" ", log)}";
    }

    /// <summary>
    /// A project file edit (for example a new PackageReference) or new content: restore when the project changed, then
    /// re-resolve references and deploy new ones next to the app.
    /// </summary>
    private async Task<string?> RefreshReferencesAsync(bool restore, IReadOnlyCollection<string> changedContent, CancellationToken cancellationToken)
    {
        var output = Path.Combine(_markupDirectory, "references.txt");
        // The files winapp gave this session can be gone from %TEMP% by now (a cleanup elsewhere): what they said at startup.
        if (!File.Exists(_options.TargetsFile) && _launchTargets is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_options.TargetsFile)!);
            File.WriteAllText(_options.TargetsFile, _launchTargets);
            File.SetLastWriteTimeUtc(_options.TargetsFile, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }
        var built = (File.Exists(_options.CscArgsFile + ".props") ? File.ReadAllLines(_options.CscArgsFile + ".props") : _launchProperties ?? [])
            .Where(line => line.Contains('=') && !line.EndsWith('=')).ToList();
        if (restore)
        {
            // As the launch build restored: the configuration and runtime identifier only. The app's evaluated
            // TargetFramework or Platform would flow into every referenced project (a netstandard2.0 source
            // generator included) and leave its assets file without its own framework.
            var restoreProperties = built.Where(line => line.StartsWith("Configuration=", StringComparison.OrdinalIgnoreCase) || line.StartsWith("RuntimeIdentifier=", StringComparison.OrdinalIgnoreCase));
            if (await MSBuildAsync(["msbuild", _options.ProjectFile, "-t:Restore", "-nologo", "-v:q", .. restoreProperties.Select(line => $"-p:{line}")], "restore", cancellationToken).ConfigureAwait(false) is { } restoreError)
            {
                return restoreError;
            }
        }
        if (await MSBuildAsync(["msbuild", _options.ProjectFile, "-restore:false", "-nologo", "-v:q", "-t:WinAppHotReloadReferences",
            $"-p:CustomAfterMicrosoftCommonTargets={_options.TargetsFile}", $"-p:WinAppHotReloadOut={output}", .. built.Select(line => $"-p:{line}")],
            "reading the project", cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        var lines = await File.ReadAllLinesAsync(output, cancellationToken).ConfigureAwait(false);
        var args = _cscArgs.Where(a => !a.StartsWith("/reference:", StringComparison.Ordinal) && !a.StartsWith("/analyzer:", StringComparison.Ordinal))
            .Concat(lines.Where(l => l.StartsWith("/reference:", StringComparison.Ordinal) || l.StartsWith("/analyzer:", StringComparison.Ordinal))
                .Select(l => l.Contains(' ') ? l[..(l.IndexOf(':') + 1)] + "\"" + l[(l.IndexOf(':') + 1)..] + "\"" : l))
            .ToArray();
        var info = CreateProjectInfo(_workspace, _options.ProjectFile, args);
        var current = Project;
        // A package or project removed from the project file stays loaded in the running app, and code generated for
        // markup that used it may still refer to it: keep its references for the session unless an assembly of the
        // same name replaces them.
        var newNames = info.MetadataReferences.OfType<PortableExecutableReference>().Select(r => Path.GetFileName(r.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedUsage = await RemovedReferenceUsageAsync(current, current.MetadataReferences.OfType<PortableExecutableReference>()
            .Where(r => !newNames.Contains(Path.GetFileName(r.FilePath))).ToList(), cancellationToken).ConfigureAwait(false);
        info = info.WithMetadataReferences(info.MetadataReferences.Concat(current.MetadataReferences.OfType<PortableExecutableReference>().Where(r => !newNames.Contains(Path.GetFileName(r.FilePath)))));
        var newAnalyzerNames = info.AnalyzerReferences.Select(r => Path.GetFileName(r.FullPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        info = info.WithAnalyzerReferences(info.AnalyzerReferences.Concat(current.AnalyzerReferences.Where(r => !newAnalyzerNames.Contains(Path.GetFileName(r.FullPath)))));
        var newReferences = info.MetadataReferences.OfType<PortableExecutableReference>().Select(r => r.FilePath).Except(current.MetadataReferences.OfType<PortableExecutableReference>().Select(r => r.FilePath), StringComparer.OrdinalIgnoreCase).Any();
        var newAnalyzers = info.AnalyzerReferences.Select(r => r.FullPath).Except(current.AnalyzerReferences.Select(r => r.FullPath), StringComparer.OrdinalIgnoreCase).Any();
        if (newReferences || info.MetadataReferences.Count != current.MetadataReferences.Count)
        {
            _solution = _solution.WithProjectMetadataReferences(_projectId, info.MetadataReferences);
        }
        if (newAnalyzers || info.AnalyzerReferences.Count != current.AnalyzerReferences.Count)
        {
            _solution = _solution.WithProjectAnalyzerReferences(_projectId, info.AnalyzerReferences);
        }

        _appItems = [.. lines.Where(l => l.StartsWith("/item:", StringComparison.Ordinal)).Select(l => l["/item:".Length..])];
        _contentTargets.Clear();
        foreach (var line in lines.Where(l => l.StartsWith("/content:", StringComparison.Ordinal)))
        {
            // Files the build copies next to the app (images, data): new ones, and the ones saved now if they changed.
            var (source, target) = line["/content:".Length..].Split('|') is [var s, var t] ? (s, t) : ("", "");
            if (target.Length == 0)
            {
                continue;
            }
            _contentTargets[source] = target;
            CopyChangedContent(source, target, compare: changedContent.Contains(source, StringComparer.OrdinalIgnoreCase));
        }
        foreach (var line in lines.Where(l => l.StartsWith("/copylocal:", StringComparison.Ordinal)))
        {
            var (source, subdirectory) = line["/copylocal:".Length..].Split('|') is [var s, var d] ? (s, d) : (line["/copylocal:".Length..], "");
            var destination = Path.Combine(_baseDirectory, subdirectory, Path.GetFileName(source));
            if (_baseDirectory.Length == 0 || !File.Exists(source) || File.Exists(destination))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            if (destination.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && subdirectory.Length == 0)
            {
                _newAssemblies.Add(Path.GetFileNameWithoutExtension(destination));
                // A library's compiled markup is in its .pri, which the build would merge into the app's.
                if (File.Exists(Path.ChangeExtension(source, ".pri")) && !File.Exists(Path.ChangeExtension(destination, ".pri")))
                {
                    File.Copy(Path.ChangeExtension(source, ".pri"), Path.ChangeExtension(destination, ".pri"));
                }
            }
        }
        return removedUsage;
    }

    /// <summary>
    /// A package or project removed from the project file while the app's code or markup still uses it: the running app
    /// keeps it, but the next build fails, so this is reported (file(line,col) of the first use) until the use is gone.
    /// </summary>
    private async Task<string?> RemovedReferenceUsageAsync(Project project, List<PortableExecutableReference> removed, CancellationToken cancellationToken)
    {
        if (removed.Count == 0 || await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation)
        {
            return null;
        }
        var removedAssemblies = removed.Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>().ToHashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
        foreach (var reference in removed)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
            {
                continue;
            }
            var namespaces = new HashSet<string>(StringComparer.Ordinal);
            void Collect(INamespaceSymbol ns)
            {
                if (ns.GetTypeMembers().Any(t => t.DeclaredAccessibility == Accessibility.Public))
                {
                    namespaces.Add(ns.ToDisplayString());
                }
                foreach (var child in ns.GetNamespaceMembers())
                {
                    Collect(child);
                }
            }
            Collect(assembly.GlobalNamespace);
            // Namespaces the app or a reference it still has also have types in are not evidence of a use.
            namespaces.RemoveWhere(name => name.Split('.').Aggregate((INamespaceSymbol?)compilation.GlobalNamespace, (ns, part) => ns?.GetNamespaceMembers().FirstOrDefault(m => m.Name == part)) is { } merged &&
                merged.ConstituentNamespaces.Any(c => !removedAssemblies.Contains(c.ContainingAssembly) && c.GetTypeMembers().Any(t => t.DeclaredAccessibility == Accessibility.Public)));
            if (namespaces.Count == 0)
            {
                continue;
            }
            var files = Directory.EnumerateFiles(_projectDirectory, "*.xaml", SearchOption.AllDirectories)
                .Concat(project.Documents.Select(d => d.FilePath).OfType<string>().Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
                .Where(p => File.Exists(p) && IsSource(p));
            foreach (var file in files)
            {
                var text = (await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)).Replace("\r\n", "\n");
                foreach (var ns in namespaces)
                {
                    var at = -1;
                    if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                    {
                        // The first element or markup extension with the namespace's prefix; its xmlns declaration otherwise.
                        if (System.Text.RegularExpressions.Regex.Match(text, $@"xmlns:(\w+)\s*=\s*""(?:using|clr-namespace):{System.Text.RegularExpressions.Regex.Escape(ns)}""") is { Success: true } xmlns)
                        {
                            var use = System.Text.RegularExpressions.Regex.Match(text, $@"[<{{]{xmlns.Groups[1].Value}:\w");
                            at = use.Success ? use.Index + 1 : xmlns.Index;
                        }
                    }
                    else
                    {
                        at = text.IndexOf($"using {ns};", StringComparison.Ordinal);
                    }
                    if (at >= 0)
                    {
                        var line = text.AsSpan(0, at).Count('\n');
                        var column = at - (text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1);
                        return $"{Relative(file)}({line + 1},{column + 1}): uses {ns} from {Path.GetFileNameWithoutExtension(reference.FilePath)}, which {Path.GetFileName(_options.ProjectFile)} no longer references; the running app keeps it, but the next build fails until the use is removed or the reference is added back";
                    }
                }
            }
        }
        return null;
    }
}

/// <summary>The compiler failed to produce an update for a change it accepts; only a rebuild can apply it.</summary>
internal sealed class CompilerCrashException(Exception inner) : Exception(inner.Message, inner);