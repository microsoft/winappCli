// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

/// <summary>
/// Where a run puts its loose layout, together with whether winapp owns that directory.
/// </summary>
/// <remarks>
/// The two travel as one value because they are only correct together. The path alone cannot say
/// who created the directory: <c>winapp run .</c> and
/// <c>winapp run . --output-appx-directory &lt;input&gt;\AppX</c> name the same folder and mean
/// different things. Carrying them separately would let a future call site pass one and forget the
/// other, and the failure mode of that mistake is deleting a developer's files.
/// </remarks>
/// <param name="Directory">
/// The directory the caller named, or <see langword="null"/> to use the generated default.
/// </param>
/// <param name="Reconciliation">How much of that directory this run may remove.</param>
internal readonly record struct LayoutOutput(DirectoryInfo? Directory, LayoutReconciliation Reconciliation)
{
    /// <summary>No directory was named, so winapp generates and owns one.</summary>
    public static LayoutOutput Generated { get; } = new(null, LayoutReconciliation.Exact);

    /// <summary>A directory the user typed. Copied into, never deleted from.</summary>
    public static LayoutOutput UserSupplied(DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return new LayoutOutput(directory, LayoutReconciliation.Additive);
    }

    /// <summary>
    /// A directory winapp created for this deployment and names explicitly, because the default
    /// would be the wrong place — the guest registration layout beside a deployed payload.
    /// </summary>
    public static LayoutOutput WinappManaged(DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return new LayoutOutput(directory, LayoutReconciliation.Exact);
    }

    /// <summary>Fills in <paramref name="generatedDefault"/> when no directory was named.</summary>
    public DirectoryInfo Resolve(Func<DirectoryInfo> generatedDefault)
    {
        ArgumentNullException.ThrowIfNull(generatedDefault);
        return Directory ?? generatedDefault();
    }
}
