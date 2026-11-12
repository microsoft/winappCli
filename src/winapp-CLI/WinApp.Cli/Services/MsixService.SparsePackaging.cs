// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security;
using System.Text;
using System.Xml.Linq;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Sparse identity package production: creating the identity-only .msix from a sparse
/// manifest, resolving its output path, and embedding the &lt;msix&gt; identity into an app.
/// Split out of MsixService.Identity.cs (which retains the debug/loose-layout identity flow)
/// to keep each file within the repository's file-size guidance.
/// </summary>
internal partial class MsixService
{
    /// <summary>
    /// Classification of a manifest's sparse-identity status, distinguishing a valid non-sparse
    /// manifest from one that could not be parsed so callers can report each case correctly.
    /// </summary>
    public enum SparseManifestKind
    {
        /// <summary>Parsed successfully and does NOT declare AllowExternalContent.</summary>
        NotSparse,
        /// <summary>Parsed successfully and declares &lt;uap10:AllowExternalContent&gt;true.</summary>
        Sparse,
        /// <summary>The content could not be parsed as XML (malformed / invalid manifest).</summary>
        ParseError,
    }

    /// <summary>
    /// Classifies whether a manifest declares a &lt;uap10:AllowExternalContent&gt;true&lt;/uap10:AllowExternalContent&gt;
    /// element (a sparse identity package), returning <see cref="SparseManifestKind.ParseError"/> with the
    /// parser message in <paramref name="parseError"/> when the content is not valid manifest XML — so a
    /// malformed file is not silently reported as a valid non-sparse manifest.
    /// </summary>
    public static SparseManifestKind ClassifySparseManifest(string manifestContent, out string? parseError)
    {
        parseError = null;
        try
        {
            return AppxManifestDocument.Parse(manifestContent).AllowsExternalContent
                ? SparseManifestKind.Sparse
                : SparseManifestKind.NotSparse;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException or InvalidOperationException or ArgumentException)
        {
            parseError = ex.Message;
            return SparseManifestKind.ParseError;
        }
    }

    /// <summary>
    /// Returns true when the manifest content declares a &lt;uap10:AllowExternalContent&gt;true&lt;/uap10:AllowExternalContent&gt; element,
    /// indicating a sparse identity package whose binaries/assets live at an external location.
    /// Malformed manifests are treated as non-sparse; use <see cref="ClassifySparseManifest"/> to
    /// distinguish a parse failure from a valid non-sparse manifest.
    /// </summary>
    public static bool ManifestHasAllowExternalContent(string manifestContent)
        => ClassifySparseManifest(manifestContent, out _) == SparseManifestKind.Sparse;

    /// <summary>
    /// Returns warning messages for content found in a folder being packaged as a sparse
    /// (AllowExternalContent) identity package. Assets and binaries should be deployed at the
    /// external location alongside the application rather than inside the identity-only .msix.
    /// Returns an empty list when the manifest is not a sparse manifest.
    /// </summary>
    public static IReadOnlyList<string> GetSparseFolderContentWarnings(DirectoryInfo inputFolder, string manifestContent)
    {
        var warnings = new List<string>();
        if (!ManifestHasAllowExternalContent(manifestContent))
        {
            return warnings;
        }

        var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".ico" };
        var binaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".so" };

        var files = inputFolder.EnumerateFiles("*", SearchOption.AllDirectories).ToList();

        if (files.Any(f => imageExtensions.Contains(f.Extension)))
        {
            warnings.Add($"{UiSymbols.Warning} Assets found in package folder. For sparse packages, assets should be deployed at the external location alongside your application, not inside the .msix.");
        }
        if (files.Any(f => binaryExtensions.Contains(f.Extension)))
        {
            warnings.Add($"{UiSymbols.Warning} Binaries found in package folder. Sparse packages are identity-only — application binaries should not be included in the .msix.");
        }

        return warnings;
    }

    /// <summary>
    /// Normalizes a sparse identity manifest so a manifest supplied directly as a file (e.g. one
    /// produced by an older template, or hand-edited) is corrected to the same rules folder packing
    /// applies, plus the identity fields required for a valid sparse package. Without this, packing a
    /// manifest file staged it verbatim and could ship a package with an incorrect
    /// RuntimeBehavior (e.g. <c>packagedClassicApp</c>), a missing ProcessorArchitecture, or a
    /// MinVersion below the 10.0.19041.0 required by AllowExternalContent.
    ///
    /// Corrections applied (all no-ops when the manifest is already correct, e.g. one freshly
    /// generated by <c>winapp init --exe --sparse</c>):
    /// <list type="bullet">
    ///   <item>Ensures the uap10/desktop6 namespaces used by sparse attributes are declared.</item>
    ///   <item>For an .exe application, forces <c>uap10:RuntimeBehavior="win32App"</c> and
    ///   <c>uap10:TrustLevel="mediumIL"</c>.</item>
    ///   <item>Removes <c>EntryPoint</c> (not valid for a win32App sparse identity).</item>
    ///   <item>Sets Identity <c>ProcessorArchitecture="neutral"</c> when missing/empty.</item>
    ///   <item>Raises any TargetDeviceFamily <c>MinVersion</c> below 10.0.19041.0 to that floor.</item>
    /// </list>
    /// <paramref name="corrections"/> receives a human-readable description of each change actually
    /// made, so callers can report exactly what was normalized.
    /// </summary>
    internal static string NormalizeSparseIdentityManifest(string manifestContent, out IReadOnlyList<string> corrections)
    {
        var doc = AppxManifestDocument.Parse(manifestContent);
        var messages = new List<string>();

        // Namespaces required by the uap10:*/desktop6:* attributes the corrections rely on.
        doc.EnsureNamespace("uap10", AppxManifestDocument.Uap10Ns);
        doc.EnsureNamespace("desktop6", AppxManifestDocument.Desktop6Ns);

        var executable = doc.ApplicationExecutable;
        bool isExe = !string.IsNullOrEmpty(executable)
            && string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase);

        var app = doc.GetFirstApplicationElement();
        if (app != null && isExe)
        {
            var behavior = app.Attribute(AppxManifestDocument.Uap10Ns + "RuntimeBehavior")?.Value;
            var trust = app.Attribute(AppxManifestDocument.Uap10Ns + "TrustLevel")?.Value;
            if (!string.Equals(behavior, "win32App", StringComparison.Ordinal))
            {
                app.SetAttributeValue(AppxManifestDocument.Uap10Ns + "RuntimeBehavior", "win32App");
                messages.Add(string.IsNullOrEmpty(behavior)
                    ? "set RuntimeBehavior to 'win32App'"
                    : $"corrected RuntimeBehavior '{behavior}' to 'win32App'");
            }
            if (!string.Equals(trust, "mediumIL", StringComparison.Ordinal))
            {
                app.SetAttributeValue(AppxManifestDocument.Uap10Ns + "TrustLevel", "mediumIL");
                messages.Add(string.IsNullOrEmpty(trust)
                    ? "set TrustLevel to 'mediumIL'"
                    : $"corrected TrustLevel '{trust}' to 'mediumIL'");
            }
        }

        // EntryPoint is not valid for a win32App sparse identity package.
        if (doc.ApplicationEntryPoint != null)
        {
            doc.ApplicationEntryPoint = null;
            messages.Add("removed EntryPoint");
        }

        // Identity-only packages carry no architecture-specific binaries, so a missing/empty
        // ProcessorArchitecture must default to 'neutral' rather than being left unset.
        if (string.IsNullOrWhiteSpace(doc.IdentityProcessorArchitecture))
        {
            doc.IdentityProcessorArchitecture = "neutral";
            messages.Add("set ProcessorArchitecture to 'neutral'");
        }

        // AllowExternalContent requires Windows 10 2004 (10.0.19041.0); raise any lower MinVersion.
        messages.AddRange(RaiseSparseTargetDeviceFamilyMinVersion(doc));

        corrections = messages;
        return doc.ToXml();
    }

    /// <summary>
    /// The minimum OS version a sparse (<c>AllowExternalContent</c>) package supports. Windows 10
    /// 2004 (build 19041) is the first release that honors external content, so any lower
    /// TargetDeviceFamily MinVersion produces a package that packs under MakeAppx /nv yet is
    /// rejected at deployment.
    /// </summary>
    internal const string SparseMinVersion = "10.0.19041.0";

    /// <summary>
    /// Raises any <c>TargetDeviceFamily/@MinVersion</c> below <see cref="SparseMinVersion"/> to that
    /// floor, returning a human-readable description of each change made. Shared by the manifest-file
    /// normalization path and the folder-packing sparse rewrite so both enforce the same floor.
    /// </summary>
    private static List<string> RaiseSparseTargetDeviceFamilyMinVersion(AppxManifestDocument doc)
    {
        var messages = new List<string>();
        var minFloor = new Version(10, 0, 19041, 0);
        var dependencies = doc.Document.Root?.Element(AppxManifestDocument.DefaultNs + "Dependencies");
        if (dependencies == null)
        {
            return messages;
        }

        foreach (var tdf in dependencies.Elements(AppxManifestDocument.DefaultNs + "TargetDeviceFamily"))
        {
            var minValue = tdf.Attribute("MinVersion")?.Value;
            if (Version.TryParse(minValue, out var current) && current < minFloor)
            {
                tdf.SetAttributeValue("MinVersion", SparseMinVersion);
                var familyName = tdf.Attribute("Name")?.Value ?? "TargetDeviceFamily";
                messages.Add($"raised {familyName} MinVersion '{minValue}' to '{SparseMinVersion}'");
            }
        }

        return messages;
    }

    public async Task<CreateMsixPackageResult> CreateSparseIdentityPackageAsync(
        FileInfo manifestPath,
        FileSystemInfo? outputPath,
        TaskContext taskContext,
        bool autoSign = false,
        FileInfo? certificatePath = null,
        string certificatePassword = "password",
        bool generateDevCert = false,
        bool installDevCert = false,
        string? publisher = null,
        IfExists certIfExists = IfExists.Overwrite,
        CancellationToken cancellationToken = default)
    {
        if (!manifestPath.Exists)
        {
            throw new FileNotFoundException($"Sparse manifest not found at: {manifestPath}");
        }

        var manifestContent = await File.ReadAllTextAsync(manifestPath.FullName, Encoding.UTF8, cancellationToken);
        var doc = AppxManifestDocument.Parse(manifestContent);

        if (!doc.AllowsExternalContent)
        {
            throw new InvalidOperationException(
                "The manifest does not declare <uap10:AllowExternalContent>true</uap10:AllowExternalContent> under <Properties>, so it is not a sparse identity package. " +
                "Generate one with 'winapp init --exe <exe> --sparse', or pass an input folder to package a full MSIX.");
        }

        // MakeAppx packages the sparse manifest with /nv (no semantic validation), so a
        // structurally invalid manifest — a missing Identity Name/Publisher or Application Id —
        // would otherwise pack (and possibly sign) "successfully" yet be rejected at deployment.
        // Reuse the shared required-identity/application parser to fail fast with a clear error.
        var identity = ParseAppxManifestAsync(manifestContent);

        var packageName = ManifestService.CleanPackageName(identity.PackageName);
        var extractedPublisher = publisher ?? identity.Publisher;

        // Resolve output path: default to <PackageName>.identity.msix in the current directory.
        var defaultFileName = $"{packageName}.identity.msix";
        var (outputMsixPath, outputFolder) = ResolveSparseOutputPath(
            outputPath, defaultFileName, currentDirectoryProvider.GetCurrentDirectoryInfo());

        if (!outputFolder.Exists)
        {
            outputFolder.Create();
        }

        // Stage a directory containing ONLY the manifest. Sparse identity packages carry no
        // binaries or assets — those are resolved from the external content location at runtime.
        var stagingDir = new DirectoryInfo(Path.Join(Path.GetTempPath(), $"winapp-sparse-{Guid.NewGuid():N}"));
        stagingDir.Create();
        try
        {
            var stagedManifest = Path.Join(stagingDir.FullName, "appxmanifest.xml");

            // Normalize the manifest to the same rules folder packing applies (plus the identity
            // fields a sparse package requires) instead of staging it verbatim, so a manifest from
            // an older template or a hand-edit cannot ship with an incorrect RuntimeBehavior,
            // a missing ProcessorArchitecture, or a MinVersion below 10.0.19041.0.
            var normalizedManifest = NormalizeSparseIdentityManifest(manifestContent, out var corrections);
            await File.WriteAllTextAsync(stagedManifest, normalizedManifest, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

            foreach (var correction in corrections)
            {
                taskContext.AddStatusMessage($"{UiSymbols.Info} Normalized sparse manifest: {correction}");
            }

            taskContext.AddDebugMessage($"{UiSymbols.Package} Packaging sparse identity manifest (external content): {manifestPath.Name}");

            if (autoSign && generateDevCert && certificatePath == null)
            {
                ThrowIfGeneratedCertExists(GetGeneratedCertPath(outputFolder, packageName), certIfExists);
            }

            await CreateMsixPackageFromFolderAsync(stagingDir, outputMsixPath, taskContext, cancellationToken);

            if (autoSign)
            {
                await SignMsixPackageAsync(outputFolder, certificatePassword, generateDevCert, installDevCert, packageName, extractedPublisher, outputMsixPath, certificatePath, manifestPath, taskContext, cancellationToken, certIfExists: certIfExists);
            }
        }
        finally
        {
            try
            {
                if (stagingDir.Exists)
                {
                    stagingDir.Delete(recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not clean up staging directory: {stagingDir.FullName} ({ex.Message})");
            }
        }

        return new CreateMsixPackageResult(outputMsixPath, autoSign);
    }

    /// <summary>
    /// Resolves where a sparse identity .msix should be written. An existing directory (even one
    /// whose name contains a dot) is used as the output folder; a non-existent path with an
    /// extension is treated as a file and must be '.msix' (bundles are rejected); anything else is
    /// treated as a target directory.
    /// </summary>
    internal static (FileInfo OutputMsix, DirectoryInfo OutputFolder) ResolveSparseOutputPath(
        FileSystemInfo? outputPath, string defaultFileName, DirectoryInfo currentDirectory)
    {
        // defaultFileName is a bare file name derived from the package name. Reject anything rooted or
        // nested so Path.Combine below can never drop the target directory and write somewhere else.
        if (string.IsNullOrWhiteSpace(defaultFileName)
            || Path.IsPathRooted(defaultFileName)
            || defaultFileName != Path.GetFileName(defaultFileName))
        {
            throw new InvalidOperationException(
                $"Internal error: sparse output file name must be a bare file name, not a path: '{defaultFileName}'.");
        }

        // Validated above to be a bare file name; use it consistently so the combines below are
        // unambiguously relative to the target directory.
        var safeFileName = Path.GetFileName(defaultFileName);

        if (outputPath == null)
        {
            return (new FileInfo(Path.Join(currentDirectory.FullName, safeFileName)), currentDirectory);
        }

        if (Directory.Exists(outputPath.FullName))
        {
            // An existing directory is always treated as the output folder, even if its name
            // contains a dot (e.g. './release.v2') that Path.HasExtension would misread as a file.
            var dir = new DirectoryInfo(outputPath.FullName);
            return (new FileInfo(Path.Join(dir.FullName, safeFileName)), dir);
        }

        if (Path.HasExtension(outputPath.Name))
        {
            // An extension on a non-existent path means the caller intends a file. Only .msix is
            // valid for a sparse identity package — reject .msixbundle and anything else rather
            // than silently creating a directory with that name.
            if (!string.Equals(Path.GetExtension(outputPath.Name), ".msix", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Invalid --output '{outputPath.Name}'. Sparse identity packages must be a single '.msix' file " +
                    "(bundles are not supported). Pass a '.msix' path or a directory.");
            }

            var file = new FileInfo(outputPath.FullName);
            return (file, file.Directory!);
        }

        var folder = new DirectoryInfo(outputPath.FullName);
        return (new FileInfo(Path.Join(folder.FullName, safeFileName)), folder);
    }

    public async Task<MsixIdentityResult> EmbedIdentityAsync(
        FileInfo target,
        FileInfo manifestPath,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        if (!manifestPath.Exists)
        {
            throw new FileNotFoundException(
                $"AppX manifest not found at: {manifestPath}. Pass --manifest, or generate one with 'winapp init --exe <exe> --sparse'.");
        }

        var manifestContent = await File.ReadAllTextAsync(manifestPath.FullName, Encoding.UTF8, cancellationToken);

        // embed-identity connects an app to a sparse identity package (one registered with
        // 'Add-AppxPackage -ExternalLocation'), which requires AllowExternalContent. Refuse a
        // non-sparse manifest so we don't embed an identity that can never be registered that way.
        if (!AppxManifestDocument.Parse(manifestContent).AllowsExternalContent)
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath.Name}' is not a sparse identity manifest (missing <uap10:AllowExternalContent>true). " +
                "embed-identity only applies to sparse packages registered with 'Add-AppxPackage -ExternalLocation'. " +
                "Generate one with 'winapp init --exe <exe> --sparse'.");
        }

        var identity = ParseAppxManifestAsync(manifestContent);

        var extension = target.Extension.ToLowerInvariant();
        if (extension == ".exe")
        {
            if (!target.Exists)
            {
                throw new FileNotFoundException($"Executable not found at: {target}");
            }

            taskContext.AddDebugMessage($"Embedding <msix> identity into exe fusion manifest: {target.Name}");
            await EmbedMsixIdentityToExeAsync(target, identity, taskContext, cancellationToken);
        }
        else
        {
            taskContext.AddDebugMessage($"Inserting <msix> identity into external SxS manifest: {target.Name}");
            await EmbedIdentityIntoXmlManifestAsync(target, identity, cancellationToken);
        }

        return identity;
    }

    /// <summary>
    /// Inserts or replaces the &lt;msix&gt; identity element in an external side-by-side manifest
    /// XML file. Creates a minimal assembly manifest if the target file does not yet exist.
    /// </summary>
    private static async Task EmbedIdentityIntoXmlManifestAsync(FileInfo target, MsixIdentityResult identity, CancellationToken cancellationToken)
    {
        XDocument xdoc;
        if (target.Exists)
        {
            var existing = await File.ReadAllTextAsync(target.FullName, cancellationToken);
            xdoc = XDocument.Parse(existing);
        }
        else
        {
            xdoc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(AsmV1Ns + "assembly", new XAttribute("manifestVersion", "1.0")));
        }

        var root = xdoc.Root
            ?? throw new InvalidOperationException($"The manifest '{target.FullName}' has no root element.");

        // Only touch genuine side-by-side (fusion) manifests. Refuse to append <msix> to an
        // arbitrary .xml file (e.g. app.config) that happens to be passed as the target.
        if (root.Name != AsmV1Ns + "assembly")
        {
            throw new InvalidOperationException(
                $"The target '{target.FullName}' is not a side-by-side manifest (its root is <{root.Name.LocalName}>, expected <assembly xmlns=\"{AsmV1Ns.NamespaceName}\">). " +
                "Point embed-identity at your app's .manifest/.xml side-by-side manifest.");
        }

        // Remove any existing <msix> element(s) so re-running the command is idempotent.
        root.Elements(MsixV1Ns + "msix").Remove();

        // The fusion manifest must carry a top-level <assemblyIdentity> for Windows to grant
        // identity (see the MS "grant identity to non-packaged apps" docs). Add one if the file
        // (new or existing) has none at the root — ignoring nested/dependency identities.
        if (!root.Elements(AsmV1Ns + "assemblyIdentity").Any())
        {
            root.AddFirst(new XElement(AsmV1Ns + "assemblyIdentity",
                new XAttribute("version", "1.0.0.0"),
                new XAttribute("name", identity.PackageName),
                new XAttribute("type", "win32")));
        }

        var msix = new XElement(MsixV1Ns + "msix",
            new XAttribute("publisher", identity.Publisher),
            new XAttribute("packageName", identity.PackageName),
            new XAttribute("applicationId", identity.ApplicationId));
        root.Add(msix);

        // Write to a uniquely named sibling file first, then atomically move it over the target.
        // Saving directly with FileMode.Create truncates an existing hand-authored manifest up
        // front, so a cancellation or I/O error mid-save could leave the original empty or partial.
        var tempPath = Path.Join(
            target.DirectoryName ?? Directory.GetCurrentDirectory(),
            $".{target.Name}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                await xdoc.SaveAsync(stream, SaveOptions.None, cancellationToken);
            }

            File.Move(tempPath, target.FullName, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup; ignore expected I/O/permission failures removing the temp file.
                }
            }

            throw;
        }
    }
}
