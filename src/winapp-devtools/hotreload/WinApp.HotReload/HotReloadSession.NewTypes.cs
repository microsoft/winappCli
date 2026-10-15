// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml.Linq;
using WinApp.HotReload.Protocol;

namespace WinApp.HotReload;

/// <summary>
/// New types while the app runs, on the compiler pipeline: a new page, UserControl or Themes\Generic.xaml is compiled like
/// any page (the XAML compiler service adds a .xaml file the build didn't list), and waits for its code-behind; a package or
/// project reference added to the project file reloads the compiler's references (RestartXamlCompilerWithCurrentReferencesAsync);
/// a referenced library's own XAML says restart.
/// </summary>
internal sealed partial class HotReloadSession
{
    /// <summary>New pages compiled before their code-behind declared their class: held back until it does.</summary>
    private readonly HashSet<string> _awaitingCodeBehind = new(StringComparer.OrdinalIgnoreCase);

    private bool InProject(string path) =>
        Path.GetFullPath(path).StartsWith(_projectDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// XAML files the build didn't compile. In the project, the compiler service takes them as new pages (a rebuild would
    /// compile them, so a missing class or member is the compiler's error, as it would be then). A referenced library's
    /// XAML is compiled into the library and its .pri, which this pipeline doesn't rebuild: that says restart.
    /// </summary>
    private async Task<SaveResult?> AddNewXamlAsync(IReadOnlyList<string> xaml, CancellationToken cancellationToken)
    {
        var added = xaml.Where(p => !_xamlInputs.Contains(p)).ToList();
        if (added.FirstOrDefault(p => !InProject(p)) is { } library)
        {
            return new SaveResult(Outcome.Restart, $"{Relative(library)}: changes to a referenced library take effect when the app restarts");
        }
        foreach (var path in added)
        {
            _xamlInputs.Add(path);
            // Its pass 1 declares a type the app's assembly image (pass 2's view of the app) doesn't have yet.
            _xamlImageStale = true;
            Log($"new XAML file: {Relative(path)}");
            if (XDocument.Parse(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false)).Root?
                .Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value is { } className &&
                !await HasWrittenPartAsync(className, cancellationToken).ConfigureAwait(false))
            {
                _awaitingCodeBehind.Add(path);
            }
        }
        return null;
    }

    /// <summary>
    /// After a clean compile: a new page whose class only the XAML compiler declares so far waits for its code-behind.
    /// Applied alone, its generated partial class is internal, and the code-behind's "public" would then change the
    /// visibility of a type the app already has (ENC0047, a restart). A compiler error is reported first, as a build would.
    /// </summary>
    private async Task<SaveResult?> AwaitingCodeBehindAsync(IReadOnlyList<string> xaml, CancellationToken cancellationToken)
    {
        var waiting = new List<string>();
        foreach (var path in xaml.Where(_awaitingCodeBehind.Contains).ToList())
        {
            var className = XDocument.Parse(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false)).Root?
                .Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value;
            if (className is null || await HasWrittenPartAsync(className, cancellationToken).ConfigureAwait(false))
            {
                _awaitingCodeBehind.Remove(path);
            }
            else
            {
                waiting.Add(path);
            }
        }
        return waiting.Count == 0 ? null : new SaveResult(Outcome.Waiting, string.Join(Environment.NewLine + "  ", waiting.Select(p =>
            $"{Relative(p)}: waiting for its code-behind ({Path.GetFileName(p)}.cs) to declare its class; they apply together")), waiting);
    }

    /// <summary>Whether the app's own code (not the XAML compiler's) declares part of the class.</summary>
    private async Task<bool> HasWrittenPartAsync(string className, CancellationToken cancellationToken) =>
        (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!.GetTypeByMetadataName(className) is { } type &&
        type.DeclaringSyntaxReferences.Any(r => !IsGenerated(r.SyntaxTree.FilePath));
}
