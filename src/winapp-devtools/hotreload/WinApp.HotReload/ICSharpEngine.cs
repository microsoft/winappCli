// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.HotReload;

/// <summary>
/// The only thing the XAML layer knows about C# hot reload. Engine B (<see cref="RoslynEngine"/>) implements it today;
/// a <c>dotnet watch</c> engine can implement it later without the XAML layer changing.
/// </summary>
internal interface ICSharpEngine
{
    /// <summary>
    /// Brings the running app up to date with the saved files (C# and the project file) plus the XAML compiler's
    /// generated C#. Returns applied, unchanged, error or restart (always with a reason), and the types that changed.
    /// </summary>
    Task<CSharpResult> ApplyAsync(CSharpChange change, CancellationToken cancellationToken);

    /// <summary>
    /// The app's types as a metadata-only assembly, with <paramref name="files"/> (saved .cs files not applied yet) as
    /// saved, so the XAML compiler sees members just added. Null when that code doesn't compile yet.
    /// </summary>
    Task<byte[]?> AppImageAsync(IReadOnlyList<string> files, CancellationToken cancellationToken);
}

/// <param name="Files">Saved files (full paths) the engine owns: the app's .cs files and its project file.</param>
/// <param name="Generated">The XAML compiler's C# for this save, by path (empty until the XAML layer exists).</param>
internal sealed record CSharpChange(IReadOnlyList<string> Files, IReadOnlyDictionary<string, string> Generated);

internal enum CSharpOutcome { Applied, Unchanged, Error, Restart }

/// <param name="Detail">The error or restart reason; for applied, what to note (one line each).</param>
/// <param name="ChangedTypes">Full names of the types the update changed in the app.</param>
internal sealed record CSharpResult(CSharpOutcome Outcome, string? Detail = null, IReadOnlyList<string>? ChangedTypes = null)
{
    public static CSharpResult Restart(string reason) => new(CSharpOutcome.Restart, reason);
    public static CSharpResult Error(string message) => new(CSharpOutcome.Error, message);
}

/// <summary>When no live engine can run (the SDK lacks its hot reload API, or the build is optimized): every C# save restarts.</summary>
internal sealed class RestartingEngine(string reason) : ICSharpEngine
{
    public string Reason => reason;

    public Task<CSharpResult> ApplyAsync(CSharpChange change, CancellationToken cancellationToken) =>
        Task.FromResult(CSharpResult.Restart(reason));

    public Task<byte[]?> AppImageAsync(IReadOnlyList<string> files, CancellationToken cancellationToken) =>
        Task.FromResult<byte[]?>(null);
}
