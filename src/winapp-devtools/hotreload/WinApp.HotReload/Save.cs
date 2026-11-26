// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using WinApp.HotReload.Protocol;

namespace WinApp.HotReload;

internal enum Outcome { Applied, Unchanged, NotApplied, Waiting, Error, Restart }

internal sealed record SaveResult(Outcome Outcome, string Detail, IReadOnlyList<string>? Notes = null);

/// <summary>
/// One save, end to end: the XAML compiler compiles the saved XAML, its generated C# and the saved C# go through the
/// engine seam, and the app hands WinUI the new markup and rebuilds what changed (the "reload" request). Without a
/// WinUI that can do that (<paramref name="xaml"/> null), a XAML save isn't applied: it says it needs a restart. Edits
/// to referenced projects, the package manifest and string resources restart the app with a reason.
/// </summary>
/// <param name="reload">Sends the reload request to the app; null without live XAML.</param>
/// <param name="xamlUnavailable">Why a WinUI that can apply XAML can't in this session; null on a WinUI that can't.</param>
internal sealed class Save(ICSharpEngine engine, ProjectGraph graph, XamlCompiler? xaml = null,
    Func<List<MarkupPayload>, CancellationToken, Task<HotReloadResponse>>? reload = null, string? xamlUnavailable = null)
{
    /// <summary>The x:Key values of App.xaml and dictionaries as the running app has them, to tell which a save removed.</summary>
    private readonly Dictionary<string, HashSet<string>> _keys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>C# was saved since the build: the XAML compiler checks x:Bind against the app's types as they are now.</summary>
    private bool _codeChanged;
    /// <summary>The attributes of each App.xaml's &lt;Application&gt; as the app started with them.</summary>
    private readonly Dictionary<string, string> _settings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the resource keys the app starts with (before any save changes the files).</summary>
    public void Start()
    {
        foreach (var file in xaml?.Files.Keys ?? [])
        {
            try
            {
                if (XDocument.Load(file) is { Root.Name.LocalName: "Application" or "ResourceDictionary" } document)
                {
                    _keys[file] = Keys(document);
                    _settings[file] = Settings(document);
                }
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
            {
                // A file that doesn't read keeps no keys: none counts as removed.
            }
        }
    }

    public async Task<SaveResult> RunAsync(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        // A WinUI that can't load XAML while the app runs: the save isn't applied, and says so. The rest of it is. (When
        // every save restarts, for example in Release, XAML restarts too.)
        if (xaml is null && engine is not RestartingEngine && files.Where(f => Is(f, ".xaml") && graph.LibraryOf(f) is null).ToList() is { Count: > 0 } stock)
        {
            var line = $"{string.Join(", ", stock.Select(Relative))}: " +
                (xamlUnavailable is null ? "XAML changes need a restart on this WinUI version (press Ctrl+R)" : xamlUnavailable);
            if (files.Except(stock).ToList() is not { Count: > 0 } rest)
            {
                return new SaveResult(Outcome.NotApplied, line);
            }
            var applied = await RunAsync(rest, cancellationToken).ConfigureAwait(false);
            return applied.Outcome switch
            {
                Outcome.Applied => applied with { Notes = [.. applied.Notes ?? [], line] },
                Outcome.Unchanged => new SaveResult(Outcome.NotApplied, line),
                _ => applied,
            };
        }
        var documents = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(f => Is(f, ".xaml") && File.Exists(f)))
        {
            try
            {
                documents[file] = XDocument.Parse(await RoslynEngine.ReadSharedAsync(file, cancellationToken).ConfigureAwait(false), LoadOptions.SetLineInfo);
            }
            catch (XmlException ex)
            {
                // A half-typed tag: a build would only fail.
                return new SaveResult(Outcome.Error, $"{Relative(file)}({ex.LineNumber},{ex.LinePosition}): invalid XML: {ex.Message}");
            }
        }
        if (RestartReason(files, documents) is { } reason)
        {
            return await RestartAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        var code = files.Where(f => Is(f, ".cs") || string.Equals(f, graph.ProjectFile, StringComparison.OrdinalIgnoreCase)).ToList();
        _codeChanged |= code.Count > 0;
        var compiled = new XamlResult(null, new Dictionary<string, string>(), []);
        if (xaml is not null && documents.Count > 0)
        {
            if (xaml.StateReplaced() is { } replaced)
            {
                // The compiler would continue from a build the running app doesn't have: nothing in this save is applied.
                return new SaveResult(Outcome.NotApplied, $"{string.Join(", ", documents.Keys.Select(Relative))}: {replaced}");
            }
            if (await NewPageWithoutCodeAsync(documents, cancellationToken).ConfigureAwait(false) is { } waiting)
            {
                return new SaveResult(Outcome.Waiting, waiting);
            }
            // Pass 1 doesn't need the image: it's built alongside.
            var image = _codeChanged ? engine.AppImageAsync(code.Where(f => Is(f, ".cs")).ToList(), cancellationToken) : Task.FromResult<byte[]?>(null);
            compiled = await xaml.CompileAsync([.. documents.Keys], image, engine.Has, cancellationToken).ConfigureAwait(false);
            if (compiled.Error is { } xamlError)
            {
                return new SaveResult(Outcome.Error, xamlError);
            }
            if (await RemovedNameInUseAsync(documents, compiled, files, cancellationToken).ConfigureAwait(false) is { } nameError)
            {
                return new SaveResult(Outcome.Error, nameError);
            }
        }
        var result = await engine.ApplyAsync(new CSharpChange(code, compiled.Generated), cancellationToken).ConfigureAwait(false);
        // Nothing was compiled without a live engine, or when a project file change needs a rebuild.
        var compiledCode = engine is not RestartingEngine && !files.Contains(graph.ProjectFile, StringComparer.OrdinalIgnoreCase);
        if (result.Outcome == CSharpOutcome.Restart)
        {
            var why = InGeneratedCodeRestart(result.Detail!);
            return compiledCode ? new SaveResult(Outcome.Restart, why) : await RestartAsync(why, cancellationToken).ConfigureAwait(false);
        }
        if (result.Outcome == CSharpOutcome.Error)
        {
            return xaml is null && UncompiledXaml(result.Detail!) is { } uncompiled
                ? new SaveResult(Outcome.NotApplied, $"{uncompiled}: " + (xamlUnavailable ?? "new XAML isn't compiled on this WinUI version (press Ctrl+R to restart)"))
                : InGeneratedCode(result.Detail!);
        }
        var names = string.Join(", ", files.Select(Relative));
        var notes = result.Detail?.Split(Environment.NewLine).ToList() ?? [];
        if (xaml is not null && files.Contains(graph.ProjectFile, StringComparer.OrdinalIgnoreCase))
        {
            // New packages or projects: markup can use their types from the next save on.
            xaml.UseReferences(graph.References());
        }
        if (reload is null || (result.Outcome == CSharpOutcome.Unchanged && compiled.Markup.Count == 0))
        {
            return new SaveResult(result.Outcome == CSharpOutcome.Applied ? Outcome.Applied : Outcome.Unchanged, names, notes);
        }

        // The app rebuilds the owners of what changed (and only then is this markup the app's).
        foreach (var payload in compiled.Markup.Where(m => m.Kind is "application" or "dictionary"))
        {
            if (FileOf(payload.Uri) is { } file && _keys.TryGetValue(file, out var before))
            {
                payload.DroppedKeys = [.. before.Except(Keys(documents[file]))];
            }
        }
        var response = await reload([.. compiled.Markup], cancellationToken).ConfigureAwait(false);
        if (!response.Ok)
        {
            return new SaveResult(Outcome.Restart, $"{names}: the app couldn't take the change ({response.Error}); it takes effect when the app restarts");
        }
        if (response.Restart is { } restart)
        {
            return await RestartAsync(restart, cancellationToken).ConfigureAwait(false);
        }
        if (response.Error is { } loadError)
        {
            return new SaveResult(Outcome.Error, LoadError(response.ErrorUri, loadError));
        }
        xaml?.Applied(compiled.Markup);
        foreach (var payload in compiled.Markup)
        {
            if (FileOf(payload.Uri) is { } file && documents.TryGetValue(file, out var document))
            {
                _keys[file] = Keys(document);
                if (payload.Kind == "application" && _settings.TryGetValue(file, out var before) && Settings(document) is var now && now != before)
                {
                    // Settings on <Application> itself only apply when the app starts.
                    notes.Add($"{Relative(file)}: settings on <Application> ({string.Join(", ", ChangedSettings(before, now))}) take effect when the app restarts");
                }
                _settings[file] = Settings(document);
            }
        }
        // A page built again runs its constructor and starts its fields over: what C# hot reload would say about objects
        // that already existed doesn't apply to it.
        var recreated = response.Notes.Select(n => n.Split(' ')[0]).ToHashSet(StringComparer.Ordinal);
        notes.RemoveAll(n => recreated.Any(r => n.StartsWith(r + "'s constructor", StringComparison.Ordinal) || n.StartsWith(r + ".", StringComparison.Ordinal) && n.EndsWith("in objects created before this save", StringComparison.Ordinal)));
        return new SaveResult(Outcome.Applied, names, [.. notes, .. response.Notes]);
    }

    /// <summary>Why this save can't go into the running app, for what isn't C# or XAML the app can load again.</summary>
    private string? RestartReason(IReadOnlyList<string> files, IReadOnlyDictionary<string, XDocument> documents)
    {
        foreach (var file in files)
        {
            var name = Relative(file);
            if (graph.LibraryOf(file) is { } library)
            {
                return $"{name}: edits to {Path.GetFileNameWithoutExtension(library)}, a project the app references, take effect when the app restarts";
            }
            if (Is(file, ".xaml"))
            {
                if (xaml is not null && !File.Exists(file))
                {
                    return $"{name}: a deleted XAML file takes effect when the app is rebuilt";
                }
                if (xaml is not null && documents.TryGetValue(file, out var document) && document.Root?.Name.LocalName == "Window")
                {
                    return $"{name}: a window's own XAML takes effect when the app restarts (put its content in a Page to keep edits live)";
                }
                continue;
            }
            if (Is(file, ".appxmanifest"))
            {
                return $"{name}: package manifest changes take effect when the app is registered again";
            }
            if (Is(file, ".resw"))
            {
                return $"{name}: string resources take effect when the app restarts";
            }
            if (!Is(file, ".cs") && !string.Equals(file, graph.ProjectFile, StringComparison.OrdinalIgnoreCase))
            {
                return $"{name}: build settings take effect when the app is rebuilt";
            }
        }
        return null;
    }

    /// <summary>A restart, once the rebuild works: the app is stopped only when its rebuild would start it again.</summary>
    private async Task<SaveResult> RestartAsync(string reason, CancellationToken cancellationToken)
    {
        // The compiler service keeps its state in obj, where the build writes too.
        xaml?.Stop();
        return await graph.CheckBuildAsync(cancellationToken).ConfigureAwait(false) is { } error
            ? new SaveResult(Outcome.Error, error)
            : new SaveResult(Outcome.Restart, reason);
    }

    /// <summary>
    /// A XAML file the build didn't compile is compiled like the others. A new page or UserControl waits for its
    /// code-behind to declare its class: applied alone, the class would be the compiler's internal one, and the
    /// code-behind's "public" would then be a change the running app can't take.
    /// </summary>
    private async Task<string?> NewPageWithoutCodeAsync(Dictionary<string, XDocument> documents, CancellationToken cancellationToken)
    {
        foreach (var (file, document) in documents.Where(d => !xaml!.Files.ContainsKey(d.Key)))
        {
            if (document.Root?.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value is { } className)
            {
                var name = className[(className.LastIndexOf('.') + 1)..];
                var code = file + ".cs";
                var declared = File.Exists(code) && CSharpSyntaxTree.ParseText(await RoslynEngine.ReadSharedAsync(code, cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken).GetRoot(cancellationToken).DescendantNodes().OfType<ClassDeclarationSyntax>().Any(c => c.Identifier.ValueText == name);
                if (!declared)
                {
                    return $"{Relative(file)}: waiting for {Path.GetFileName(code)} to declare {name}; they apply together";
                }
            }
            xaml!.Add(file);
            _codeChanged = true;
        }
        return null;
    }

    /// <summary>
    /// The compiler keeps a field for a name the XAML no longer has (a running app can't lose a field), so code that
    /// still uses the old name compiles here, then finds the field empty. A build fails instead (CS0103); so does this
    /// save, pointing at the code.
    /// </summary>
    private async Task<string?> RemovedNameInUseAsync(Dictionary<string, XDocument> documents, XamlResult compiled, IReadOnlyList<string> saved,
        CancellationToken cancellationToken)
    {
        foreach (var (file, document) in documents)
        {
            var stem = Path.ChangeExtension(Path.Combine(xaml!.Intermediate, Path.GetRelativePath(graph.ProjectDirectory, file)), null);
            if (!compiled.Generated.TryGetValue(stem + ".i.g.cs", out var pass1))
            {
                continue;
            }
            var names = XamlCompiler.Names(document);
            var removed = CSharpSyntaxTree.ParseText(pass1, cancellationToken: cancellationToken).GetRoot(cancellationToken).DescendantNodes().OfType<FieldDeclarationSyntax>()
                .Where(f => f.AttributeLists.SelectMany(l => l.Attributes).Any(a => a.Name.ToString().Contains("GeneratedCode", StringComparison.Ordinal)))
                .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.ValueText))
                .Where(n => n != "_contentLoaded" && !names.Contains(n)).ToHashSet(StringComparer.Ordinal);
            if (removed.Count == 0)
            {
                continue;
            }
            foreach (var code in saved.Where(f => Is(f, ".cs")).Append(file + ".cs").Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
            {
                var tree = CSharpSyntaxTree.ParseText(await RoslynEngine.ReadSharedAsync(code, cancellationToken).ConfigureAwait(false), path: code, cancellationToken: cancellationToken);
                var use = tree.GetRoot(cancellationToken).DescendantNodes().OfType<IdentifierNameSyntax>().FirstOrDefault(id =>
                    removed.Contains(id.Identifier.ValueText) &&
                    !(id.Parent is MemberAccessExpressionSyntax access && access.Name == id && access.Expression is not ThisExpressionSyntax) &&
                    !DeclaredLocally(id));
                if (use is not null)
                {
                    var position = use.GetLocation().GetLineSpan().StartLinePosition;
                    return $"{Relative(code)}({position.Line + 1},{position.Character + 1}): CS0103: The name '{use.Identifier.ValueText}' does not exist in the current context " +
                        $"({Relative(file)} no longer names an element '{use.Identifier.ValueText}')";
                }
            }
        }
        return null;

        // A local or parameter of the same name hides the field.
        static bool DeclaredLocally(IdentifierNameSyntax id) =>
            id.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>() is { } method &&
            (method.ParameterList.Parameters.Any(p => p.Identifier.ValueText == id.Identifier.ValueText) ||
                method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Any(v => v.Identifier.ValueText == id.Identifier.ValueText));
    }

    /// <summary>
    /// A change .NET can't make to the running app, found in the XAML compiler's generated code
    /// ("MainPage.g.cs(41,9): ENC0033: Deleting field 'obj4' ..."): said about the XAML file the code came from.
    /// </summary>
    private string InGeneratedCodeRestart(string detail)
    {
        var match = Regex.Match(detail, @"^(?<file>[^()]+\.g\.cs)\(\d+,\d+\): (?<code>ENC\d+): (?<message>.*)$");
        return match.Success && xaml?.XamlFileOf(Path.Combine(graph.ProjectDirectory, match.Groups["file"].Value)) is { } file
            ? $"{file}: the running app can't take this markup change, so it takes effect when the app restarts ({match.Groups["code"].Value}: {match.Groups["message"].Value})"
            : detail;
    }

    /// <summary>
    /// A C# error in the XAML compiler's generated code is about the XAML line that made it. A name the code doesn't
    /// have yet (Click="OnWave" before OnWave is written) means the files don't agree yet: waiting, not an error.
    /// </summary>
    private SaveResult InGeneratedCode(string detail)
    {
        var lines = detail.Split(Environment.NewLine + "  ");
        var waiting = true;
        for (var i = 0; i < lines.Length; i++)
        {
            var match = Regex.Match(lines[i], @"^(?<file>[^()]+\.g\.cs)\((?<line>\d+),\d+\): (?<code>CS\d+): (?<message>.*)$");
            if (match.Success && xaml?.XamlLocation(Path.GetFullPath(Path.Combine(graph.ProjectDirectory, match.Groups["file"].Value)), int.Parse(match.Groups["line"].Value,
                System.Globalization.CultureInfo.InvariantCulture)) is { } location)
            {
                lines[i] = $"{location}: {match.Groups["code"].Value}: {match.Groups["message"].Value}";
                waiting &= match.Groups["code"].Value is "CS1061" or "CS0103" or "CS0117";
            }
            else
            {
                waiting = false;
            }
        }
        var text = string.Join(Environment.NewLine + "  ", lines);
        return waiting ? new SaveResult(Outcome.Waiting, $"{text} (the XAML uses code that isn't there yet)") : new SaveResult(Outcome.Error, text);
    }

    /// <summary>"Failed to create a 'Thickness' from the text '1,2,3,4,5'. [Line: 5 Position: 34]" as "Page.xaml(5,34): Failed to ...".</summary>
    private string LoadError(string? uri, string message)
    {
        var file = uri is null ? null : FileOf(uri);
        var match = Regex.Match(message, @"\s*\[Line: (?<line>\d+) Position: (?<column>\d+)\]\s*$");
        var where = file is null ? "" : match.Success ? $"{Relative(file)}({match.Groups["line"].Value},{match.Groups["column"].Value}): " : $"{Relative(file)}: ";
        return where + (match.Success ? message[..match.Index] : message) + " The app is unchanged.";
    }

    private string? FileOf(string uri) =>
        xaml?.Files.FirstOrDefault(p => string.Equals("ms-appx:///" + p.Value, uri, StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>The attributes of the root element (RequestedTheme="Dark"), without namespace declarations and x:Class.</summary>
    private static string Settings(XDocument document) =>
        string.Join("\n", document.Root!.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.NamespaceName.Length == 0).Select(a => $"{a.Name.LocalName}={a.Value}"));

    private static IEnumerable<string> ChangedSettings(string before, string after) =>
        before.Split('\n').Concat(after.Split('\n')).Where(s => s.Length > 0).Except(before.Split('\n').Intersect(after.Split('\n'))).Select(s => s.Split('=')[0]).Distinct();

    private static HashSet<string> Keys(XDocument document) =>
        document.Descendants().Select(e => e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// On a WinUI that can't load XAML while the app runs: the code-behind file whose error is only XAML that wasn't
    /// compiled, a new page's <c>InitializeComponent</c> or an <c>x:Name</c> its XAML declares. Only a rebuild compiles it.
    /// </summary>
    private string? UncompiledXaml(string errors)
    {
        foreach (Match match in Regex.Matches(errors, @"^\s*(?<file>.+\.xaml\.cs)\(\d+,\d+\): CS0103: The name '(?<name>\w+)'", RegexOptions.Multiline))
        {
            var file = match.Groups["file"].Value;
            var name = match.Groups["name"].Value;
            var page = Path.Combine(graph.ProjectDirectory, file[..^".cs".Length]);
            if (name == "InitializeComponent" || (File.Exists(page) && File.ReadAllText(page).Contains($"Name=\"{name}\"", StringComparison.Ordinal)))
            {
                return file;
            }
        }
        return null;
    }

    private string Relative(string path) => Path.GetRelativePath(graph.ProjectDirectory, path);

    private static bool Is(string path, string extension) => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}
