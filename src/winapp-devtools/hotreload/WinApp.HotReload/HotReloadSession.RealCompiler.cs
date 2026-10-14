// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using WinApp.HotReload.Protocol;

namespace WinApp.HotReload;

/// <summary>
/// SPIKE (S1/S2): XAML saves go through the real XAML compiler instead of the text transform. Each save builds the project
/// (its obj, so Prototype A's connection ID ledger carries over; output to a side folder, since the running app holds bin),
/// takes the compiler's regenerated .g.cs/.g.i.cs into the Roslyn workspace, sends the edit-and-continue delta and the
/// compiler's .xbf as binary markup, and has the app run InitializeComponent again. Nothing is rewritten.
/// </summary>
internal sealed partial class HotReloadSession
{
    public static bool RealCompiler { get; } = Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_REAL_COMPILER") == "1";

    private static readonly string? SpikeLog = Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_SPIKE_LOG");

    private static int s_spikeSave;

    /// <summary>The documents' text as the running binary was built from, pinned: the rebuilds overwrite the files in obj.</summary>
    private static DocumentInfo Pinned(DocumentInfo document)
    {
        if (document.FilePath is not { } path || !File.Exists(path))
        {
            return document;
        }
        using var stream = File.OpenRead(path);
        var text = SourceText.From(stream, checksumAlgorithm: SourceHashAlgorithm.Sha256);
        return document.WithTextLoader(TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), path)));
    }

    /// <summary>The XAML compiler's output folder: where the generated XamlTypeInfo.g.cs is.</summary>
    private string IntermediateDirectory() =>
        Path.GetDirectoryName(Project.Documents.Select(d => d.FilePath).OfType<string>()
            .First(p => p.EndsWith("XamlTypeInfo.g.cs", StringComparison.OrdinalIgnoreCase)))!;

    private static bool IsGenerated(string path) =>
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>The app's own project file (a package or project reference added): Directory.Build files still restart.</summary>
    private bool IsAppProjectFile(string path) => string.Equals(Path.GetFullPath(path), Path.GetFullPath(_options.ProjectFile), StringComparison.OrdinalIgnoreCase);

    /// <summary>NEW TYPES: the .xbf content the app last got, by .xbf path; first the launch build's (obj before any save).</summary>
    private Dictionary<string, string>? _sentXbf;

    private static Dictionary<string, string> XbfSnapshot(string intermediate) =>
        Directory.EnumerateFiles(intermediate, "*.xbf", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))), StringComparer.OrdinalIgnoreCase);

    private async Task<SaveResult> ProcessRealCompilerAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        var save = Math.Max(Interlocked.Increment(ref s_spikeSave), SpikeLog is { Length: > 0 } && Directory.Exists(SpikeLog) ? Directory.GetDirectories(SpikeLog).Length + 1 : 0);
        s_spikeSave = save;
        var report = new StringBuilder();
        var logDirectory = SpikeLog is { Length: > 0 } ? Path.Combine(SpikeLog, $"{save:00}-{string.Join("+", paths.Select(Path.GetFileName))}") : null;
        var intermediate = IntermediateDirectory();
        report.AppendLine($"save {save}: {string.Join(", ", paths.Select(Relative))}");

        // 1. The user's own sources, as saved.
        if (paths.FirstOrDefault(p => !Path.GetFullPath(p).StartsWith(_projectDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) is { } outside)
        {
            // NEW TYPES: a referenced library's code and markup are compiled into its own assembly and .pri, which this
            // path doesn't rebuild or reload yet. Saying so beats an "applied" that changed nothing.
            report.AppendLine("library file: " + outside);
            WriteSpikeReport(logDirectory, report);
            return new SaveResult(Outcome.Restart, $"{Relative(outside)}: changes to a referenced library take effect when the app restarts");
        }
        foreach (var path in paths.Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && IsSource(p) && !IsGenerated(p)))
        {
            var ids = _solution.GetDocumentIdsWithFilePath(path);
            if (File.Exists(path))
            {
                var text = SourceText.From(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false), Encoding.UTF8, SourceHashAlgorithm.Sha256);
                _solution = ids.IsEmpty
                    ? _solution.AddDocument(DocumentId.CreateNewId(_projectId), Path.GetFileName(path), text, FoldersFor(path), path)
                    : ids.Aggregate(_solution, (s, id) => s.WithDocumentText(id, text));
            }
            else
            {
                _solution = ids.Aggregate(_solution, (s, id) => s.RemoveDocument(id));
            }
        }

        // 2. The real compiler, into the project's obj (Prototype A's ledger lives there).
        if (paths.Any(IsAppProjectFile))
        {
            // NEW TYPES: a package or project reference added. Restore, re-read the references into the workspace (the
            // compiler's type universe is the build's, so the msbuild below sees them too), and deploy the new assemblies
            // and their .pri next to the running app, as the rewrite path does.
            var referenceWatch = System.Diagnostics.Stopwatch.StartNew();
            var referencesError = await RefreshReferencesAsync(restore: true, [], cancellationToken).ConfigureAwait(false);
            report.AppendLine($"references: {referenceWatch.ElapsedMilliseconds} ms, new assemblies: {(_newAssemblies.Count == 0 ? "none" : string.Join(", ", _newAssemblies))}");
            if (referencesError is not null)
            {
                report.AppendLine("references failed: " + referencesError);
                WriteSpikeReport(logDirectory, report);
                return new SaveResult(Outcome.Error, referencesError);
            }
        }
        _sentXbf ??= XbfSnapshot(intermediate);
        var built = File.Exists(_options.CscArgsFile + ".props")
            ? File.ReadAllLines(_options.CscArgsFile + ".props").Where(line => line.Contains('=') && !line.EndsWith('=')).ToList()
            : [];
        var before = Project.Documents.Where(d => d.FilePath is { } p && p.StartsWith(intermediate, StringComparison.OrdinalIgnoreCase) && IsGenerated(p))
            .ToDictionary(d => d.FilePath!, d => d, StringComparer.OrdinalIgnoreCase);
        if (logDirectory is not null)
        {
            foreach (var (path, document) in before)
            {
                var copy = Path.Combine(logDirectory, "before", Path.GetRelativePath(intermediate, path));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                await File.WriteAllTextAsync(copy, (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString(), cancellationToken).ConfigureAwait(false);
            }
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // Only the XAML compiler's targets: CoreCompile would overwrite obj's PDB, which edit and continue reads (through the
        // path recorded in the baseline assembly) to tell whether the baseline documents match the running binary.
        if (await MSBuildAsync(["msbuild", _options.ProjectFile, "-restore:false", "-nologo", "-v:q", "-t:PrepareForBuild;ResolveReferences;PrepareResources", .. built.Select(line => $"-p:{line}")],
            "the build", cancellationToken).ConfigureAwait(false) is { } buildError)
        {
            report.AppendLine("build failed: " + buildError);
            WriteSpikeReport(logDirectory, report);
            return new SaveResult(Outcome.Error, buildError);
        }
        report.AppendLine($"build: {watch.ElapsedMilliseconds} ms");

        // 3. The compiler's generated C#, as documents of the workspace.
        var changedGenerated = new List<string>();
        var current = Directory.EnumerateFiles(intermediate, "*.cs", SearchOption.AllDirectories).Where(IsGenerated)
            .Where(p => !Path.GetRelativePath(intermediate, p).Contains(Path.DirectorySeparatorChar + "refint" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var path in current)
        {
            var text = SourceText.From(SimulatedCompilerChange(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false)), Encoding.UTF8, SourceHashAlgorithm.Sha256);
            if (before.TryGetValue(path, out var document))
            {
                if (!(await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ContentEquals(text))
                {
                    _solution = _solution.WithDocumentText(document.Id, text);
                    changedGenerated.Add(path);
                }
            }
            else
            {
                _solution = _solution.AddDocument(DocumentId.CreateNewId(_projectId), Path.GetFileName(path), text, filePath: path);
                changedGenerated.Add(path + " (new)");
            }
            if (logDirectory is not null)
            {
                var copy = Path.Combine(logDirectory, "after", Path.GetRelativePath(intermediate, path));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(path, copy, overwrite: true);
            }
        }
        foreach (var (path, document) in before.Where(b => !current.Contains(b.Key, StringComparer.OrdinalIgnoreCase)))
        {
            _solution = _solution.RemoveDocument(document.Id);
            changedGenerated.Add(path + " (removed)");
        }
        report.AppendLine("generated documents changed: " + (changedGenerated.Count == 0 ? "none" : string.Join(", ", changedGenerated.Select(p => Path.GetRelativePath(intermediate, p)))));

        // 4. Edit and continue, as is.
        var result = await UpdatesAsync(cancellationToken).ConfigureAwait(false);
        report.AppendLine($"edit and continue: status {result.Status}, {result.Deltas.Count} delta(s){(result.Deltas.Count > 0 ? $" ({result.Deltas.Sum(d => d.UpdatedTypes.Length)} updated types, IL {result.Deltas.Sum(d => d.IL.Length)} bytes)" : "")}, needs restart: {result.NeedsRestart}");
        foreach (var diagnostic in result.Errors)
        {
            report.AppendLine("  error " + Format(diagnostic));
        }
        foreach (var diagnostic in result.RudeEdits)
        {
            report.AppendLine("  rude edit " + Format(diagnostic));
        }
        foreach (var diagnostic in _enc.LastWarnings)
        {
            report.AppendLine($"  warning {diagnostic.Id} {Format(diagnostic)}");
        }
        if (result.Errors.Count > 0 || result.RudeEdits.Count > 0 || result.NeedsRestart)
        {
            Discard();
            WriteSpikeReport(logDirectory, report);
            var reasons = result.Errors.Concat(result.RudeEdits).Select(Format).Distinct().ToList();
            return new SaveResult(result.Errors.Count > 0 ? Outcome.Error : Outcome.Restart,
                reasons.Count > 0 ? string.Join(Environment.NewLine + "  ", reasons) : "the change can't be applied to the running app");
        }

        // 5. The compiler's .xbf for each page whose markup changed since the app last got it (a saved page, and one
        //    whose earlier save failed), and the page runs InitializeComponent again.
        var request = new HotReloadRequest { Op = "apply", Assemblies = [.. _newAssemblies] };
        request.Deltas.AddRange(result.Deltas.Select(d => new DeltaPayload { ModuleId = d.ModuleId, Metadata = d.Metadata, IL = d.IL, Pdb = d.Pdb, UpdatedTypes = d.UpdatedTypes }));
        var sending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (xbf, hash) in XbfSnapshot(intermediate))
        {
            if (_sentXbf!.TryGetValue(xbf, out var sent) && sent == hash)
            {
                continue;
            }
            var path = Path.Combine(_projectDirectory, Path.ChangeExtension(Path.GetRelativePath(intermediate, xbf), ".xaml"));
            if (!File.Exists(path))
            {
                report.AppendLine($"no source for {xbf}");
                continue;
            }
            sending[xbf] = hash;
            var root = System.Xml.Linq.XDocument.Load(path).Root;
            var className = root?.Attribute(System.Xml.Linq.XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value;
            // NEW TYPES: a Themes/Generic.xaml holds default styles; the runtime reads it through the markup override.
            var generic = className is null && string.Equals(Path.GetRelativePath(_projectDirectory, path), Path.Combine("Themes", "Generic.xaml"), StringComparison.OrdinalIgnoreCase);
            request.Markup.Add(new MarkupPayload { Kind = generic ? MarkupKind.Generic : MarkupKind.Component, Uri = UriFor(path), Xbf = await File.ReadAllBytesAsync(xbf, cancellationToken).ConfigureAwait(false), Manifest = new ComponentManifest { ClassName = className ?? "" } });
            _sources[UriFor(path)] = path;
            if (className is not null)
            {
                // SPIKE: WINAPP_HOT_RELOAD_SPIKE_RECREATE=1 re-creates pages (new instance, state lost) instead of re-initializing them in place.
                (Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_SPIKE_RECREATE") == "1" ? request.RecreateTypes : request.RefreshTypes).Add(className);
            }
            report.AppendLine($"markup: {UriFor(path)} ({new FileInfo(xbf).Length} bytes .xbf){(generic ? ", default styles" : className is null ? "" : $", {className} re-initialized")}");
        }
        HotReloadResponse response;
        try
        {
            response = await _agent.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Discard();
            report.AppendLine("lost the app: " + ex.Message);
            WriteSpikeReport(logDirectory, report);
            return new SaveResult(Outcome.Restart, "lost the connection to the app: " + ex.Message);
        }
        if (!response.Ok)
        {
            Discard();
            report.AppendLine("the app refused: " + response.Error);
            WriteSpikeReport(logDirectory, report);
            return new SaveResult(Outcome.Restart, response.Error ?? "the app could not apply the update");
        }
        Commit();
        foreach (var (xbf, hash) in sending)
        {
            _sentXbf![xbf] = hash;
        }
        _newAssemblies.Clear();
        foreach (var line in response.Errors.Concat(response.Messages))
        {
            report.AppendLine("  app: " + line);
        }
        WriteSpikeReport(logDirectory, report);
        var detail = string.Join(", ", paths.Select(Relative)) + $" [real compiler: {changedGenerated.Count} generated file(s) changed, {result.Deltas.Sum(d => d.UpdatedTypes.Length)} type(s) updated]";
        return response.Errors.Count > 0
            ? new SaveResult(Outcome.Error, string.Join(Environment.NewLine + "  ", response.Errors.Select(SourcePaths)))
            : new SaveResult(Outcome.Applied, detail + string.Concat(response.Messages.Select(m => Environment.NewLine + "  " + m)));
    }

    /// <summary>
    /// SPIKE: simulates a one-line XAML compiler change. Roslyn 5.9 crashes (NRE in DefinitionMap.GetPreviousMethodHandle)
    /// when an update adds an initialized static field to a type that an earlier update added; the compiler's
    /// "private static bool isobjNTextDisabled = false;" initializer is redundant. Off with WINAPP_HOT_RELOAD_SPIKE_RAW=1.
    /// </summary>
    private static string SimulatedCompilerChange(string text) =>
        Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_SPIKE_RAW") == "1"
            ? text
            : System.Text.RegularExpressions.Regex.Replace(text, @"(private static bool isobj\w+Disabled) = false;", "$1;");

    private static void WriteSpikeReport(string? logDirectory, StringBuilder report)
    {
        if (logDirectory is not null)
        {
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(Path.Combine(logDirectory, "result.txt"), report.ToString());
        }
    }
}
