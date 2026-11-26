// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using WinApp.HotReload.Protocol;

namespace WinApp.HotReload;

/// <summary>
/// Engine B: a Roslyn workspace of the app as it was built, and the SDK's hot reload service (<see cref="HotReloadApi"/>)
/// to turn each save into one .NET update, which the agent in the app applies. It owns the baseline the running app was
/// built from, keeps deleted files as ghosts, skips saves that change no code, and decides restarts by diagnostic ID.
/// </summary>
internal sealed class RoslynEngine : ICSharpEngine
{
    // CoreCLR can't add an explicit interface implementation to a loaded type, so it doesn't advertise this. A type
    // that is new in the update (an iterator's state machine) takes one fine, so it is offered on a retry.
    private const string ExplicitInterfaceCapability = "AddExplicitInterfaceImplementation";

    private readonly ProjectGraph _graph;
    private readonly AgentClient _agent;
    private readonly string _baseDirectory;
    private readonly AdhocWorkspace _workspace;
    private readonly ProjectId _projectId;
    private readonly HashSet<string> _ghosts = new(StringComparer.OrdinalIgnoreCase);
    private HotReloadApi _api = null!;
    private Solution _solution;
    private Solution _committed;
    private bool _offerExplicitInterfaces;

    private RoslynEngine(ProjectGraph graph, AgentClient agent, string baseDirectory, AdhocWorkspace workspace, ProjectId projectId, Solution solution)
    {
        _graph = graph;
        _agent = agent;
        _baseDirectory = baseDirectory;
        _workspace = workspace;
        _projectId = projectId;
        _solution = _committed = solution;
    }

    /// <param name="capabilities">What the app's runtime can apply, from the agent.</param>
    /// <param name="baselineDirectory">Where to keep a copy of the assembly the app runs.</param>
    public static async Task<RoslynEngine> CreateAsync(ProjectGraph graph, AgentClient agent, string capabilities, string baseDirectory,
        string baselineDirectory, CancellationToken cancellationToken)
    {
        var host = MefHostServices.Create(MefHostServices.DefaultAssemblies.Concat(HotReloadApi.FeatureAssemblies.Select(Assembly.Load)));
        var workspace = new AdhocWorkspace(host);
        var info = graph.CreateProjectInfo(workspace, graph.CompilerArguments());
        info = info.WithCompilationOutputInfo(info.CompilationOutputInfo.WithAssemblyPath(SnapshotBaseline(info.CompilationOutputInfo.AssemblyPath!, baselineDirectory)))
            .WithDocuments(info.Documents.Select(document => AsBuilt(document, graph)));
        var solution = workspace.AddProject(info).Solution;
        var engine = new RoslynEngine(graph, agent, baseDirectory, workspace, info.Id, solution);
        var offered = capabilities.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
        engine._api = new HotReloadApi(solution, [info.Id], () => engine._offerExplicitInterfaces ? offered.Add(ExplicitInterfaceCapability) : offered);
        await engine._api.StartAsync(solution, cancellationToken).ConfigureAwait(false);
        // The first save would otherwise pay for compiling the whole app; do it while nobody waits.
        engine._warmUp = Task.Run(async () =>
        {
            try
            {
                (await engine.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))?.GetDiagnostics(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The first save compiles again and reports what went wrong.
            }
        }, cancellationToken);
        engine._editWarmUp = engine._warmUp.ContinueWith(_ => engine.WarmUpFirstEditAsync(graph.ProjectDirectory, cancellationToken), cancellationToken,
            TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        return engine;
    }

    /// <summary>Set by the first save: the warm-up of the first edit doesn't start any more, so that save doesn't wait for it.</summary>
    private volatile bool _saved;

    private Task _editWarmUp = Task.CompletedTask;

    /// <summary>
    /// The first C# change that isn't only a method body would otherwise pay for .NET's edit-and-continue analysis and
    /// the first type image for the XAML compiler (0.8 s together). Both are done here, unless a save came first: the
    /// update, a method added to one of the app's classes in a copy of the app, is discarded; the app never gets it.
    /// </summary>
    private async Task WarmUpFirstEditAsync(string projectDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var compilation = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
            if (_saved)
            {
                return;
            }
            compilation.Emit(Stream.Null, options: new EmitOptions(metadataOnly: true, includePrivateMembers: true), cancellationToken: cancellationToken);
            if (_saved)
            {
                return;
            }
            var copy = _solution.AddDocument(DocumentId.CreateNewId(_projectId), "WinAppHotReloadWarmUp.cs",
                SourceText.From(WarmUpEdit(compilation, IsUserFile, cancellationToken), Encoding.UTF8),
                filePath: Path.Combine(projectDirectory, "obj", "WinAppHotReloadWarmUp.cs"));
            try
            {
                await _api.GetUpdatesAsync(copy, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _api.Discard();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only a warm-up: the first change pays for it instead.
        }
    }

    /// <summary>
    /// The warm-up's edit: a method added to the first partial class of the app's own code (a page's code-behind, say),
    /// the way a new event handler is; a new class when there's none.
    /// </summary>
    internal static string WarmUpEdit(Compilation compilation, Func<string?, bool> isUserFile, CancellationToken cancellationToken)
    {
        foreach (var tree in compilation.SyntaxTrees.Where(t => isUserFile(t.FilePath)))
        {
            var partial = tree.GetRoot(cancellationToken).DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax &&
                    c.TypeParameterList is null && c.Modifiers.Any(m => m.ValueText == "partial") && !c.Modifiers.Any(m => m.ValueText == "static"));
            if (partial is not null && compilation.GetSemanticModel(tree).GetDeclaredSymbol(partial, cancellationToken) is { } type)
            {
                var body = $"partial class {type.Name} {{ private static int WinAppHotReloadWarmUp() => 1; }}";
                return type.ContainingNamespace.IsGlobalNamespace ? body : $"namespace {type.ContainingNamespace.ToDisplayString()} {{ {body} }}";
            }
        }
        return "internal static class WinAppHotReloadWarmUp { internal static int Value() => 1; }";
    }

    private Task _warmUp = Task.CompletedTask;

    private Project Project => _solution.GetProject(_projectId)!;

    public async Task<CSharpResult> ApplyAsync(CSharpChange change, CancellationToken cancellationToken)
    {
        await _warmUp.ConfigureAwait(false);
        _saved = true;
        // A warm-up already computing its update finishes (and discards it) before this one starts.
        await _editWarmUp.ConfigureAwait(false);
        var referencesChanged = false;
        if (change.Files.Any(f => string.Equals(f, _graph.ProjectFile, StringComparison.OrdinalIgnoreCase)))
        {
            var (problem, changed) = await RefreshReferencesAsync(cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                return problem;
            }
            referencesChanged = changed;
        }
        var deleted = await UpdateDocumentsAsync(change.Files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)), cancellationToken).ConfigureAwait(false);
        foreach (var (path, text) in change.Generated)
        {
            var source = SourceText.From(text, Encoding.UTF8, SourceHashAlgorithm.Sha256);
            _solution = Project.Documents.FirstOrDefault(d => SamePath(d.FilePath, path)) is { } existing
                ? _solution.WithDocumentText(existing.Id, source)
                : _solution.AddDocument(DocumentId.CreateNewId(_projectId), Path.GetFileName(path), source, filePath: path);
        }

        var updates = await UpdatesAsync(cancellationToken).ConfigureAwait(false);
        var ghosted = new List<string>();
        if (updates is not null && updates.Errors.Count == 0 && (updates.RudeEdits.Count > 0 || updates.NeedsRestart) && deleted.Count > 0)
        {
            // A loaded type can't be removed. Once the rest compiles without a deleted file, its code stays for the session.
            _api.Discard();
            await KeepAsGhostsAsync(deleted, cancellationToken).ConfigureAwait(false);
            ghosted.AddRange(deleted.Select(path => $"{Path.GetRelativePath(_graph.ProjectDirectory, path)} was deleted; its code stays in the running app until it restarts"));
            updates = await UpdatesAsync(cancellationToken).ConfigureAwait(false);
        }
        if (updates is { Errors.Count: > 0 } && updates.Errors.All(d => d.Id == "CS9346"))
        {
            _api.Discard();
            _offerExplicitInterfaces = true;
            _api.CapabilitiesChanged();
            try
            {
                updates = await UpdatesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _offerExplicitInterfaces = false;
                _api.CapabilitiesChanged();
            }
        }

        if (updates is null || (updates.Errors.Count == 0 && updates.RudeEdits.Count == 0 && !updates.NeedsRestart && updates.Updates.Count == 0))
        {
            // Nothing that runs changed (a comment, whitespace), or only the references did. An empty update is
            // discarded, never committed: committing one sets up the crash UpdatesAsync describes.
            if (updates is not null)
            {
                _api.Discard();
            }
            _committed = _solution;
            return referencesChanged || ghosted.Count > 0
                ? new CSharpResult(CSharpOutcome.Applied, ghosted.Count > 0 ? string.Join(Environment.NewLine, ghosted) : null)
                : new CSharpResult(CSharpOutcome.Unchanged);
        }
        // Errors the runtime raises (ENC IDs) mean the code is valid but can't go into the running app.
        var restartErrors = updates.Errors.Where(d => d.Id.StartsWith("ENC", StringComparison.Ordinal)).ToList();
        if (updates.Errors.Count > restartErrors.Count)
        {
            _api.Discard();
            return CSharpResult.Error(string.Join(Environment.NewLine + "  ", updates.Errors.Except(restartErrors).Take(3).Select(Format)));
        }
        if (restartErrors.Count > 0 || updates.RudeEdits.Count > 0 || updates.NeedsRestart)
        {
            _api.Discard();
            return CSharpResult.Restart(restartErrors.Concat(updates.RudeEdits).Select(Format).FirstOrDefault() ?? "the change can't be applied to the running app");
        }
        if (await StaleCallerAsync(cancellationToken).ConfigureAwait(false) is { } stale)
        {
            _api.Discard();
            return stale;
        }
        var notes = (await NotesAsync(cancellationToken).ConfigureAwait(false)).Concat(ghosted).ToList();

        HotReloadResponse response;
        try
        {
            response = await _agent.SendAsync(new HotReloadRequest
            {
                Op = "apply",
                Deltas = [.. updates.Updates.Select(u => new DeltaPayload { ModuleId = u.ModuleId, Metadata = u.Metadata, IL = u.IL, Pdb = u.Pdb, UpdatedTypes = u.UpdatedTypes })],
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _api.Discard();
            return CSharpResult.Restart("lost the connection to the app: " + ex.Message);
        }
        if (!response.Ok)
        {
            _api.Discard();
            return CSharpResult.Restart("the app couldn't apply the update: " + response.Error);
        }
        _api.Commit();
        _committed = _solution;
        return new CSharpResult(CSharpOutcome.Applied, notes.Count > 0 ? string.Join(Environment.NewLine, notes) : null, response.UpdatedTypes);
    }

    /// <summary>
    /// A method whose signature changed (or that was removed) is replaced in the running app by one that throws, and
    /// .NET only recompiles the files saved. A caller in another file would still call the old one, so it restarts,
    /// or, when the caller no longer compiles, it's the build error.
    /// </summary>
    private async Task<CSharpResult?> StaleCallerAsync(CancellationToken cancellationToken)
    {
        var oldProject = _committed.GetProject(_projectId)!;
        var changes = Project.GetChanges(oldProject);
        var saved = changes.GetChangedDocuments().Concat(changes.GetRemovedDocuments()).ToHashSet();
        var before = (await oldProject.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        var after = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        foreach (var id in changes.GetChangedDocuments())
        {
            var tree = await oldProject.GetDocument(id)!.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            var model = before.GetSemanticModel(tree!);
            foreach (var declaration in tree!.GetRoot(cancellationToken).DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration, cancellationToken) is not IMethodSymbol method ||
                    DocumentationCommentId.CreateDeclarationId(method) is not { } signature ||
                    DocumentationCommentId.GetFirstSymbolForDeclarationId(signature, after) is not null)
                {
                    continue;
                }
                var callers = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindReferencesAsync(method, _committed, cancellationToken).ConfigureAwait(false);
                var outside = callers.SelectMany(r => r.Locations).Where(l => !saved.Contains(l.Document.Id)).ToList();
                if (outside.Count == 0)
                {
                    continue;
                }
                var caller = outside[0];
                var callerTree = after.SyntaxTrees.FirstOrDefault(t => SamePath(t.FilePath, caller.Document.FilePath!));
                if (callerTree is not null && after.GetSemanticModel(callerTree).GetDiagnostics(cancellationToken: cancellationToken)
                    .FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is { } error)
                {
                    return CSharpResult.Error(Format(error));
                }
                var span = caller.Location.GetLineSpan();
                return CSharpResult.Restart($"{Path.GetRelativePath(_graph.ProjectDirectory, span.Path)}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): " +
                    $"calls {method.Name} as it was before this save, and .NET can't update a caller in a file that wasn't saved; it takes effect when the app restarts");
            }
        }
        return null;
    }

    /// <summary>
    /// What an applied update leaves different from a restart (decision D4): code that already ran doesn't run again.
    /// A new static field keeps its default until a restart; objects that exist keep what their constructor did.
    /// </summary>
    private async Task<List<string>> NotesAsync(CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var oldProject = _committed.GetProject(_projectId)!;
        var before = (await oldProject.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        var after = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        foreach (var id in Project.GetChanges(oldProject).GetChangedDocuments())
        {
            var oldTree = (await oldProject.GetDocument(id)!.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false))!;
            var newTree = (await Project.GetDocument(id)!.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false))!;
            var model = after.GetSemanticModel(newTree);
            foreach (var type in newTree.GetRoot(cancellationToken).DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(type, cancellationToken) is not INamedTypeSymbol symbol ||
                    before.GetTypeByMetadataName(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "")) is not { } existing)
                {
                    continue;
                }
                foreach (var field in symbol.GetMembers().OfType<IFieldSymbol>().Where(f => existing.GetMembers(f.Name).IsEmpty && !f.IsConst && !f.IsImplicitlyDeclared))
                {
                    var hasInitializer = field.DeclaringSyntaxReferences.Any(r => r.GetSyntax(cancellationToken) is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax { Initializer: not null });
                    if (hasInitializer)
                    {
                        notes.Add(field.IsStatic
                            ? $"{symbol.Name}.{field.Name} starts at its default until the app restarts (its initializer runs when the class first loads)"
                            : $"{symbol.Name}.{field.Name} starts at its default in objects created before this save");
                    }
                }
                var oldType = oldTree.GetRoot(cancellationToken).DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>()
                    .FirstOrDefault(t => t.Identifier.ValueText == type.Identifier.ValueText);
                var oldConstructors = oldType?.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax>().ToList() ?? [];
                foreach (var constructor in oldType is null ? [] : type.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax>()
                    .Where(c => !oldConstructors.Any(o => Microsoft.CodeAnalysis.CSharp.SyntaxFactory.AreEquivalent(o, c))))
                {
                    notes.Add(constructor.Modifiers.Any(m => m.ValueText == "static")
                        ? $"{symbol.Name}'s static constructor changed: it already ran, and runs again when the app restarts"
                        : $"{symbol.Name}'s constructor changed: objects created before this save don't run it again");
                }
            }
        }
        return [.. notes.Distinct()];
    }

    public bool Has(string path, string text) =>
        Project.Documents.FirstOrDefault(d => SamePath(d.FilePath, path)) is { } document && document.TryGetText(out var current) && current.ToString() == text;

    public async Task<byte[]?> AppImageAsync(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        await _warmUp.ConfigureAwait(false);
        var solution = _solution;
        foreach (var path in files.Where(File.Exists))
        {
            var text = SourceText.From(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false), Encoding.UTF8);
            solution = Project.Documents.FirstOrDefault(d => SamePath(d.FilePath, path)) is { } existing
                ? solution.WithDocumentText(existing.Id, text)
                : solution.AddDocument(DocumentId.CreateNewId(_projectId), Path.GetFileName(path), text, filePath: path);
        }
        var project = solution.GetProject(_projectId)!;
        // The image is for the app's own code (x:Bind paths, handlers, types the markup uses). Each save compiles its
        // pages' generated code itself, so while the app's code and references are as they were, the last image is
        // still right: building one costs a large app a third of a second.
        var key = await ImageKeyAsync(project, IsUserFile, cancellationToken).ConfigureAwait(false);
        if (_image is { } last && last.Key == key)
        {
            return last.Bytes;
        }
        var compilation = (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
        using var stream = new MemoryStream();
        // Private members too: x:Bind reaches the page's private fields and methods.
        var emit = compilation.Emit(stream, options: new EmitOptions(metadataOnly: true, includePrivateMembers: true), cancellationToken: cancellationToken);
        if (!emit.Success)
        {
            return null;
        }
        _image = new AppImage(key, stream.ToArray());
        return _image.Bytes;
    }

    private sealed record AppImage(string Key, byte[] Bytes);

    private AppImage? _image;

    /// <summary>What the app's own code (not the XAML compiler's) and its references are, as text to compare.</summary>
    internal static async Task<string> ImageKeyAsync(Project project, Func<string?, bool> isUserFile, CancellationToken cancellationToken)
    {
        var key = new StringBuilder();
        key.Append(project.ParseOptions?.GetHashCode()).Append('|').Append(project.CompilationOptions?.GetHashCode()).Append('\n');
        foreach (var reference in project.MetadataReferences)
        {
            key.Append(reference.Display).Append('\n');
        }
        foreach (var document in project.Documents.Where(d => isUserFile(d.FilePath)).OrderBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            key.Append(document.FilePath).Append(' ').Append(Convert.ToHexString([.. text.GetChecksum()])).Append('\n');
        }
        return key.ToString();
    }

    /// <summary>Brings the workspace's documents in line with the saved files; returns the files deleted.</summary>
    private async Task<List<string>> UpdateDocumentsAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var deleted = new List<string>();
        foreach (var path in paths)
        {
            var existing = Project.Documents.FirstOrDefault(d => SamePath(d.FilePath, path));
            if (File.Exists(path))
            {
                var text = SourceText.From(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false), Encoding.UTF8, SourceHashAlgorithm.Sha256);
                _ghosts.Remove(path);
                if (existing is not null && (await existing.GetTextAsync(cancellationToken).ConfigureAwait(false)).ContentEquals(text))
                {
                    continue;
                }
                var folders = Path.GetRelativePath(_graph.ProjectDirectory, Path.GetDirectoryName(path)!).Split(Path.DirectorySeparatorChar).Where(f => f != ".").ToList();
                _solution = existing is null
                    ? _solution.AddDocument(DocumentId.CreateNewId(_projectId), Path.GetFileName(path), text, folders, path)
                    : _solution.WithDocumentText(existing.Id, text);
            }
            else if (existing is not null && !_ghosts.Contains(path))
            {
                _solution = _solution.RemoveDocument(existing.Id);
                deleted.Add(path);
            }
        }
        // A deletion in an earlier save that didn't apply is still part of this update.
        foreach (var document in _committed.GetProject(_projectId)!.Documents)
        {
            if (document.FilePath is { } path && Project.GetDocument(document.Id) is null && !_ghosts.Contains(path) && !deleted.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                deleted.Add(path);
            }
        }
        return deleted;
    }

    private async Task KeepAsGhostsAsync(IEnumerable<string> deleted, CancellationToken cancellationToken)
    {
        foreach (var path in deleted)
        {
            foreach (var id in _committed.GetDocumentIdsWithFilePath(path).Where(id => Project.GetDocument(id) is null))
            {
                var old = _committed.GetDocument(id)!;
                _solution = _solution.AddDocument(id, old.Name, await old.GetTextAsync(cancellationToken).ConfigureAwait(false), old.Folders, path);
            }
            _ghosts.Add(path);
        }
    }

    /// <summary>
    /// The update for the current files, or null when nothing but comments or whitespace changed within existing
    /// lines. Such a save never reaches .NET's hot reload: a no-change update there sets up a crash in the next update
    /// of a method added this session (dotnet/roslyn#79898). It's included with the next real change instead.
    /// </summary>
    private async Task<ApiUpdates?> UpdatesAsync(CancellationToken cancellationToken)
    {
        var changes = _solution.GetChanges(_committed).GetProjectChanges().ToList();
        if (changes.Count == 0 || await OnlyTriviaChangedAsync(changes, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return await _api.GetUpdatesAsync(_solution, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> OnlyTriviaChangedAsync(IEnumerable<ProjectChanges> changes, CancellationToken cancellationToken)
    {
        foreach (var project in changes)
        {
            if (project.GetAddedDocuments().Any() || project.GetRemovedDocuments().Any() || project.GetAddedMetadataReferences().Any() ||
                project.GetRemovedMetadataReferences().Any() || project.GetAddedAnalyzerReferences().Any() || project.GetRemovedAnalyzerReferences().Any() ||
                project.OldProject.ParseOptions != project.NewProject.ParseOptions || project.OldProject.CompilationOptions != project.NewProject.CompilationOptions ||
                // A generator's inputs: its output can change.
                project.GetChangedAdditionalDocuments().Any() || project.GetAddedAdditionalDocuments().Any() || project.GetRemovedAdditionalDocuments().Any() ||
                project.GetChangedAnalyzerConfigDocuments().Any() || project.GetAddedAnalyzerConfigDocuments().Any() || project.GetRemovedAnalyzerConfigDocuments().Any())
            {
                return false;
            }
            foreach (var id in project.GetChangedDocuments())
            {
                var before = await project.OldProject.GetDocument(id)!.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                var after = await project.NewProject.GetDocument(id)!.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                // A new line moves the code below it: its line numbers are a real update.
                if (before is null || after is null || !before.IsEquivalentTo(after, topLevel: false) ||
                    (await before.GetTextAsync(cancellationToken).ConfigureAwait(false)).Lines.Count != (await after.GetTextAsync(cancellationToken).ConfigureAwait(false)).Lines.Count)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// A project file save: new references go into the workspace and new assemblies next to the app. A reference that
    /// was removed stays (the running app keeps it loaded), but code that still uses it is an error, as in a build.
    /// </summary>
    private async Task<(CSharpResult? Problem, bool Changed)> RefreshReferencesAsync(CancellationToken cancellationToken)
    {
        var (arguments, copyLocal, restart, error) = await _graph.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (restart is not null || error is not null)
        {
            return (restart is not null ? CSharpResult.Restart(restart) : CSharpResult.Error(error!), false);
        }
        var fresh = _graph.CreateProjectInfo(_workspace, arguments!);
        var current = Project;
        var currentPaths = current.MetadataReferences.OfType<PortableExecutableReference>().Select(r => r.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var freshPaths = fresh.MetadataReferences.OfType<PortableExecutableReference>().Select(r => r.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!freshPaths.SetEquals(currentPaths))
        {
            var removed = currentPaths.Except(freshPaths, StringComparer.OrdinalIgnoreCase).Select(Path.GetFileNameWithoutExtension).ToList();
            var without = (await current.WithMetadataReferences(fresh.MetadataReferences).GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
            if (removed.Count > 0 && without.GetDiagnostics(cancellationToken).FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error && IsUserFile(d.Location.SourceTree?.FilePath)) is { } use)
            {
                return (CSharpResult.Error($"{Format(use)} ({Path.GetFileName(_graph.ProjectFile)} no longer references {string.Join(", ", removed)})"), false);
            }
            _solution = _solution.WithProjectMetadataReferences(_projectId, current.MetadataReferences.Concat(
                fresh.MetadataReferences.Where(r => r is PortableExecutableReference p && !currentPaths.Contains(p.FilePath!))));
        }
        var analyzers = current.AnalyzerReferences.Select(r => r.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (fresh.AnalyzerReferences.Where(r => !analyzers.Contains(r.FullPath!)).ToList() is { Count: > 0 } added)
        {
            _solution = _solution.WithProjectAnalyzerReferences(_projectId, current.AnalyzerReferences.Concat(added));
        }
        foreach (var (source, subDirectory) in copyLocal)
        {
            var destination = Path.Combine(_baseDirectory, subDirectory, Path.GetFileName(source));
            if (_baseDirectory.Length > 0 && File.Exists(source) && !File.Exists(destination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
        }
        return (null, _solution != _committed);
    }

    private bool IsUserFile(string? path) => path is not null && ProjectGraph.IsUnder(path, _graph.ProjectDirectory) &&
        !ProjectGraph.IsUnder(path, Path.Combine(_graph.ProjectDirectory, "obj"));

    private string Format(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        var where = span.Path is { Length: > 0 } path
            ? $"{Path.GetRelativePath(_graph.ProjectDirectory, path)}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): "
            : "";
        return $"{where}{diagnostic.Id}: {diagnostic.GetMessage(System.Globalization.CultureInfo.CurrentUICulture)}";
    }

    /// <summary>
    /// Pins a document to the text the running app was compiled from, read now: the build's copy of the sources when
    /// it has one, the file otherwise. Read lazily, a file the developer saves before the first update would already
    /// be the "before" text, and the save would look like no change.
    /// </summary>
    private static DocumentInfo AsBuilt(DocumentInfo document, ProjectGraph graph)
    {
        if (document.FilePath is not { } path)
        {
            return document;
        }
        var copy = graph.AsBuiltCopy(path);
        var source = copy is not null && File.Exists(copy) ? copy : path;
        if (!File.Exists(source))
        {
            return document;
        }
        // From the file's bytes, so the checksum matches the one the compiler recorded.
        using var stream = File.OpenRead(source);
        var text = SourceText.From(stream, checksumAlgorithm: SourceHashAlgorithm.Sha256);
        return document.WithTextLoader(TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), path)));
    }

    /// <summary>
    /// A copy of the assembly and PDB the running app was built from: updates are computed against them, and another
    /// build (an IDE, a second terminal) may overwrite the originals in obj while the app runs.
    /// </summary>
    private static string SnapshotBaseline(string assembly, string directory)
    {
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
    /// Edit and continue opens the PDB at the path the assembly records (obj\...\App.pdb) before it looks next to the
    /// assembly, and a build while the app runs overwrites that one. The copy records the copied PDB instead (or, when
    /// that path doesn't fit, a path that doesn't exist, which falls back to it).
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
                var own = Encoding.UTF8.GetBytes(Path.ChangeExtension(assemblyCopy, ".pdb"));
                if (own.Length > room)
                {
                    own = Encoding.UTF8.GetBytes(Path.Combine(Path.GetPathRoot(assemblyCopy)!, "~winapp-moved", Path.GetFileNameWithoutExtension(assemblyCopy) + ".pdb"));
                }
                if (entry.DataPointer <= 0 || own.Length > room)
                {
                    continue;
                }
                Array.Clear(bytes, entry.DataPointer + header, entry.DataSize - header);
                own.CopyTo(bytes, entry.DataPointer + header);
            }
        }
        File.WriteAllBytes(assemblyCopy, bytes);
    }

    private static bool SamePath(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a file an editor may still hold open.</summary>
    internal static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < 10 && File.Exists(path))
            {
                await Task.Delay(30, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
