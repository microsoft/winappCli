// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>The kind of a type member surfaced by the API metadata index.</summary>
internal enum MemberKind
{
    Method,
    Property,
    Event,
    Field
}

/// <summary>The kind of a type surfaced by the API metadata index.</summary>
internal enum TypeKind
{
    Class,
    Struct,
    Enum,
    Interface,
    Delegate
}

/// <summary>A single parameter of a method member.</summary>
internal sealed class WinMdParameterInfo
{
    public required string Name { get; init; }

    public required string Type { get; init; }
}

/// <summary>A property, method, event, or field declared on a <see cref="WinMdTypeInfo"/>.</summary>
internal sealed class WinMdMemberInfo
{
    public required string Name { get; init; }

    public required MemberKind Kind { get; init; }

    public required string Signature { get; init; }

    public string? ReturnType { get; init; }

    public List<WinMdParameterInfo>? Parameters { get; init; }

    /// <summary>
    /// True for a method called on the type rather than on an instance. Recorded
    /// separately from <see cref="Signature"/> so a consumer can branch on it without
    /// parsing the rendered text.
    /// </summary>
    public bool IsStatic { get; init; }

    /// <summary>
    /// Parameter types spelled the way an XML documentation ID spells them, used only to
    /// look this member up in a documentation file. Not cached: descriptions are merged
    /// during indexing, so this has served its purpose before anything is written out.
    /// </summary>
    [JsonIgnore]
    public List<string>? DocParameterTypes { get; init; }

    /// <summary>Generic arity, which a documentation ID appends to the method name as <c>``n</c>.</summary>
    [JsonIgnore]
    public int GenericParameterCount { get; init; }

    public string? Description { get; set; }

    public string? DeprecatedMessage { get; set; }
}

/// <summary>A WinRT/managed type parsed from a <c>.winmd</c>/<c>.dll</c> plus its XML-doc description.</summary>
internal sealed class WinMdTypeInfo
{
    public required string Namespace { get; init; }

    public required string Name { get; init; }

    public required string FullName { get; init; }

    public required TypeKind Kind { get; init; }

    public string? BaseType { get; init; }

    /// <summary>
    /// The interfaces this type declares, named as a caller writes them
    /// (<c>Windows.Foundation.Collections.IVector&lt;T&gt;</c>). <see langword="null"/>
    /// when the type declares none, so the field is omitted from the cache.
    /// </summary>
    public List<string>? Interfaces { get; init; }

    public required List<WinMdMemberInfo> Members { get; init; }

    public List<string>? EnumValues { get; init; }

    public required string SourceFile { get; init; }

    public string? Description { get; set; }

    public string? DeprecatedMessage { get; set; }
}

/// <summary>A NuGet/SDK package (or project reference) that carries <c>.winmd</c> metadata and XML docs.</summary>
internal sealed record PackageWithWinMd(string Id, string Version, List<string> WinMdFiles, List<string> XmlDocFiles);

/// <summary>A package reference recorded in a cached <see cref="ProjectManifest"/>.</summary>
internal sealed class ProjectPackageRef
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    /// <summary>
    /// Fingerprint of the metadata files this project resolved the package to, including
    /// their sizes and write times. Compared on reuse so a rebuilt referenced project is
    /// re-exported rather than answered from stale metadata.
    /// </summary>
    public required string SourceStamp { get; init; }

    /// <summary>
    /// Fingerprint of the metadata file <em>paths</em> this project resolved the package
    /// to, and the package's cache directory name. Recording it here is what lets the
    /// query side read the export built for <em>this</em> project rather than one another
    /// project wrote for the same id and version from different files.
    /// </summary>
    public required string AssetPathKey { get; init; }
}

/// <summary>The cached manifest describing a project's resolved metadata packages.</summary>
internal sealed class ProjectManifest
{
    public required string ProjectName { get; init; }

    public required string ProjectDir { get; init; }

    public required string ProjectFile { get; init; }

    public required List<ProjectPackageRef> Packages { get; init; }

    public required string GeneratedAt { get; init; }
}

/// <summary>The <c>meta.json</c> summary written alongside each cached package.</summary>
internal sealed class PackageMeta
{
    /// <summary>
    /// The <see cref="ApiCachePaths.CacheFormatVersion"/> this cache was written
    /// with. A cache recording a different version is rebuilt rather than reused.
    /// </summary>
    public int Format { get; init; }

    public required string PackageId { get; init; }

    public required string Version { get; init; }

    /// <summary>
    /// A fingerprint of the metadata files this cache was built from. A cache whose
    /// fingerprint no longer matches the files a project resolves to is rebuilt: the
    /// package id and version do not change when a referenced project is rebuilt, nor
    /// when a different target framework selects different assets from the same package.
    /// Absent in caches written before this field existed, which therefore rebuild once.
    /// </summary>
    public string? SourceStamp { get; init; }

    public required List<string> WinMdFiles { get; init; }

    public required int TotalTypes { get; init; }

    public required int TotalMembers { get; init; }

    public required int TotalNamespaces { get; init; }

    /// <summary>
    /// True when at least one of the package's metadata files could not be parsed,
    /// so the indexed type list is known to be partial. Queries use this to avoid
    /// reporting an authoritative "not found" from an index that is missing data.
    /// </summary>
    public bool Incomplete { get; init; }

    /// <summary>Per-file parse diagnostics, present only when <see cref="Incomplete"/>.</summary>
    public List<string>? ParseErrors { get; init; }

    public required string GeneratedAt { get; init; }
}
