// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text.Json.Serialization;

[JsonSerializable(typeof(Scenario[]))]
[JsonSerializable(typeof(CorePattern[]))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(DocLink[]))]
[JsonSerializable(typeof(ProviderSnapshot))]
[JsonSerializable(typeof(SnapshotManifest))]
internal partial class ControlsJsonContext : JsonSerializerContext { }

/// <summary>
/// Write-side context for baked snapshots: indented so the committed corpus produces a
/// reviewable line-by-line diff instead of one unreadable mega-line. Reading goes through
/// <see cref="ControlsJsonContext"/>; only the build-time snapshot baker uses this.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ProviderSnapshot))]
[JsonSerializable(typeof(SnapshotManifest))]
internal partial class ControlsSnapshotWriteContext : JsonSerializerContext { }

/// <summary>
/// One provider's baked corpus, as committed under <c>Services/Controls/Data</c>
/// and embedded (Brotli-compressed) in the binary. Mirrors <see cref="ProviderData"/>
/// but as a single self-describing document rather than the three loose files the
/// per-user cache writes, so the embedded floor is one resource per provider.
///
/// The dictionaries are sorted rather than hashed so a re-bake of unchanged upstream
/// content is byte-identical. Without that, every bake would emit a reordered file and
/// the drift check could not tell a real corpus change from serialization noise.
/// </summary>
internal sealed class ProviderSnapshot
{
    [JsonPropertyName("scenarios")] public Scenario[] Scenarios { get; set; } = [];
    [JsonPropertyName("tags")] public SortedDictionary<string, string[]> Tags { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("keywords")] public SortedDictionary<string, string[]> Keywords { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Metadata for the embedded snapshot set. <see cref="BakedAtUtc"/> is the moment the
/// corpus was pulled from upstream — directly comparable with the per-user cache's
/// <c>last-updated.txt</c>, which is what lets a freshly-installed binary prefer its
/// own snapshot over an older on-disk cache. <see cref="CacheVersion"/> pins the
/// parse/extraction logic the snapshot was produced by.
/// </summary>
internal sealed class SnapshotManifest
{
    [JsonPropertyName("bakedAtUtc")] public DateTime BakedAtUtc { get; set; }
    [JsonPropertyName("cacheVersion")] public string CacheVersion { get; set; } = "";
    [JsonPropertyName("scenarioCounts")] public SortedDictionary<string, int> ScenarioCounts { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class Scenario
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("controlId")] public string ControlId { get; set; } = "";
    [JsonPropertyName("controlName")] public string ControlName { get; set; } = "";
    [JsonPropertyName("headerText")] public string HeaderText { get; set; } = "";
    [JsonPropertyName("xaml")] public string? Xaml { get; set; }
    [JsonPropertyName("csharp")] public string? CSharp { get; set; }
    /// <summary>"gallery", "toolkit", or "reactor". Drives id prefix and metadata output.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "gallery";
    /// <summary>NuGet package required to use this control (toolkit only).</summary>
    [JsonPropertyName("nugetPackage")] public string? NuGetPackage { get; set; }
    /// <summary>XAML namespace declarations needed (e.g., xmlns:controls="...") (toolkit only).</summary>
    [JsonPropertyName("xmlnsImports")] public string[] XmlnsImports { get; set; } = [];
    /// <summary>Longer description from ControlInfoData.json (Gallery only).</summary>
    [JsonPropertyName("description")] public string? Description { get; set; }
    /// <summary>Control-level one-line concept summary. For gallery: ControlInfoData.Subtitle
    /// (short, median 68 chars). For toolkit: md frontmatter description. Surfaced in search
    /// list as "[gallery] Name — &lt;summary&gt;".</summary>
    [JsonPropertyName("controlDescription")] public string? ControlDescription { get; set; }
    /// <summary>Related WinUI 3 controls — names of "see also" alternatives/pairings (Gallery only).</summary>
    [JsonPropertyName("relatedControls")] public string[] RelatedControls { get; set; } = [];
    /// <summary>API namespace from ControlInfoData.json (Gallery only). Surfaced in output
    /// only when non-default — helps agents pick the right `using`/`xmlns` for long-tail
    /// controls in Microsoft.Windows.*, Microsoft.UI.Windowing, etc. Empty when unknown
    /// or when the standard Microsoft.UI.Xaml.Controls namespace is enough.</summary>
    [JsonPropertyName("apiNamespace")] public string? ApiNamespace { get; set; }
    /// <summary>C# namespaces the control's samples import, from the index's control-level
    /// <c>usings</c> (index sources only). Rendered as its own "Namespace:" line rather than
    /// prepended to <see cref="CSharp"/>: the sample is a class-body fragment, so a `using`
    /// placed inside it sits where C# does not allow one. Carries the imports a consumer
    /// cannot infer — a source parser cannot see the host project's global usings, so treat
    /// this as the non-obvious imports rather than a complete set.</summary>
    [JsonPropertyName("usings")] public string[] Usings { get; set; } = [];
    /// <summary>Official documentation links (API reference, guidelines, etc.) (Gallery only).</summary>
    [JsonPropertyName("docs")] public DocLink[] Docs { get; set; } = [];
}

internal sealed class DocLink
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("uri")] public string Uri { get; set; } = "";
}

internal sealed class CorePattern
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("scenario")] public string Scenario { get; set; } = "";
    [JsonPropertyName("tags")] public string[] Tags { get; set; } = [];
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("prerequisites")] public string[] Prerequisites { get; set; } = [];
    [JsonPropertyName("xaml")] public string? Xaml { get; set; }
    [JsonPropertyName("csharp")] public string CSharp { get; set; } = "";
    [JsonPropertyName("notes")] public string[] Notes { get; set; } = [];
}
