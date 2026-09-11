// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// How much of an existing loose-layout directory a materialization may remove.
/// </summary>
/// <remarks>
/// This is decided by the caller, not inferred from the path, because the only thing that makes
/// deletion safe is knowing who created the directory — and that is knowable at the call site and
/// nowhere else. Two runs can name the same directory, one by default and one with an explicit
/// <c>--output-appx-directory</c>; the second may be a folder the developer keeps other files in,
/// and no amount of inspection distinguishes those files from ones a previous build left behind.
/// </remarks>
internal enum LayoutReconciliation
{
    /// <summary>
    /// A staging directory winapp created for this operation and will delete afterwards. Nothing in
    /// it predates the run, so there is nothing to reconcile.
    /// </summary>
    /// <remarks>
    /// Used by MSIX and bundle packaging. These stage under the system temp directory, which on some
    /// machines is reached through a junction, so the link checks that protect a long-lived layout
    /// do not apply here — the directory is winapp's own, brand new, and never pruned.
    /// </remarks>
    None,

    /// <summary>
    /// A directory the caller named with <c>--output-appx-directory</c>. Files are copied in and
    /// nothing is ever removed, because winapp cannot tell its own leftovers from the developer's
    /// files.
    /// </summary>
    Additive,

    /// <summary>
    /// A directory winapp itself created for this deployment and keeps for the next run: the
    /// <c>AppX</c> directory generated next to the build output when the caller did not name one,
    /// and the guest registration layout the host creates alongside a deployed payload. winapp
    /// creates it, nothing else writes to it, and MSBuild does not clean it, so it can be made to
    /// match the build exactly.
    /// </summary>
    Exact,
}
