// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Discovers the <c>.winmd</c> metadata and XML documentation a project
/// references — from NuGet (<c>project.assets.json</c> / <c>packages.config</c>),
/// project references, the Windows SDK UnionMetadata, and the WinAppSDK runtime.
/// </summary>
internal static partial class NuGetResolver
{
    public static List<PackageWithWinMd> FindPackagesWithWinMd(string projectDir, string projectFile, string? winAppSdkRuntimePath, Action<string>? warn = null)
    {
        var packages = new List<PackageWithWinMd>();

        string? assetsPath = FindProjectAssetsJson(projectDir, projectFile);
        string? targetPlatformVersion = null;

        if (assetsPath != null)
        {
            packages.AddRange(FindPackagesFromAssets(assetsPath, warn, projectDir));
            targetPlatformVersion = ReadTargetPlatformVersion(assetsPath);
        }

        // A C++ project has no project.assets.json, so its target comes from the project
        // file itself. Without this it reads as "no target declared" and SDK selection
        // falls back to the newest installed Windows Kit — which confirms APIs that do
        // not exist at the version the project actually compiles against.
        targetPlatformVersion ??= ReadTargetPlatformVersionFromProjectFile(projectFile);
        if (packages.Count == 0)
        {
            string configPath = Path.Combine(projectDir, "packages.config");
            if (File.Exists(configPath))
            {
                packages.AddRange(FindPackagesFromConfig(configPath, projectDir));
            }
        }

        // A project with no MSBuild project file — an Electron or other non-.NET app
        // driven by winapp.yaml — has no project.assets.json to read. `winapp restore`
        // records the same information for it in .winapp/winmds.lock.json, so read that
        // instead. Without this the project indexes to nothing and every query typed in
        // it reports the API surface as absent.
        if (packages.Count == 0)
        {
            packages.AddRange(FindPackagesFromWinmdsLockfile(projectDir));
        }

        packages.AddRange(FindWinMdFromProjectReferences(projectFile));

        // The machine's installed WinAppSDK runtime metadata is part of a project's
        // compile surface only when the project actually references WinAppSDK. Adding it
        // unconditionally answers a plain .NET or Win32 project from WinAppSDK metadata
        // it does not build against, and pins it to whatever runtime happens to be
        // installed rather than anything the project declares.
        string? referencedWinAppSdkVersion = ReferencedWinAppSdkVersion(packages);
        packages.AddRange(CollectSdkPackages(
            winAppSdkRuntimePath,
            targetPlatformVersion,
            referencedWinAppSdkVersion is not null,
            warn,
            referencedWinAppSdkVersion));

        return Deduplicate(packages);
    }

    /// <summary>
    /// The Windows platform version a project targets, read from the target framework
    /// moniker in <c>project.assets.json</c> (<c>net8.0-windows10.0.26100.0</c> yields
    /// <c>10.0.26100.0</c>). Null when the project targets no Windows platform version.
    /// </summary>
    internal static string? ReadTargetPlatformVersion(string assetsPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
            JsonElement root = doc.RootElement;

            var monikers = new List<string>();
            if (root.TryGetProperty("targets", out var targetsEl) && targetsEl.ValueKind == JsonValueKind.Object)
            {
                monikers.AddRange(targetsEl.EnumerateObject().Select(p => p.Name));
            }
            if (root.TryGetProperty("project", out var projectEl)
                && projectEl.TryGetProperty("frameworks", out var frameworksEl)
                && frameworksEl.ValueKind == JsonValueKind.Object)
            {
                monikers.AddRange(frameworksEl.EnumerateObject().Select(p => p.Name));
            }

            // The highest version wins, matching the target whose compile assets are
            // read (see SelectWindowsTarget). Taking the first moniker instead would let
            // a multi-targeted project read 26100 package assets against 19041 SDK
            // metadata and report a 26100 API as missing.
            return monikers
                .Select(moniker => WindowsPlatformMoniker.Match(moniker))
                .Where(match => match.Success)
                .Select(match => match.Groups[1].Value)
                .Where(platform => Version.TryParse(platform, out _))
                .OrderByDescending(Version.Parse)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable assets file just means the target version is unknown, and
            // SDK selection falls back to the newest installed.
        }
        return null;
    }

    /// <summary>
    /// The Windows platform version an MSBuild project declares directly, from
    /// <c>&lt;WindowsTargetPlatformVersion&gt;</c> (a C++ project) or
    /// <c>&lt;TargetFramework(s)&gt;</c> (a .NET project whose restore output is absent).
    /// Read from the project file because a C++ project has no
    /// <c>project.assets.json</c> to carry it.
    /// </summary>
    /// <remarks>
    /// The highest declared version wins, matching <see cref="ReadTargetPlatformVersion"/>.
    /// A property with a <c>Condition</c> is read like any other: this needs the version
    /// the project targets, not a full MSBuild evaluation, and every configuration of a
    /// real project targets the same Windows SDK.
    /// </remarks>
    internal static string? ReadTargetPlatformVersionFromProjectFile(string projectFile)
    {
        try
        {
            XDocument doc = XDocument.Load(projectFile);
            XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            var declared = doc.Descendants(ns + "WindowsTargetPlatformVersion")
                .Select(e => e.Value.Trim())
                .Where(v => v.Length > 0)
                .ToList();

            // A .NET project reached here only when restore has not run yet, but the
            // moniker it declares is still the right answer.
            declared.AddRange(doc.Descendants()
                .Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(moniker => WindowsPlatformMoniker.Match(moniker))
                .Where(match => match.Success)
                .Select(match => match.Groups[1].Value));

            return declared
                .Where(v => Version.TryParse(v, out _))
                .OrderByDescending(Version.Parse)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            // An unreadable project file just means the target version is unknown, and
            // SDK selection falls back to the newest installed.
        }
        return null;
    }

    private static readonly Regex WindowsPlatformMoniker = WindowsPlatformMonikerRegex();

    [GeneratedRegex(@"-windows(\d+\.\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPlatformMonikerRegex();

    private static readonly Regex WindowsTargetMoniker = WindowsTargetMonikerRegex();

    /// <summary>
    /// Any Windows target framework moniker, including the two-component forms
    /// (<c>net8.0-windows7.0</c>, <c>net8.0-windows10.0</c>) that name no Windows SDK
    /// version. Those are not usable for SDK metadata selection — hence the separate,
    /// stricter <see cref="WindowsPlatformMoniker"/> — but they are still the target a
    /// Windows build compiles against, so package assets must be read from them.
    /// </summary>
    [GeneratedRegex(@"-windows(\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsTargetMonikerRegex();

    /// <summary>
    /// The machine-wide metadata that needs no project at all: the Windows SDK
    /// UnionMetadata and the installed WinAppSDK runtime, plus their XML docs from
    /// the NuGet global cache. This is what backs <c>find-api</c>'s SDK scope when
    /// a query runs outside any project, so it must not consult
    /// <c>project.assets.json</c>, <c>packages.config</c>, or project references.
    /// </summary>
    public static List<PackageWithWinMd> FindSdkPackages(string? winAppSdkRuntimePath) =>
        Deduplicate(CollectSdkPackages(winAppSdkRuntimePath));

    private static List<PackageWithWinMd> CollectSdkPackages(
        string? winAppSdkRuntimePath,
        string? preferredSdkVersion = null,
        bool includeWinAppSdkRuntime = true,
        Action<string>? warn = null,
        string? requiredRuntimeRelease = null)
    {
        var packages = new List<PackageWithWinMd>();

        (List<string> Files, string Version) sdk = FindWindowsSdkWinMd(preferredSdkVersion, warn);
        if (sdk.Files.Count > 0)
        {
            packages.Add(new PackageWithWinMd("WindowsSDK", sdk.Version, sdk.Files, new List<string>()));
        }

        if (includeWinAppSdkRuntime)
        {
            (List<string> Files, string Version) runtime = FindWinAppSdkRuntimeWinMd(winAppSdkRuntimePath);
            // The installed runtime is machine-wide and detection picks the newest one,
            // but a project compiles against the release it references. Indexing a 2.4
            // runtime for a project on WinAppSDK 1.8 makes the index confirm types that
            // release does not have, and an agent then writes code that will not build.
            if (runtime.Files.Count > 0 && !RuntimeMatchesRelease(runtime.Version, requiredRuntimeRelease))
            {
                warn?.Invoke(
                    $"Installed Windows App Runtime {runtime.Version} does not match the referenced " +
                    $"Windows App SDK {requiredRuntimeRelease}; its metadata is excluded from this project's API surface.");
            }
            else if (runtime.Files.Count > 0)
            {
                packages.Add(new PackageWithWinMd("WinAppSdkRuntime", runtime.Version, runtime.Files, new List<string>()));
            }
        }

        DiscoverSdkXmlDocs(packages);
        return packages;
    }

    /// <summary>
    /// Whether an installed runtime's release label (<c>2.4</c>, <c>1.8</c>) belongs to the
    /// same <c>major.minor</c> release as the Windows App SDK version a project references
    /// (<c>1.8.260222000</c>). A null requirement means the caller is building the
    /// machine-wide SDK scope, which is not tied to any project and takes the runtime as-is.
    /// An unrecognizable label on either side is accepted rather than silently dropping
    /// metadata a project may well need.
    /// </summary>
    internal static bool RuntimeMatchesRelease(string runtimeRelease, string? referencedSdkVersion)
    {
        if (referencedSdkVersion is null)
        {
            return true;
        }
        string? runtime = MajorMinor(runtimeRelease);
        string? referenced = MajorMinor(referencedSdkVersion);
        if (runtime is null || referenced is null)
        {
            return true;
        }
        return string.Equals(runtime, referenced, StringComparison.Ordinal);
    }

    /// <summary>The leading <c>major.minor</c> of a version-like string, or null.</summary>
    private static string? MajorMinor(string value)
    {
        // Both sides can carry a prerelease suffix — the runtime folder for an
        // experimental release is labelled `2.0-experimental3` — and the digits before
        // it are still the release. Without this, the minor fails to parse, the label
        // reads as unrecognizable, and the mismatch is waved through.
        string[] parts = value.Split('-')[0].Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
        {
            return null;
        }
        return major.ToString(CultureInfo.InvariantCulture) + "." + minor.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whether a package id is WinAppSDK itself or one of its sub-packages
    /// (<c>Microsoft.WindowsAppSDK.Foundation</c>, <c>.WinUI</c>, and friends).
    /// </summary>
    private static bool IsWinAppSdkPackage(string id) =>
        id.Equals("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("Microsoft.WindowsAppSDK.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The WinAppSDK version a project builds against, or <see langword="null"/> when it
    /// references none.
    /// </summary>
    /// <remarks>
    /// The umbrella <c>Microsoft.WindowsAppSDK</c> package carries no <c>.winmd</c>, so it
    /// never reaches this list and cannot be read for the release. The sub-packages that do
    /// carry metadata drift apart within one release — 2.2.0 resolves Foundation 2.1.0,
    /// InteractiveExperiences 2.0.15, and WinUI 2.2.1 — so taking whichever happens to come
    /// first would compare an installed 2.2 runtime against "2.0" and drop the very runtime
    /// the project builds against. The newest of them is the release.
    /// </remarks>
    internal static string? ReferencedWinAppSdkVersion(IEnumerable<PackageWithWinMd> packages) =>
        packages
            .Where(package => IsWinAppSdkPackage(package.Id))
            .Select(package => package.Version)
            .OrderByDescending(ReleaseSortKey)
            .FirstOrDefault();

    /// <summary>Orders versions by release; an unrecognizable one sorts last.</summary>
    private static (int Major, int Minor) ReleaseSortKey(string version)
    {
        string[] parts = (MajorMinor(version) ?? string.Empty).Split('.');
        return parts.Length == 2
            ? (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture))
            : (-1, -1);
    }

    private static List<PackageWithWinMd> Deduplicate(List<PackageWithWinMd> packages) =>
        packages
            .GroupBy(p => (p.Id.ToLowerInvariant(), p.Version.ToLowerInvariant()))
            .Select(g =>
            {
                var winMdFiles = g.SelectMany(p => p.WinMdFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var xmlDocFiles = g.SelectMany(p => p.XmlDocFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var first = g.First();
                return new PackageWithWinMd(first.Id, first.Version, winMdFiles, xmlDocFiles);
            })
            .ToList();

    /// <summary>
    /// Finds XML documentation files in a NuGet package folder that contains a
    /// <c>metadata</c> directory (WinUI pattern) or <c>lib</c> directory.
    /// </summary>
    internal static List<string> FindXmlDocsInPackageFolder(string packageFolder)
    {
        var xmlFiles = new List<string>();
        try
        {
            string metadataDir = Path.Combine(packageFolder, "metadata");
            if (Directory.Exists(metadataDir))
            {
                xmlFiles.AddRange(Directory.GetFiles(metadataDir, "*.xml"));
            }

            string libDir = Path.Combine(packageFolder, "lib");
            if (Directory.Exists(libDir))
            {
                foreach (var xml in GetFilesNoReparse(libDir, "*.xml"))
                {
                    try
                    {
                        // Skip trivial (< 1KB) XML files that carry no real docs.
                        if (new FileInfo(xml).Length > 1024)
                        {
                            xmlFiles.Add(xml);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A file that vanished or is not readable simply contributes no docs.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Doc discovery is best-effort: an unreadable package folder yields no docs
            // rather than failing the whole index.
        }
        return xmlFiles;
    }

    /// <summary>
    /// Orders NuGet version folder names semantically rather than lexically, so
    /// <c>1.10.x</c> sorts above <c>1.9.x</c>. Ties are broken toward the stable
    /// release, because <see cref="NugetService.CompareVersions"/> ignores the
    /// prerelease suffix and would otherwise leave <c>2.3.3</c> and
    /// <c>2.3.3-experimental</c> in arbitrary directory order.
    /// </summary>
    private static readonly IComparer<string?> VersionOrder = Comparer<string?>.Create((a, b) =>
    {
        if (a is null || b is null)
        {
            return a is null ? (b is null ? 0 : -1) : 1;
        }
        int cmp = NugetService.CompareVersions(a, b);
        if (cmp != 0)
        {
            return cmp;
        }
        bool aPre = a.Contains('-', StringComparison.Ordinal);
        bool bPre = b.Contains('-', StringComparison.Ordinal);
        return aPre == bPre ? 0 : (aPre ? -1 : 1);
    });

    /// <summary>
    /// Discovers XML documentation from well-known SDK NuGet packages
    /// (<c>microsoft.windows.sdk.net.ref</c>, <c>microsoft.windowsappsdk.winui</c>)
    /// that provide WinRT API docs, and attaches them to the matching package.
    /// </summary>
    private static void DiscoverSdkXmlDocs(List<PackageWithWinMd> packages)
    {
        string nugetPackagesDir = GetNuGetPackagesDir();

        string sdkRefDir = Path.Combine(nugetPackagesDir, "microsoft.windows.sdk.net.ref");
        if (Directory.Exists(sdkRefDir))
        {
            try
            {
                var latest = Directory.GetDirectories(sdkRefDir)
                    .OrderByDescending(Path.GetFileName, VersionOrder)
                    .FirstOrDefault();
                if (latest != null)
                {
                    var xmlDocs = FindXmlDocsInPackageFolder(latest);
                    if (xmlDocs.Count > 0)
                    {
                        var sdkPkg = packages.FirstOrDefault(p => p.Id.Equals("WindowsSDK", StringComparison.OrdinalIgnoreCase));
                        sdkPkg?.XmlDocFiles.AddRange(xmlDocs);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable SDK ref folder simply contributes no XML docs.
            }
        }

        string winuiDir = Path.Combine(nugetPackagesDir, "microsoft.windowsappsdk.winui");
        if (Directory.Exists(winuiDir))
        {
            try
            {
                var latest = Directory.GetDirectories(winuiDir)
                    .OrderByDescending(Path.GetFileName, VersionOrder)
                    .FirstOrDefault();
                if (latest != null)
                {
                    var xmlDocs = FindXmlDocsInPackageFolder(latest);
                    if (xmlDocs.Count > 0)
                    {
                        var runtimePkg = packages.FirstOrDefault(p => p.Id.Equals("WinAppSdkRuntime", StringComparison.OrdinalIgnoreCase))
                            ?? packages.FirstOrDefault(p =>
                                p.Id.Contains("WinUI", StringComparison.OrdinalIgnoreCase) ||
                                p.Id.Contains("WindowsAppSDK", StringComparison.OrdinalIgnoreCase));
                        runtimePkg?.XmlDocFiles.AddRange(xmlDocs);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable WinUI package folder simply contributes no XML docs.
            }
        }
    }

    internal static List<PackageWithWinMd> FindWinMdFromProjectReferences(string projectFile)
    {
        var packages = new List<PackageWithWinMd>();
        try
        {
            XDocument doc = XDocument.Load(projectFile);
            XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            string projectDir = Path.GetDirectoryName(projectFile)!;

            // Resolved paths, so the two sources below agree on identity: the same project
            // is spelled `..\Lib\Lib.csproj` in a project file and `../Lib/Lib.csproj` in
            // restore output.
            var buildOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var references = new List<string>();

            foreach (XElement element in doc.Descendants(ns + "ProjectReference"))
            {
                string? include = element.Attribute("Include")?.Value;
                if (include is null)
                {
                    continue;
                }
                // An analyzer or source-generator reference builds a .dll into the referenced
                // project's bin like any other, but the referencing project cannot call into
                // it — indexing it answers "yes, that API exists" for code that will not
                // compile. MSBuild marks these with OutputItemType="Analyzer" or
                // ReferenceOutputAssembly="false".
                bool isBuildOnly = ProjectReferenceMetadata.IsBuildOnly(element);
                // One Include may name several projects, semicolon-separated. Treating the
                // whole value as a single path finds no file, so every project it names goes
                // unindexed and their types all answer "does not exist".
                foreach (string part in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!TryResolveProjectReference(projectDir, part, out string resolved))
                    {
                        continue;
                    }
                    if (isBuildOnly)
                    {
                        buildOnly.Add(resolved);
                    }
                    else if (seen.Add(resolved))
                    {
                        references.Add(resolved);
                    }
                }
            }

            // A project file names only what this project references directly, but C#
            // exposes a transitively referenced project's public types to the compiler too:
            // App -> Middle -> Leaf lets App call Leaf. Indexing only the direct references
            // answers "does not exist" for every type in Leaf. Restore output already holds
            // the full closure, so it costs one file read rather than a graph walk.
            foreach (string relative in ReadTransitiveProjectReferences(projectDir, projectFile))
            {
                if (TryResolveProjectReference(projectDir, relative, out string resolved)
                    && !buildOnly.Contains(resolved)
                    && seen.Add(resolved))
                {
                    references.Add(resolved);
                }
            }

            if (references.Count == 0)
            {
                return packages;
            }

            List<string> referencingFrameworks = ReadTargetFrameworks(projectFile);
            foreach (string fullPath in references)
            {
                string refDir = Path.GetDirectoryName(fullPath)!;
                string refName = Path.GetFileNameWithoutExtension(fullPath);
                string binDir = Path.Combine(refDir, "bin");
                // The referenced project's own directory is the boundary here: its `bin` is
                // as repo-controlled as the `.csproj` guarded above, and a junction there
                // would send the recursive scan below onto a share.
                if (PathSafety.CrossesReparsePoint(binDir, refDir) || !Directory.Exists(binDir))
                {
                    continue;
                }
                var winmds = GetFilesNoReparse(binDir, "*.winmd")
                    .Where(f => !Path.GetFileName(f).Equals("Windows.winmd", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // A referenced C# class library builds to a .dll, not a .winmd, so a
                // winmd-only scan indexes nothing for it and every query about its types
                // answers "does not exist" — for code in the caller's own solution.
                // Only the referenced project's own output is taken: bin also holds every
                // dependency copied next to it, and indexing those would answer from
                // assemblies the project does not reference directly. That output is named
                // by <AssemblyName> when the project sets one, and only defaults to the
                // file name otherwise.
                string outputName = ReadAssemblyName(fullPath) ?? refName;
                winmds.AddRange(GetFilesNoReparse(binDir, outputName + ".dll"));

                // Newest wins per file name, but only among outputs built for the same target
                // framework the referencing project compiles against. `bin` accumulates every
                // configuration and every TFM ever built, and a multi-targeted library builds
                // them all in one command — so timestamps alone pick an arbitrary TFM, and a
                // `net8.0` build can answer a question asked by a `net8.0-windows` app about
                // an API that only exists under Windows.
                winmds = winmds
                    .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group
                        .OrderByDescending(file => ScoreTargetFramework(file, referencingFrameworks))
                        .ThenByDescending(LastWriteOrMin)
                        .First())
                    .ToList();
                if (winmds.Count > 0)
                {
                    // The referenced project's full path is hashed into the package id
                    // (as project manifests do) because the cache is keyed by that id:
                    // two projects that each reference a different Lib.csproj would
                    // otherwise share "ProjectRef.Lib/local", overwrite one another's
                    // export, and answer from the wrong library.
                    string packageId = "ProjectRef." + refName + "_" + ApiCachePaths.ShortHash(fullPath);
                    packages.Add(new PackageWithWinMd(packageId, "local", winmds, new List<string>()));
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            // An unreadable or malformed project file yields no project references
            // rather than failing the whole resolve.
        }
        return packages;
    }

    /// <summary>
    /// The assembly name a referenced project builds to, or <see langword="null"/> to use
    /// the project file name. Values containing an unexpanded MSBuild property are treated
    /// as absent: evaluating them needs the whole MSBuild engine, and guessing produces a
    /// file name that matches nothing.
    /// </summary>
    private static string? ReadAssemblyName(string projectFile)
    {
        try
        {
            // Last one wins, as in MSBuild evaluation. LocalName ignores the namespace so
            // SDK-style and legacy projects read the same way.
            string? value = XDocument.Load(projectFile)
                .Descendants()
                .Where(e => e.Name.LocalName == "AssemblyName")
                .Select(e => e.Value.Trim())
                .LastOrDefault(v => v.Length > 0 && !v.Contains("$("));
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Last write time, or <see cref="DateTime.MinValue"/> for a file that cannot be read —
    /// which just loses the newest-wins comparison rather than failing the resolve.
    /// </summary>
    private static DateTime LastWriteOrMin(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// The file whose modification time says when a project was last restored, and
    /// therefore when its index went stale: <c>project.assets.json</c> for an MSBuild
    /// project, or <c>.winapp/winmds.lock.json</c> for a project driven by
    /// <c>winapp.yaml</c> alone. Null when the project has never been restored, which
    /// is what tells callers there is nothing to index yet.
    /// </summary>
    /// <summary>
    /// Resolves a project reference path against the referencing project's directory,
    /// rejecting anything that leaves repo-controlled disk.
    /// </summary>
    /// <remarks>
    /// A rooted path discards the project directory and could name any location, including
    /// a share; a relative one may still be redirected onto a share by a checked-in
    /// symlink. Both are settled before <see cref="File.Exists(string)"/>, which would
    /// authenticate to whatever host answers.
    /// </remarks>
    private static bool TryResolveProjectReference(string projectDir, string reference, out string fullPath)
    {
        fullPath = string.Empty;
        if (Path.IsPathRooted(reference))
        {
            return false;
        }
        string candidate = Path.GetFullPath(Path.Combine(projectDir, reference.Replace('/', Path.DirectorySeparatorChar)));
        if (PathSafety.CrossesReparsePoint(candidate, projectDir) || !File.Exists(candidate))
        {
            return false;
        }
        fullPath = candidate;
        return true;
    }

    /// <summary>
    /// Every project in the referencing project's restore graph — direct and transitive —
    /// as paths relative to its directory, from the <c>"type": "project"</c> entries in
    /// <c>project.assets.json</c>. Empty when restore has not run, leaving the project
    /// file's direct references as the only source.
    /// </summary>
    private static List<string> ReadTransitiveProjectReferences(string projectDir, string projectFile)
    {
        var results = new List<string>();
        if (FindProjectAssetsJson(projectDir, projectFile) is not string assetsPath)
        {
            return results;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
            if (!doc.RootElement.TryGetProperty("libraries", out var librariesEl)
                || librariesEl.ValueKind != JsonValueKind.Object)
            {
                return results;
            }
            foreach (JsonProperty library in librariesEl.EnumerateObject())
            {
                if (library.Value.ValueKind != JsonValueKind.Object
                    || !library.Value.TryGetProperty("type", out var typeEl)
                    || !string.Equals(typeEl.GetString(), "project", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                // msbuildProject names the project file; path can name a directory for
                // some project styles, so it is only the fallback.
                string? relative = library.Value.TryGetProperty("msbuildProject", out var msbuildEl)
                    ? msbuildEl.GetString()
                    : library.Value.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;
                if (!string.IsNullOrEmpty(relative))
                {
                    results.Add(relative);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException)
        {
            // Unreadable restore output falls back to the project file's direct references.
        }
        return results;
    }

    internal static string? FindRestoreOutput(string projectDir)
    {
        string? assetsPath = FindProjectAssetsJson(projectDir);
        if (assetsPath is not null)
        {
            return assetsPath;
        }
        string lockfilePath = Path.Combine(projectDir, ".winapp", WinmdsLockfileService.LockfileName);
        // Same reason the lockfile is guarded where it is read: `.winapp` is a directory
        // the repository supplies, so a junction there redirects this probe onto whatever
        // host it names, and File.Exists authenticates before returning.
        if (PathSafety.CrossesReparsePoint(lockfilePath, projectDir))
        {
            return null;
        }
        return File.Exists(lockfilePath) ? lockfilePath : null;
    }

    internal static string? FindProjectAssetsJson(string projectDir, string? projectFile = null)
    {
        string objDir = Path.Combine(projectDir, "obj");
        // `obj` is regenerated by every build, so it reads like machine state — but it is a
        // path inside a cloned working tree, and a junction committed in its place sends
        // both the probe below and the recursive scan to wherever it points. Checked before
        // any probe, because File.Exists on a redirected path has already reached the host.
        if (PathSafety.CrossesReparsePoint(objDir, projectDir))
        {
            return null;
        }
        string direct = Path.Combine(objDir, "project.assets.json");
        if (File.Exists(direct))
        {
            return direct;
        }
        if (!Directory.Exists(objDir))
        {
            return null;
        }
        string[] files = GetFilesNoReparse(objDir, "project.assets.json");
        if (files.Length == 0)
        {
            return null;
        }

        // Every assets file records the project it was restored for. When several turn up
        // under one obj tree — colocated projects, or a nested BaseIntermediateOutputPath —
        // the one that names this project is the right answer, and picking by write time
        // instead makes the whole index depend on which project was built last.
        if (projectFile is not null && files.Length > 1)
        {
            string wanted = Path.GetFullPath(projectFile);
            string[] owned = files
                .Where(file => string.Equals(ReadRestoreProjectPath(file), wanted, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (owned.Length > 0)
            {
                files = owned;
            }
        }

        string? newest = null;
        DateTime newestTime = DateTime.MinValue;
        foreach (string file in files)
        {
            try
            {
                DateTime writeTime = File.GetLastWriteTimeUtc(file);
                if (writeTime > newestTime)
                {
                    newestTime = writeTime;
                    newest = file;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished mid-scan just loses the "newest" contest.
            }
        }
        return newest;
    }

    /// <summary>
    /// The full path of the project an assets file was restored for, from
    /// <c>project.restore.projectPath</c>. Null when it cannot be read, which just means
    /// the file cannot be attributed and is judged by write time like any other.
    /// </summary>
    private static string? ReadRestoreProjectPath(string assetsPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
            if (doc.RootElement.TryGetProperty("project", out var projectEl)
                && projectEl.TryGetProperty("restore", out var restoreEl)
                && restoreEl.TryGetProperty("projectPath", out var pathEl)
                && pathEl.GetString() is { Length: > 0 } path)
            {
                return Path.GetFullPath(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or NotSupportedException)
        {
            // Unreadable or malformed: fall back to the write-time contest.
        }
        return null;
    }

    /// <summary>
    /// Picks the restore target whose compile assets a Windows build actually uses.
    /// A multi-targeted project lists several targets in <c>project.assets.json</c>, and
    /// the first one is whichever the project file happened to name first — taking it
    /// blindly reads <c>net8.0</c> assets for a <c>net8.0-windows10.0.19041.0</c> build and
    /// reports Windows-only types as missing. Prefers the highest Windows platform version,
    /// then falls back to the first target so non-Windows projects behave as before.
    /// </summary>
    /// <remarks>
    /// When a project declares more than one Windows target the answer is only true of the
    /// one chosen, so the caller is told which that was. It is a warning rather than a
    /// failure because multi-targeting is a legitimate configuration, and refusing to
    /// index it would leave those projects with no answers at all.
    /// </remarks>
    private static JsonElement? SelectWindowsTarget(JsonElement targetsEl, Action<string>? warn = null)
    {
        JsonElement? first = null;
        JsonElement? best = null;
        Version? bestVersion = null;
        string? bestName = null;
        var windowsTargets = new List<string>();

        foreach (JsonProperty target in targetsEl.EnumerateObject())
        {
            first ??= target.Value;
            Match match = WindowsTargetMoniker.Match(target.Name);
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out Version? platform))
            {
                continue;
            }
            windowsTargets.Add(target.Name);
            if (bestVersion == null || platform > bestVersion)
            {
                bestVersion = platform;
                bestName = target.Name;
                best = target.Value;
            }
        }

        if (windowsTargets.Count > 1)
        {
            warn?.Invoke(
                $"This project targets {windowsTargets.Count} Windows frameworks ({string.Join(", ", windowsTargets)}). "
                + $"Answers come from '{bestName}'; an API reported as available may not exist for the others.");
        }

        return best ?? first;
    }

    internal static List<PackageWithWinMd> FindPackagesFromAssets(string assetsPath, Action<string>? warn = null, string? projectDir = null)
    {
        var packages = new List<PackageWithWinMd>();
        // The tree the repository controls. `project.assets.json` normally sits in
        // `<projectDir>\obj`, so its grandparent is the project when the caller did not
        // say; a custom BaseIntermediateOutputPath only makes this deeper, which narrows
        // what counts as repo-controlled rather than trusting more of the disk.
        string probeRoot = projectDir ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(assetsPath) ?? ".", ".."));
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
            JsonElement root = doc.RootElement;

            var packageFolders = new List<string>();
            if (root.TryGetProperty("packageFolders", out var packageFoldersEl))
            {
                packageFolders.AddRange(packageFoldersEl.EnumerateObject()
                    .Where(folder => IsProbeablePath(folder.Name, probeRoot))
                    .Select(folder => folder.Name));
            }

            HashSet<string> directDependencies = ReadDirectDependencies(root);

            if (!root.TryGetProperty("libraries", out var librariesEl))
            {
                return packages;
            }

            // Map "id/version" -> compile-time .dll/.winmd relative paths from the target
            // the project actually builds for Windows.
            var compileByLibrary = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            // Every library that target builds, including ones with no compile group.
            // Null when restore named no Windows target at all, in which case nothing can
            // be judged out-of-target and every library stays eligible for the scan below.
            HashSet<string>? librariesInSelectedTarget = null;
            if (root.TryGetProperty("targets", out var targetsEl) && targetsEl.ValueKind == JsonValueKind.Object
                && SelectWindowsTarget(targetsEl, warn) is JsonElement selectedTarget)
            {
                librariesInSelectedTarget = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty library in selectedTarget.EnumerateObject())
                {
                    librariesInSelectedTarget.Add(library.Name);
                    if (!library.Value.TryGetProperty("compile", out var compileEl))
                    {
                        continue;
                    }
                    // A compile group that exists is authoritative even when every entry is
                    // NuGet's "_._" placeholder: that means the package deliberately exposes
                    // no compile-time assets for this target. Recording the empty list — rather
                    // than leaving the library out of the map — keeps the scan fallback below
                    // from indexing some other target's .winmd and confirming an API the
                    // project cannot compile against.
                    compileByLibrary[library.Name] = compileEl.EnumerateObject()
                        .Select(entry => entry.Name)
                        .Where(entryName => (entryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                || entryName.EndsWith(".winmd", StringComparison.OrdinalIgnoreCase))
                            && !entryName.EndsWith("/_._", StringComparison.Ordinal))
                        .ToList();
                }
            }

            foreach (JsonProperty library in librariesEl.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("type", out var typeEl) || !string.Equals(typeEl.GetString(), "package", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                // "libraries" lists every package restore resolved across all target
                // frameworks. One the selected Windows target does not build is not on this
                // project's compile surface: without this it reaches the scan fallback
                // below and contributes .winmd for a TFM the project never compiles
                // against, so a query confirms an API that is not actually available.
                if (librariesInSelectedTarget is not null && !librariesInSelectedTarget.Contains(library.Name))
                {
                    continue;
                }
                int slash = library.Name.IndexOf('/');
                if (slash < 0)
                {
                    continue;
                }
                string id = library.Name.Substring(0, slash);
                string version = library.Name.Substring(slash + 1);
                // A package the project asked for by name is on its compile surface no matter
                // what the id looks like. Without this, a PackageReference to System.Text.Json
                // or Microsoft.CodeAnalysis.CSharp is filtered out as if it were part of the
                // framework, and every type in it answers "not found" — the one answer that
                // stops an agent from writing code that would have compiled.
                if ((IsFrameworkPackage(id) && !directDependencies.Contains(id))
                    || !library.Value.TryGetProperty("path", out var pathEl))
                {
                    continue;
                }
                string? relativePath = pathEl.GetString();
                if (relativePath == null)
                {
                    continue;
                }

                var files = new List<string>();
                bool hasSelectedAssets = compileByLibrary.TryGetValue(library.Name, out var selectedAssets);
                var packageDirs = packageFolders
                    .Select(packageFolder => TryResolveUnderRoot(packageFolder, relativePath, out string dir) ? dir : null)
                    // Containment settles the *name*; this settles the *route*. The package
                    // folder was checked, but the id/version directory beneath it is named by
                    // the same repo-controlled file, and a junction there is followed by every
                    // probe below — including the scan, whose reparse pruning cannot help when
                    // the junction is the directory it starts from.
                    .Where(dir => dir != null && IsProbeablePath(dir, probeRoot) && Directory.Exists(dir))
                    .Select(dir => dir!)
                    .ToList();
                if (hasSelectedAssets)
                {
                    foreach (string packageDir in packageDirs)
                    {
                        files.AddRange(selectedAssets!
                            .Select(asset => TryResolveUnderRoot(packageDir, asset.Replace('/', Path.DirectorySeparatorChar), out string assetPath) ? assetPath : null)
                            .Where(assetPath => assetPath != null && IsProbeablePath(assetPath, probeRoot) && File.Exists(assetPath))
                            .Select(assetPath => assetPath!));
                    }
                }

                // Fall back to scanning the package only when restore named no compile
                // assets for it. Scanning unconditionally pulls in .winmd files for
                // TFMs and RIDs NuGet did not select, so the project gets a confident
                // positive for an API it cannot actually compile against.
                if (!hasSelectedAssets)
                {
                    foreach (string packageDir in packageDirs)
                    {
                        files.AddRange(GetFilesNoReparse(packageDir, "*.winmd"));
                    }
                }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                files = files.Where(f => seen.Add(Path.GetFileName(f))).ToList();
                if (files.Count == 0)
                {
                    continue;
                }

                var xmlDocs = packageDirs.SelectMany(FindXmlDocsInPackageFolder).ToList();
                packages.Add(new PackageWithWinMd(id, version, files, xmlDocs));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // The caller only gets here when project.assets.json exists, so a failure
            // means restore output is present but unreadable. Staying silent indexes
            // whatever was collected before the throw and answers "does not exist" for
            // every package after it — a false negative with nothing to attribute it to.
            warn?.Invoke(
                $"Could not fully read '{assetsPath}' ({ex.Message}). Indexed {packages.Count} package(s) from it; " +
                "APIs from the rest will report as not found. Re-run 'dotnet restore' and then 'winapp find-api refresh'.");
        }
        return packages;
    }

    /// <summary>
    /// The package ids the project itself names, from <c>project.frameworks.*.dependencies</c>.
    /// The union across every target framework, because a package named under any of them was
    /// asked for deliberately, and the Windows-target filter above already drops libraries the
    /// selected target does not build.
    /// </summary>
    private static HashSet<string> ReadDirectDependencies(JsonElement root)
    {
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("project", out var projectEl)
            || !projectEl.TryGetProperty("frameworks", out var frameworksEl)
            || frameworksEl.ValueKind != JsonValueKind.Object)
        {
            return direct;
        }
        foreach (JsonProperty framework in frameworksEl.EnumerateObject())
        {
            if (framework.Value.ValueKind != JsonValueKind.Object
                || !framework.Value.TryGetProperty("dependencies", out var dependenciesEl)
                || dependenciesEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            foreach (JsonProperty dependency in dependenciesEl.EnumerateObject())
            {
                direct.Add(dependency.Name);
            }
        }
        return direct;
    }

    internal static bool IsFrameworkPackage(string packageId)
    {
        if (packageId.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase) || packageId.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string[] prefixes = { "System.", "Microsoft.NETCore.", "Microsoft.NET.", "runtime.", "Microsoft.Build.", "Microsoft.CodeAnalysis.", "Microsoft.DiaSymReader." };
        return prefixes.Any(prefix => packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The metadata packages recorded in <c>.winapp/winmds.lock.json</c>, which
    /// <c>winapp restore</c> writes for projects that have no MSBuild project file
    /// (Electron and other non-.NET apps driven by <c>winapp.yaml</c>). The lockfile
    /// already stores exactly what the indexer needs: a package id, its resolved
    /// version, and the absolute paths of its <c>.winmd</c> files.
    /// </summary>
    /// <remarks>
    /// Unlike <c>project.assets.json</c>, the lockfile names no target framework, so
    /// there is nothing to judge a package out-of-target against — every entry it
    /// lists is on the compile surface by construction. A stale lockfile pointing at
    /// files that no longer exist contributes nothing rather than failing the resolve.
    /// </remarks>
    internal static List<PackageWithWinMd> FindPackagesFromWinmdsLockfile(string projectDir)
    {
        var packages = new List<PackageWithWinMd>();
        string winappDir = Path.Combine(projectDir, ".winapp");
        string lockfilePath = Path.Combine(winappDir, WinmdsLockfileService.LockfileName);
        // Reparse check first: `||` short-circuits, so probing before the guard would
        // follow a `.winapp` symlink onto a share and defeat it. The project directory
        // itself is the caller's choice and may legitimately be a network location.
        if (PathSafety.CrossesReparsePoint(lockfilePath, projectDir) || !File.Exists(lockfilePath))
        {
            return packages;
        }

        try
        {
            WinmdsLockfile? lockfile = JsonSerializer.Deserialize(
                File.ReadAllText(lockfilePath),
                WinmdsLockfileJsonContext.Default.WinmdsLockfile);

            // A lockfile from a different schema describes a shape this code has not
            // agreed to read. Ignoring it leaves the project unindexed, which reports
            // honestly, rather than half-reading it and answering from a partial surface.
            if (lockfile is null || lockfile.Schema != WinmdsLockfile.CurrentSchema)
            {
                return packages;
            }

            foreach (WinmdsLockfilePackage package in lockfile.Packages)
            {
                if (string.IsNullOrEmpty(package.Name) || string.IsNullOrEmpty(package.Version)
                    || IsFrameworkPackage(package.Name))
                {
                    continue;
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var files = package.Winmds
                    .Where(winmd => IsProbeablePath(winmd, projectDir) && File.Exists(winmd))
                    .Where(winmd => seen.Add(Path.GetFileName(winmd)))
                    .ToList();
                if (files.Count == 0)
                {
                    continue;
                }

                var xmlDocs = files
                    .Select(winmd => FindPackageRootForWinMd(winmd, package.Version))
                    .Where(root => root is not null)
                    .Select(root => root!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .SelectMany(FindXmlDocsInPackageFolder)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                packages.Add(new PackageWithWinMd(package.Name, package.Version, files, xmlDocs));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable or malformed lockfile yields no packages rather than
            // failing the whole resolve, matching the packages.config path above.
        }

        return packages;
    }

    /// <summary>
    /// The NuGet package root directory containing a <c>.winmd</c>, found by walking up
    /// until a directory named for the package version — the <c>{id}/{version}/…</c>
    /// cache layout. Null when the file does not sit under that layout, in which case
    /// there is no package folder to look for XML docs in.
    /// </summary>
    private static string? FindPackageRootForWinMd(string winmdPath, string version)
    {
        DirectoryInfo? dir = Directory.GetParent(winmdPath);
        for (int depth = 0; dir is not null && depth < 8; depth++)
        {
            if (dir.Name.Equals(version, StringComparison.OrdinalIgnoreCase))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    internal static List<PackageWithWinMd> FindPackagesFromConfig(string configPath, string projectDir)
    {
        var packages = new List<PackageWithWinMd>();
        try
        {
            IEnumerable<XElement>? entries = XDocument.Load(configPath).Root?.Elements("package");
            if (entries == null)
            {
                return packages;
            }
            string? solutionPackages = FindSolutionPackagesFolder(projectDir);
            // The tree that owns the `packages` folder, so the check below walks the
            // id.version directory beneath it rather than the folder itself: a global
            // NuGet cache a developer junctioned onto another volume must keep working.
            string? packagesRoot = solutionPackages is null ? null : Path.GetDirectoryName(solutionPackages);
            string globalPackages = GetNuGetPackagesDir();
            foreach (XElement entry in entries)
            {
                string? id = entry.Attribute("id")?.Value;
                string? version = entry.Attribute("version")?.Value;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version))
                {
                    continue;
                }
                var files = new List<string>();
                if (solutionPackages != null
                    && TryResolveUnderRoot(solutionPackages, id + "." + version, out string solutionDir)
                    && IsProbeablePath(solutionDir, packagesRoot ?? projectDir)
                    && Directory.Exists(solutionDir))
                {
                    files.AddRange(GetFilesNoReparse(solutionDir, "*.winmd"));
                }
                if (files.Count == 0 && Directory.Exists(globalPackages)
                    && TryResolveUnderRoot(globalPackages, Path.Combine(id.ToLowerInvariant(), version), out string globalDir)
                    && IsProbeablePath(globalDir, projectDir)
                    && Directory.Exists(globalDir))
                {
                    files.AddRange(GetFilesNoReparse(globalDir, "*.winmd"));
                }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                files = files.Where(f => seen.Add(Path.GetFileName(f))).ToList();
                if (files.Count > 0)
                {
                    packages.Add(new PackageWithWinMd(id, version, files, new List<string>()));
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            // An unreadable or malformed packages.config yields no packages rather
            // than failing the whole resolve.
        }
        return packages;
    }

    internal static string? FindSolutionPackagesFolder(string startDir)
    {
        string current = startDir;
        for (int i = 0; i < 5; i++)
        {
            string candidate = Path.Combine(current, "packages");
            // Walking up from the project stays inside the clone, so a junction named
            // `packages` at any level would be followed by the scan in the caller. Guard
            // before Directory.Exists: `&&` short-circuits, and probing first has already
            // reached whatever host the junction names.
            if (!PathSafety.CrossesReparsePoint(candidate, current) && Directory.Exists(candidate))
            {
                return candidate;
            }
            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }
            current = parent.FullName;
        }
        return null;
    }

    internal static (List<string> Files, string Version) FindWindowsSdkWinMd(string? preferredVersion = null, Action<string>? warn = null)
    {
        string unionMetadata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "UnionMetadata");
        if (!Directory.Exists(unionMetadata))
        {
            return (new List<string>(), "unknown");
        }

        var versioned = Directory.GetDirectories(unionMetadata)
            .Select(d => (Dir: d, Name: Path.GetFileName(d)))
            .Where(x => !string.IsNullOrEmpty(x.Name) && char.IsDigit(x.Name[0]))
            .Select(x => Version.TryParse(x.Name, out var v) ? (x.Dir, Version: v) : (x.Dir, Version: null))
            .Where(x => x.Version != null)
            .OrderByDescending(x => x.Version)
            .ToList();

        string? selected = SelectWindowsSdkDir(
            versioned,
            preferredVersion,
            dir => File.Exists(Path.Combine(dir, "Windows.winmd")),
            warn);
        if (selected is null)
        {
            return (new List<string>(), "unknown");
        }
        return (new List<string> { Path.Combine(selected, "Windows.winmd") }, Path.GetFileName(selected));
    }

    /// <summary>
    /// Chooses which installed Windows SDK answers a query, given the installs found on
    /// disk (ordered newest first) and the platform version the project targets. Split
    /// from <see cref="FindWindowsSdkWinMd"/> so the selection rule can be tested without
    /// a real Windows Kits install; <paramref name="hasWindowsWinmd"/> stands in for the
    /// on-disk check. Returns null when no candidate carries Windows.winmd.
    /// </summary>
    internal static string? SelectWindowsSdkDir(
        IReadOnlyList<(string Dir, Version? Version)> versionedDescending,
        string? preferredVersion,
        Func<string, bool> hasWindowsWinmd,
        Action<string>? warn = null)
    {
        // Prefer the SDK the project actually targets. Indexing whatever is newest on
        // the machine reports types the project cannot compile against: a project on
        // 10.0.26100.0 was answered from an installed 10.0.28000.0 and confidently
        // returned an API that fails to build with CS0234. Matching is tried on the
        // full version first, then on the build number, because a target moniker and
        // the UnionMetadata folder can disagree in the trailing revision.
        if (!string.IsNullOrEmpty(preferredVersion) && Version.TryParse(preferredVersion, out Version? wanted))
        {
            string? exact = versionedDescending
                .Where(x => x.Version!.Equals(wanted))
                .Concat(versionedDescending.Where(x => x.Version!.Build == wanted.Build && !x.Version.Equals(wanted)))
                .Select(x => x.Dir)
                .FirstOrDefault(hasWindowsWinmd);
            if (exact is not null)
            {
                return exact;
            }

            // Never answer a project from an SDK newer than the one it targets.
            // UnionMetadata is cumulative, so an older SDK can only omit APIs the project
            // might use — a false "not found" the user can sanity-check — while a newer
            // one adds APIs that do not exist at the target, which find-api then confirms
            // and the build rejects with CS0234. Take the highest install at or below the
            // target; only when there is none fall back to the closest one above, which
            // keeps the overshoot as small as possible. Say so in both cases.
            string? capped = versionedDescending
                .Where(x => x.Version! <= wanted)
                .Select(x => x.Dir)
                .FirstOrDefault(hasWindowsWinmd);
            if (capped is not null)
            {
                warn?.Invoke($"Windows SDK {preferredVersion} is not installed; answering from {Path.GetFileName(capped)}. APIs introduced after that version will report as not found.");
                return capped;
            }

            string? closestNewer = versionedDescending
                .Where(x => x.Version! > wanted)
                .Reverse()
                .Select(x => x.Dir)
                .FirstOrDefault(hasWindowsWinmd);
            if (closestNewer is not null)
            {
                warn?.Invoke($"No Windows SDK at or below the targeted {preferredVersion} is installed; answering from {Path.GetFileName(closestNewer)}. Results may include APIs that do not exist at your target.");
            }
            return closestNewer;
        }

        return versionedDescending.Select(x => x.Dir).FirstOrDefault(hasWindowsWinmd);
    }

    internal static (List<string> Files, string Version) FindWinAppSdkRuntimeWinMd(string? runtimePath)
    {
        if (string.IsNullOrEmpty(runtimePath) || !Directory.Exists(runtimePath))
        {
            return (new List<string>(), "unknown");
        }
        try
        {
            var files = Directory.EnumerateFiles(runtimePath, "*.winmd", SearchOption.TopDirectoryOnly).ToList();
            if (files.Count > 0)
            {
                return (files, RuntimeReleaseLabel(Path.GetFileName(runtimePath)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable runtime folder reports no winmds and an unknown version.
        }
        return (new List<string>(), "unknown");
    }

    /// <summary>
    /// A recognizable release label for a Windows App Runtime package folder such as
    /// <c>Microsoft.WindowsAppRuntime.2_2.4.0.0_arm64__8wekyb3d8bbwe</c>.
    /// </summary>
    /// <remarks>
    /// The two release lines encode the release differently. The 1.x packages carry it
    /// in the name (<c>...Runtime.1.8</c>) and use an unrelated package version
    /// (<c>8000.946.1701.0</c>), so the name is the useful label. The 2.x packages carry
    /// only the major in the name (<c>...Runtime.2</c>) and the real release in the
    /// version (<c>2.4.0.0</c>), so the two are combined into "2.4" rather than
    /// reporting a bare "2".
    /// </remarks>
    internal static string RuntimeReleaseLabel(string folderName)
    {
        const string prefix = "Microsoft.WindowsAppRuntime.";
        string[] parts = folderName.Split('_');
        string head = parts[0];
        if (head.Length <= prefix.Length || !head.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return folderName;
        }

        string suffix = head.Substring(prefix.Length);
        string core = suffix.Split('-')[0];
        if (core.Contains('.', StringComparison.Ordinal))
        {
            return suffix;
        }
        if (parts.Length > 1 && Version.TryParse(parts[1], out Version? packageVersion))
        {
            return core + "." + packageVersion.Minor + (suffix.Length > core.Length ? suffix.Substring(core.Length) : string.Empty);
        }
        return suffix;
    }

    /// <summary>
    /// The target framework monikers a project declares, from <c>&lt;TargetFramework&gt;</c>
    /// or <c>&lt;TargetFrameworks&gt;</c>. Empty for a project that declares none — a C++
    /// project, or one whose monikers come from a props file — which simply means no
    /// output can be preferred over another on framework grounds.
    /// </summary>
    private static List<string> ReadTargetFrameworks(string projectFile)
    {
        try
        {
            XDocument doc = XDocument.Load(projectFile);
            return doc.Descendants()
                .Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// How well a built output's target framework folder suits a project that compiles
    /// against <paramref name="frameworks"/>. Higher is better; <c>-1</c> means the output
    /// targets a platform the referencing project does not, so it cannot be consumed at all.
    /// </summary>
    /// <remarks>
    /// A deliberately small subset of NuGet's compatibility rules, covering what actually
    /// separates the outputs sitting side by side in one <c>bin</c> tree: an exact moniker,
    /// a platform-neutral build (usable by anything on the same base framework), the same
    /// platform at a different platform version, and a foreign platform. Anything this
    /// cannot identify scores 0 and is left to the timestamp, which is the behavior a
    /// single-target project has always had.
    /// </remarks>
    private static int ScoreTargetFramework(string filePath, List<string> frameworks)
    {
        if (frameworks.Count == 0 || FindTargetFrameworkFolder(filePath) is not string candidate)
        {
            return 0;
        }
        if (frameworks.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            return 3;
        }

        (string candidateBase, string candidatePlatform) = SplitMoniker(candidate);
        bool baseMatches = false;
        foreach (string framework in frameworks)
        {
            (string frameworkBase, string frameworkPlatform) = SplitMoniker(framework);
            if (!string.Equals(candidateBase, frameworkBase, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            baseMatches = true;
            if (candidatePlatform.Length == 0)
            {
                return 2;
            }
            if (string.Equals(candidatePlatform, frameworkPlatform, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
        }
        // A platform-specific output the referencing project cannot compile against: a
        // net8.0 app referencing a library's net8.0-windows build would be told that
        // every Windows-only type on it exists.
        return candidatePlatform.Length > 0 && baseMatches ? -1 : 0;
    }

    /// <summary>
    /// The innermost directory of a path that names a target framework, or null when none
    /// does. MSBuild writes each target framework to its own
    /// <c>bin\&lt;configuration&gt;\&lt;tfm&gt;</c> folder.
    /// </summary>
    private static string? FindTargetFrameworkFolder(string filePath)
    {
        for (DirectoryInfo? dir = Directory.GetParent(filePath); dir is not null; dir = dir.Parent)
        {
            if (TargetFrameworkFolder.IsMatch(dir.Name))
            {
                return dir.Name;
            }
        }
        return null;
    }

    /// <summary>
    /// Splits a target framework moniker into its base framework and platform name,
    /// discarding the platform version: <c>net8.0-windows10.0.19041.0</c> becomes
    /// <c>("net8.0", "windows")</c>, <c>net8.0</c> becomes <c>("net8.0", "")</c>.
    /// </summary>
    private static (string Base, string Platform) SplitMoniker(string moniker)
    {
        int dash = moniker.IndexOf('-');
        if (dash < 0)
        {
            return (moniker, string.Empty);
        }
        string platform = moniker[(dash + 1)..];
        int digit = platform.AsSpan().IndexOfAnyInRange('0', '9');
        return (moniker[..dash], digit < 0 ? platform : platform[..digit]);
    }

    private static readonly Regex TargetFrameworkFolder = TargetFrameworkFolderRegex();

    [GeneratedRegex(@"^net(standard|coreapp)?\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetFrameworkFolderRegex();

    /// <summary>
    /// Recursively enumerates files without descending through a reparse point (a junction
    /// or symbolic link).
    /// </summary>
    /// <remarks>
    /// Callers already refuse to start a scan at a directory reached by crossing a reparse
    /// point, but that only guards the entry. <c>SearchOption.AllDirectories</c> follows a
    /// junction found part-way down, so a repository that commits one inside <c>bin</c> or
    /// <c>obj</c> still redirects the walk onto whatever it points at — a network share
    /// turns a local, read-only query into an outbound SMB authentication attempt against a
    /// host the repository chose. The options below otherwise match
    /// <c>SearchOption.AllDirectories</c> exactly, so hidden and system files are still
    /// enumerated.
    /// </remarks>
    private static string[] GetFilesNoReparse(string directory, string pattern)
    {
        return Directory.GetFiles(directory, pattern, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MatchType = MatchType.Win32,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        });
    }

    /// <summary>
    /// Whether a path named by project metadata may be touched. <c>project.assets.json</c>,
    /// <c>packages.config</c>, and project files all live in the repository, so cloning a
    /// repository is enough to choose these values. A UNC path among them turns a local,
    /// read-only query into an outbound SMB authentication attempt against a host the
    /// repository picked, so network paths are skipped rather than probed.
    /// </summary>
    /// <remarks>
    /// A value that lands inside <paramref name="root"/> is repo-controlled twice over: the
    /// repository names it *and* supplies the directories it passes through, so a junction
    /// committed along the way redirects the probe onto a share without the value ever
    /// looking network-shaped. Such a value must be reachable without crossing a reparse
    /// point. A value outside <paramref name="root"/> — most often a NuGet cache on another
    /// volume — was configured by the user rather than named by the repository, and only the
    /// network check applies; requiring containment there would reject every normal machine.
    /// </remarks>
    private static bool IsProbeablePath(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || PathSafety.IsNetworkPath(path))
        {
            return false;
        }
        return !PathSafety.IsUnder(path, root) || !PathSafety.CrossesReparsePoint(path, root);
    }

    /// <summary>
    /// Resolves a relative path named by project metadata against <paramref name="root"/>,
    /// rejecting rooted values and anything that climbs out of the root. A rooted asset
    /// name silently wins over the root it is combined with, which is how a package-relative
    /// value reaches an arbitrary location on disk. The root itself is the caller's
    /// responsibility: a NuGet cache configured outside the repository may legitimately
    /// be a network share.
    /// </summary>
    private static bool TryResolveUnderRoot(string root, string relative, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            return false;
        }
        try
        {
            return ApiCachePaths.TryCombineContained(root, new[] { relative }, out resolved);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            resolved = string.Empty;
            return false;
        }
    }

    private static string GetNuGetPackagesDir()
    {
        string? env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }
}
