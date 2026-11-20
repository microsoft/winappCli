// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml;
using System.Xml.Linq;

namespace WinApp.HotReload;

internal enum Outcome { Applied, Unchanged, Waiting, Error, Restart }

internal sealed record SaveResult(Outcome Outcome, string Detail);

/// <summary>
/// One save, end to end. Until the XAML layer arrives, a XAML save restarts the app with a reason (live XAML needs
/// WinUI's hot reload runtime), as do edits to referenced projects, the package manifest and string resources. C# and
/// the app's project file go through the engine seam.
/// </summary>
internal sealed class Save(ICSharpEngine engine, ProjectGraph graph)
{
    public async Task<SaveResult> RunAsync(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        foreach (var xaml in files.Where(f => Is(f, ".xaml") && File.Exists(f)))
        {
            try
            {
                XDocument.Parse(await RoslynEngine.ReadSharedAsync(xaml, cancellationToken).ConfigureAwait(false));
            }
            catch (XmlException ex)
            {
                // A half-typed tag: the rebuild would only fail.
                return new SaveResult(Outcome.Error, $"{Relative(xaml)}({ex.LineNumber},{ex.LinePosition}): {ex.Message}");
            }
        }
        if (RestartReason(files) is { } reason)
        {
            return new SaveResult(Outcome.Restart, reason);
        }
        var result = await engine.ApplyAsync(new CSharpChange(files, new Dictionary<string, string>()), cancellationToken).ConfigureAwait(false);
        var names = string.Join(", ", files.Select(Relative));
        return result.Outcome switch
        {
            CSharpOutcome.Applied => new SaveResult(Outcome.Applied, names),
            CSharpOutcome.Unchanged => new SaveResult(Outcome.Unchanged, names),
            CSharpOutcome.Error => new SaveResult(Outcome.Error, result.Detail!),
            _ => new SaveResult(Outcome.Restart, result.Detail!),
        };
    }

    /// <summary>Why this save can't go into the running app, for what isn't C# in the app.</summary>
    private string? RestartReason(IReadOnlyList<string> files)
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
                return $"{name}: XAML changes take effect when the app restarts (this app's WinUI can't reload XAML while it runs)";
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

    private string Relative(string path) => Path.GetRelativePath(graph.ProjectDirectory, path);

    private static bool Is(string path, string extension) => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}
