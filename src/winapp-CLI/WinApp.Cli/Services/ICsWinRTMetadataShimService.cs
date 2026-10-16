// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// SHIM (temporary): auto-resolves a <c>CsWinRTWindowsMetadata</c> folder for project-mode builds on
/// hosts without the Windows SDK version the project targets, so C#/WinRT authoring projects (anything
/// importing <c>Microsoft.Windows.CsWinRT</c>) can build off the <c>Microsoft.Windows.SDK.NET.Ref</c> NuGet
/// ref-pack winmds instead of a bare SDK version that <c>cswinrt.exe</c> would fail to resolve.
/// Remove once the upstream <c>Microsoft.Windows.CsWinRT.targets</c> default is fixed.
/// See <see cref="CsWinRTMetadataShimService"/>.
/// </summary>
internal interface ICsWinRTMetadataShimService
{
    /// <summary>
    /// Returns the absolute path of a folder of contract winmds to inject as
    /// <c>-p:CsWinRTWindowsMetadata=&lt;folder&gt;</c>, or <c>null</c> when no injection is needed
    /// (the targeted Windows SDK is installed) or possible (the ref pack is not restored). Never throws for
    /// a missing ref pack — it no-ops so the normal build error can surface.
    /// </summary>
    /// <param name="targetFrameworkMoniker">
    /// The project's target framework moniker (e.g. <c>net10.0-windows10.0.19041.0</c>) used to check
    /// whether that SDK version is installed and to prefer a matching ref-pack version; may be <c>null</c>,
    /// in which case any registered SDK counts as installed and the highest ref-pack version is used.
    /// </param>
    /// <param name="projectWindowsMetadata">
    /// The project's evaluated <c>CsWinRTWindowsMetadata</c>, when read. A bare SDK version is checked like
    /// the default; <c>local</c> or a winmd folder is the project's own choice and is never overridden.
    /// </param>
    string? ResolveMetadataFolder(string? targetFrameworkMoniker, string? projectWindowsMetadata = null);

    /// <summary>
    /// True when cswinrt can't read the Windows SDK this build asks for: no SDK is registered, or the
    /// version it would read isn't installed. This is when the shim WOULD inject the ref-pack winmd folder
    /// if it were restored. Callers use this to decide whether to read the project's own
    /// <c>CsWinRTWindowsMetadata</c>, and whether an explicit <c>dotnet restore</c> (to populate
    /// <c>Microsoft.Windows.SDK.NET.Ref</c>) is worth doing before resolving the folder, so the FIRST build
    /// on such a host isn't handed a missing folder.
    /// </summary>
    /// <param name="targetFrameworkMoniker">See <see cref="ResolveMetadataFolder"/>.</param>
    /// <param name="projectWindowsMetadata">See <see cref="ResolveMetadataFolder"/>.</param>
    bool IsWindowsSdkAbsent(string? targetFrameworkMoniker, string? projectWindowsMetadata = null);
}
