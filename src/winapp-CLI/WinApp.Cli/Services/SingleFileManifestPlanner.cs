// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// A manifest the app author supplied, rather than one winapp infers.
/// </summary>
/// <param name="File">The manifest file.</param>
/// <param name="Source">Human-readable phrase naming which tier found it, for logs and errors.</param>
internal sealed record AuthoredSingleFileManifest(FileInfo File, string Source);

/// <summary>
/// The manifest metadata inferred for a .NET file-based app, ready to hand to
/// <see cref="IManifestTemplateService.GenerateCompleteManifestAsync"/>.
/// </summary>
/// <param name="PackageName">Sanitized <c>Identity/@Name</c>.</param>
/// <param name="DisplayName">Human-readable name for <c>Properties/DisplayName</c> and <c>uap:VisualElements/@DisplayName</c>.</param>
/// <param name="PublisherDN">Normalized X.500 <c>Identity/@Publisher</c>.</param>
/// <param name="Version">Four-part, in-range <c>Identity/@Version</c>.</param>
/// <param name="Description">Text for <c>uap:VisualElements/@Description</c>.</param>
/// <param name="Capabilities">Capabilities to declare, already resolved to their element and XML namespace.</param>
internal sealed record SingleFileManifestInfo(
    string PackageName,
    string DisplayName,
    string PublisherDN,
    string Version,
    string Description,
    IReadOnlyList<AppxCapability> Capabilities);

/// <summary>
/// Maps a .NET file-based app's evaluated MSBuild properties onto manifest metadata.
/// <para>
/// Mirrors <c>winapp manifest generate</c>'s options and adds one thing it does not have:
/// <c>WinAppCapabilities</c>. Full trust is not the whole story — <c>runFullTrust</c> lets the app run
/// unsandboxed, but some APIs are gated on a declared capability regardless, and the Windows AI APIs are
/// the case that forced this: Phi Silica requires <c>systemAIModels</c>, which no amount of full trust
/// substitutes for. Shell integrations (protocol handlers, file associations) are a third case again:
/// those need authored <c>Extensions</c> entries and are served by the <c>WinAppManifestPath</c> escape
/// hatch, not by a capability.
/// </para>
/// <para>
/// <c>runFullTrust</c> itself stays fixed template boilerplate rather than user input, so the common
/// app declares nothing. Capability names are resolved through <see cref="AppxCapabilityCatalog"/>,
/// because they span several different elements and namespaces and a flat name list emitted into one
/// of them produces manifests Windows rejects.
/// </para>
/// Pure and side-effect free.
/// </summary>
internal static partial class SingleFileManifestPlanner
{
    /// <summary>MSBuild property naming the package identity; falls back to the file stem plus a hash of its path.</summary>
    internal const string PackageNameProperty = "WinAppPackageName";

    /// <summary>MSBuild property naming the app's display name; falls back to the file stem.</summary>
    internal const string DisplayNameProperty = "WinAppDisplayName";

    /// <summary>MSBuild property naming the publisher; falls back to <c>CN=&lt;current user&gt;</c>.</summary>
    internal const string PublisherProperty = "WinAppPublisher";

    /// <summary>MSBuild property naming the package version; falls back to <c>$(Version)</c>.</summary>
    internal const string VersionProperty = "WinAppVersion";

    /// <summary>MSBuild property naming the app description; falls back to the display name.</summary>
    internal const string DescriptionProperty = "WinAppDescription";

    /// <summary>
    /// MSBuild property listing capabilities to declare, separated by <c>;</c> or <c>,</c>. Each entry is
    /// either a name whose namespace is documented (<c>systemAIModels</c>, <c>microphone</c>) or an
    /// explicitly qualified <c>prefix:name</c> (<c>rescap:broadFileSystemAccess</c>).
    /// </summary>
    internal const string CapabilitiesProperty = "WinAppCapabilities";

    /// <summary>
    /// The <c>Application/@Id</c> written for a file-based app. Fixed rather than derived from the package
    /// name: <c>Application/@Id</c> has stricter character rules than <c>Identity/@Name</c>, so deriving it
    /// from a dotted reverse-DNS package name (<c>com.contoso.counter</c>) invites sanitization bugs for no
    /// user-visible benefit. The resulting AUMID is the conventional <c>&lt;pkgfamily&gt;!App</c>.
    /// <c>winapp manifest generate</c> keeps its own package-name-derived Id; this divergence is scoped to
    /// single-file mode.
    /// </summary>
    internal const string ApplicationId = "App";

    /// <summary>The MSIX version used when neither <c>WinAppVersion</c> nor <c>$(Version)</c> yields one.</summary>
    internal const string DefaultVersion = "1.0.0.0";

    /// <summary>
    /// Infers the manifest metadata for <paramref name="singleFile"/> from its evaluated properties.
    /// </summary>
    /// <param name="singleFile">The <c>.cs</c> file-based app (its stem supplies the defaults).</param>
    /// <param name="properties">The evaluated MSBuild properties. Undeclared names evaluate to an empty string, so every lookup is a null-or-empty check.</param>
    /// <param name="defaultPublisher">The publisher used when <c>WinAppPublisher</c> is unset; defaults to <c>CN=&lt;current user&gt;</c>.</param>
    /// <exception cref="ProjectRunException">Thrown when a declared version cannot be represented as a valid MSIX <c>Identity/@Version</c>.</exception>
    public static SingleFileManifestInfo Plan(
        FileInfo singleFile,
        IReadOnlyDictionary<string, string> properties,
        string? defaultPublisher = null)
    {
        var stem = Path.GetFileNameWithoutExtension(singleFile.Name);

        var packageName = InferPackageName(singleFile, properties);

        // The display name is free text, so the RAW stem is the better default than the sanitized identity.
        var displayName = Read(properties, DisplayNameProperty) ?? stem;

        // Bare names auto-wrap as CN=<name>, exactly as `manifest generate --publisher-name` does.
        // Normalize throws ArgumentException for a value that is empty once wrapper quotes are stripped
        // (e.g. WinAppPublisher=''), so it is translated here: an unhandled exception would print a stack
        // trace and, under --json, no envelope at all — one malformed optional directive breaking
        // automation. Version validation already reports this way.
        var publisher = Read(properties, PublisherProperty);
        string publisherDN;
        try
        {
            publisherDN = publisher is not null
                ? PublisherDnHelper.Normalize(publisher)
                : defaultPublisher ?? SystemDefaultsHelper.GetDefaultPublisherCN();
        }
        catch (ArgumentException)
        {
            // The inner message is deliberately not appended: under AOT its ParamName resource does not
            // resolve, so it renders as "Arg_ParamName_Name, publisher" — noise that says less than the
            // sentence above it.
            throw new ProjectRunException(
                $"'{singleFile.Name}' declares {PublisherProperty}='{publisher}', which is not a usable publisher. " +
                $"Set '#:property {PublisherProperty}=<name>' to a plain name (wrapped as CN=<name>) or a full X.500 " +
                "distinguished name such as 'CN=Contoso'.");
        }

        var version = ResolveVersion(singleFile, properties);

        // Falling back to the display name (rather than a generic "My Application") keeps the Settings and
        // installer text meaningful for an app whose whole configuration is a handful of directives.
        var description = Read(properties, DescriptionProperty) ?? displayName;

        // Resolved here, at plan time, so a bad name is a command error rather than a manifest Windows
        // refuses at registration.
        if (!AppxCapabilityCatalog.TryParse(Read(properties, CapabilitiesProperty), out var capabilities, out var capabilityError))
        {
            throw new ProjectRunException(
                $"'{singleFile.Name}' declares {CapabilitiesProperty} that cannot be used. {capabilityError}");
        }

        return new SingleFileManifestInfo(packageName, displayName, publisherDN, version, description, capabilities);
    }

    /// <summary>
    /// Resolves <c>Identity/@Version</c> from <c>WinAppVersion</c>, else the standard MSBuild
    /// <c>$(Version)</c> so <c>#:property Version=2.1.0</c> sets the assembly and package versions together.
    /// </summary>
    /// <remarks>
    /// <c>$(Version)</c> cannot be used raw. <c>Identity/@Version</c> is schema-constrained to exactly four
    /// components, each 0-65535, while MSBuild's default <c>$(Version)</c> is the three-part <c>1.0.0</c>
    /// and a real one legitimately carries a semver suffix (<c>1.2.3-preview.4</c>).
    /// <para>
    /// <c>$(VersionPrefix)</c> is NOT a safe shortcut for stripping that suffix: setting <c>Version</c>
    /// explicitly leaves <c>VersionPrefix</c> EMPTY, so reading it first silently discards the user's
    /// version.
    /// </para>
    /// So: cut at the first <c>-</c>, then pad to four components. An out-of-range or over-long value is
    /// REJECTED rather than truncated, because silently shipping a different version than the one the user
    /// wrote is worse than an actionable error.
    /// </remarks>
    private static string ResolveVersion(FileInfo singleFile, IReadOnlyDictionary<string, string> properties)
    {
        var declaredBy = VersionProperty;
        var raw = Read(properties, VersionProperty);
        if (raw is null)
        {
            declaredBy = "Version";
            raw = Read(properties, "Version");
        }

        if (raw is null)
        {
            return DefaultVersion;
        }

        // Cut the semver pre-release/build suffix: 1.2.3-preview.4 → 1.2.3, 2.0.0+build.55 → 2.0.0.
        var core = raw.Split('-', 2)[0].Split('+', 2)[0].Trim();

        // System.Version needs at least Major.Minor, so a single-component version (a legal, if unusual,
        // $(Version) of "7") would otherwise be rejected instead of padded to 7.0.0.0. Padding here rather
        // than in the shared normalizer keeps `manifest generate`'s behavior untouched.
        if (core.Length > 0 && !core.Contains('.'))
        {
            core += ".0";
        }

        // Require the whole remaining value to be numeric components before normalizing. The shared
        // normalizer deliberately parses only a LEADING numeric token, because it also handles decorated
        // file metadata like "10.0.26100.1 (WinBuild.160101.0800)" — but that leniency is wrong for a
        // hand-written directive: it would turn '1.2.3oops' into 1.2.3.0 and ship a version the user never
        // wrote, contradicting the documented promise that unusable input is rejected.
        var normalized = StrictVersionRegex().IsMatch(core)
            ? ManifestService.NormalizeManifestVersion(core)
            : null;
        if (normalized is null)
        {
            throw new ProjectRunException(
                $"'{singleFile.Name}' declares {declaredBy}='{raw}', which cannot be used as a package version. " +
                "An MSIX Identity version needs up to four components, each between 0 and 65535 (for example 1.2.3.0). " +
                $"Set '#:property {VersionProperty}=<Major.Minor.Build.Revision>' to give the package its own version.");
        }

        return normalized;
    }

    /// <summary>
    /// Reads a property, mapping both "absent" and the empty string an undeclared MSBuild property
    /// evaluates to onto <see langword="null"/>, so callers express defaults with <c>??</c>.
    /// </summary>
    private static string? Read(IReadOnlyDictionary<string, string> properties, string name)
    {
        if (!properties.TryGetValue(name, out var value))
        {
            return null;
        }

        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>MSBuild property pointing at a hand-authored manifest, mirroring the NuGet targets' escape hatch.</summary>
    internal const string ManifestPathProperty = "WinAppManifestPath";

    /// <summary>
    /// Finds a manifest the app author supplied — tiers 2 and 3 of the manifest precedence — or
    /// <see langword="null"/> when the identity is inferred instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared by <c>winapp run</c> (which generates a manifest when this returns null) and
    /// <c>winapp unregister</c> (which infers the identity instead). Both must agree on which manifest an
    /// app registers under, so the probe lives in one place rather than being restated per command.
    /// </para>
    /// <para>
    /// Only the per-file <c>&lt;stem&gt;.appxmanifest</c> name is discovered implicitly. The
    /// directory-wide names (<c>Package.appxmanifest</c>, <c>appxmanifest.xml</c>) are deliberately NOT
    /// probed: file-based apps are per-file and can sit side by side, so a shared
    /// <c>Package.appxmanifest</c> would silently be applied to every <c>.cs</c> in the folder —
    /// registering <c>bar.cs</c> under <c>foo.cs</c>'s identity. A user who genuinely wants one manifest
    /// for several files points at it explicitly with <c>--manifest</c> or
    /// <c>#:property WinAppManifestPath</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="ProjectRunException">Thrown when <c>WinAppManifestPath</c> names a file that does not exist.</exception>
    public static AuthoredSingleFileManifest? FindAuthoredManifest(
        FileInfo singleFile,
        IReadOnlyDictionary<string, string> properties)
    {
        var sourceDirectory = singleFile.DirectoryName ?? ".";

        // Tier 2: an explicit WinAppManifestPath declared by the app itself.
        var declaredPath = Read(properties, ManifestPathProperty);
        if (declaredPath is not null)
        {
            var declared = new FileInfo(Path.GetFullPath(declaredPath, sourceDirectory));
            if (!declared.Exists)
            {
                throw new ProjectRunException(
                    $"'{singleFile.Name}' sets {ManifestPathProperty} to '{declared.FullName}', but no file exists there. " +
                    "Point it at an existing manifest, or remove it to let winapp generate one.");
            }

            return new AuthoredSingleFileManifest(declared, $"declared by {ManifestPathProperty}");
        }

        // Tier 3: a manifest the user authored next to the .cs. Reduce the stem to a bare name first: it
        // originates from the user-supplied input path, and a rooted or separator-bearing value would make
        // the join discard sourceDirectory and probe somewhere else entirely.
        var stem = Path.GetFileName(Path.GetFileNameWithoutExtension(singleFile.Name));
        if (string.IsNullOrEmpty(stem))
        {
            return null;
        }

        var authoredPath = Path.Join(sourceDirectory, $"{stem}.appxmanifest");
        return File.Exists(authoredPath)
            ? new AuthoredSingleFileManifest(new FileInfo(authoredPath), "found next to the file")
            : null;
    }

    /// <summary>
    /// Resolves the <c>Identity/@Name</c> a file-based app registers under, without building it.
    /// </summary>
    /// <remarks>
    /// An authored manifest is authoritative; otherwise the name is INFERRED with the same rules
    /// <c>winapp run</c> applies, rather than read back from the generated manifest. Inference is
    /// deterministic and needs nothing on disk, so this still resolves after the SDK's temp output has
    /// been cleaned — which is exactly when a user reaches for <c>winapp unregister</c>.
    /// </remarks>
    /// <exception cref="ProjectRunException">Thrown when an authored manifest is unreadable or declares no identity.</exception>
    public static string ResolvePackageName(
        FileInfo singleFile,
        IReadOnlyDictionary<string, string> properties)
    {
        var authored = FindAuthoredManifest(singleFile, properties);
        if (authored is null)
        {
            // Deliberately InferPackageName rather than Plan: Plan also validates the version, publisher
            // and description, none of which affect which package is registered. Going through it would
            // mean an unrelated invalid edit — say adding '#:property WinAppVersion=70000.0' — made an
            // ALREADY-REGISTERED app impossible to unregister, stranding the registration precisely when
            // the user is trying to clean it up.
            return InferPackageName(singleFile, properties);
        }

        string? identityName;
        try
        {
            identityName = AppxManifestDocument.Load(authored.File.FullName).IdentityName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            throw new ProjectRunException(
                $"Could not read the manifest '{authored.File.FullName}' ({authored.Source}): {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(identityName))
        {
            throw new ProjectRunException(
                $"The manifest '{authored.File.FullName}' ({authored.Source}) declares no Identity/@Name.");
        }

        return identityName;
    }

    /// <summary>
    /// Infers <c>Identity/@Name</c>: the declared <c>WinAppPackageName</c>, else the file stem with a
    /// short hash of its full path appended.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="Plan"/> so the identity can be resolved on its own. Deciding WHICH
    /// package an app registers under must not depend on metadata that only affects what the manifest
    /// CONTAINS — otherwise an invalid version or publisher would make an already-registered app
    /// impossible to unregister.
    /// <para>
    /// The path hash is what keeps two same-named files apart. Without it, every <c>counter.cs</c> on the
    /// machine shares the identity <c>counter</c> under the default publisher, so running one replaces
    /// another's registration AND hands it the first app's <c>LocalState</c> — an app silently reading and
    /// overwriting an unrelated app's saved data. The execution alias is derived from this identity, so it
    /// becomes unique here too. It is hashed from the full path, so it is stable across runs,
    /// configurations and machines with the same layout, and changes only if the file moves.
    /// </para>
    /// <para>
    /// <c>Identity/@Name</c> must match <c>[-.A-Za-z0-9]+</c>, so a stem like <c>my counter</c> needs
    /// sanitizing. A declared <c>WinAppPackageName</c> is used verbatim apart from that same sanitizing —
    /// naming the package is how a user opts into a stable identity they control, and a value that cannot
    /// be an Identity name is corrected exactly as <c>manifest generate</c> does rather than silently
    /// producing an unpackable manifest.
    /// </para>
    /// </remarks>
    private static string InferPackageName(FileInfo singleFile, IReadOnlyDictionary<string, string> properties)
    {
        var declared = Read(properties, PackageNameProperty);
        if (declared != null)
        {
            return ManifestService.CleanPackageName(declared);
        }

        // Reserve room for the suffix BEFORE sanitizing. CleanPackageName caps at the schema's 50-char
        // limit, so appending afterwards would push a long stem past it and fail registration with an
        // opaque 0xC00CE169. Trimming the stem is safe: the hash is what makes the identity unique.
        var suffix = $"-{ComputePathSuffix(singleFile)}";
        var stem = Path.GetFileNameWithoutExtension(singleFile.Name);
        if (stem.Length > MaxPackageNameLength - suffix.Length)
        {
            stem = stem[..(MaxPackageNameLength - suffix.Length)];
        }

        return ManifestService.CleanPackageName(stem) + suffix;
    }

    /// <summary>The <c>Identity/@Name</c> maximum the AppX schema enforces.</summary>
    private const int MaxPackageNameLength = 50;

    /// <summary>
    /// Eight lowercase hex characters of the SHA-256 of the file's full path, case-insensitively
    /// normalized because Windows paths are.
    /// </summary>
    private static string ComputePathSuffix(FileInfo singleFile)
    {
        var normalized = singleFile.FullName.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    /// <summary>One to four dot-separated numeric components, and nothing else.</summary>
    [GeneratedRegex(@"^\d+(\.\d+){0,3}$")]
    private static partial Regex StrictVersionRegex();
}
