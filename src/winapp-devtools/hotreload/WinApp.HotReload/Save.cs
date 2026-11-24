// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.HotReload;

internal enum Outcome { Applied, Unchanged, Waiting, Error, Restart }

internal sealed record SaveResult(Outcome Outcome, string Detail, IReadOnlyList<string>? Notes = null);

/// <summary>
/// One save, end to end. Until the XAML layer arrives, a XAML save isn't applied: it says the change needs a restart on
/// this WinUI version (live XAML needs WinUI's hot reload runtime). Edits to referenced projects, the package manifest
/// and string resources restart the app with a reason. C# and the app's project file go through the engine seam.
/// </summary>
internal sealed class Save(ICSharpEngine engine, ProjectGraph graph)
{
    public async Task<SaveResult> RunAsync(IReadOnlyList<string> saved, CancellationToken cancellationToken)
    {
        var xaml = saved.Where(f => Is(f, ".xaml") && graph.LibraryOf(f) is null).ToList();
        var files = saved.Except(xaml).ToList();
        var xamlLine = xaml.Count == 0 ? null : $"{string.Join(", ", xaml.Select(Relative))}: XAML changes need a restart on this WinUI version";
        if (files.Count == 0)
        {
            return new SaveResult(Outcome.Unchanged, xamlLine!);
        }
        if (RestartReason(files) is { } reason)
        {
            // Nothing here has been compiled yet: the app is stopped only once its rebuild works.
            return await graph.CheckBuildAsync(cancellationToken).ConfigureAwait(false) is { } error
                ? new SaveResult(Outcome.Error, error)
                : new SaveResult(Outcome.Restart, reason);
        }
        var result = await engine.ApplyAsync(new CSharpChange(files, new Dictionary<string, string>()), cancellationToken).ConfigureAwait(false);
        // Nothing was compiled without a live engine, or when a project file change needs a rebuild.
        var compiled = engine is not RestartingEngine && !files.Contains(graph.ProjectFile, StringComparer.OrdinalIgnoreCase);
        if (result.Outcome == CSharpOutcome.Restart && !compiled && await graph.CheckBuildAsync(cancellationToken).ConfigureAwait(false) is { } buildError)
        {
            return new SaveResult(Outcome.Error, buildError);
        }
        var names = string.Join(", ", files.Select(Relative));
        var notes = (result.Detail?.Split(Environment.NewLine) ?? []).Concat(xamlLine is null ? [] : [xamlLine]).ToList();
        return result.Outcome switch
        {
            CSharpOutcome.Applied => new SaveResult(Outcome.Applied, names, notes.Count > 0 ? notes : null),
            CSharpOutcome.Unchanged => new SaveResult(Outcome.Unchanged, xamlLine ?? names),
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
