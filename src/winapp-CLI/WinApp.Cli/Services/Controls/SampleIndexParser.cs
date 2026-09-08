// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Linq;
using System.Text.Json;

/// <summary>
/// Reads a WinUI sample index (<c>docs/winui-sample-index.schema.json</c>) and maps it onto
/// the internal <see cref="Scenario"/> model. This is the whole cost of consuming a source
/// that publishes an index — contrast the per-repository scrapers, which reconstruct the
/// same data from folder layout.
///
/// <para>Generalized from <see cref="ReactorFetcher"/>'s parser, which now delegates here.
/// Reactor is the one source with a published index today, so its already-shipping file is
/// the proof that this reader works against real upstream data rather than only against a
/// shape we invented.</para>
///
/// <para>Parsed with <see cref="JsonDocument"/> (AOT-safe, no reflection). Every element is
/// kind-checked before use: this runs on untrusted network content, so a document whose
/// <c>controls</c> or <c>samples</c> arrays hold non-objects skips those entries rather than
/// throwing.</para>
/// </summary>
internal static class SampleIndexParser
{
    /// <summary>
    /// Ceiling on the control-level <c>usings</c> block that gets copied onto every one of
    /// a control's samples. Generous next to real indexes — the largest shipping control
    /// uses a few short namespace names — so it bounds the copy without touching
    /// legitimate content.
    /// </summary>
    private const int MaxUsingsPrefixChars = 8 * 1024;

    /// <summary>
    /// Map an index document to <see cref="Scenario"/>[] plus the two per-control search
    /// dictionaries. <paramref name="source"/> is stamped onto every scenario
    /// (<see cref="Scenario.Source"/>) rather than read from the document, so a source can
    /// never mislabel its samples as another's.
    /// </summary>
    /// <returns>
    /// <c>tags</c> feeds <c>ProviderData.Tags</c> (search weight 3.0) and
    /// <c>curatedKeywords</c> feeds <c>ProviderData.Keywords</c> (weight 5.0). They are
    /// deliberately separate: an author's own terms outrank supplementary ones, and
    /// collapsing them would either dilute the former or inflate the latter.
    /// </returns>
    /// <remarks>
    /// Throws <see cref="JsonException"/> on malformed JSON — callers treat that as a fetch
    /// failure. A structurally valid document that simply carries nothing we understand
    /// (wrong <c>schemaVersion</c>, missing <c>controls</c>) yields an empty result instead.
    /// </remarks>
    internal static (Scenario[] scenarios, Dictionary<string, string[]> tags, Dictionary<string, string[]> curatedKeywords) Parse(
        string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var scenarios = new List<Scenario>();
        var tags = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var curatedKeywords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (root.ValueKind != JsonValueKind.Object || !IsSupportedVersion(root)) return ([], tags, curatedKeywords);

        if (!root.TryGetProperty(SampleIndexSchema.Controls, out var controls)
            || controls.ValueKind != JsonValueKind.Array)
        {
            return ([], tags, curatedKeywords);
        }

        foreach (var control in controls.EnumerateArray().Where(control => control.ValueKind == JsonValueKind.Object))
        {
            var controlId = GetString(control, SampleIndexSchema.Id);
            if (string.IsNullOrEmpty(controlId)) continue;

            var controlName = GetString(control, SampleIndexSchema.Name);
            var summary = GetString(control, SampleIndexSchema.Description);
            var details = GetString(control, SampleIndexSchema.Details);
            var apiNamespace = GetString(control, SampleIndexSchema.ApiNamespace);
            var nugetPackage = GetString(control, SampleIndexSchema.NuGetPackage);
            var relatedControls = GetStringArray(control, SampleIndexSchema.RelatedControls);
            var xmlnsImports = GetStringArray(control, SampleIndexSchema.XmlnsImports);
            var usings = GetStringArray(control, SampleIndexSchema.Usings);
            var keywords = GetStringArray(control, SampleIndexSchema.Keywords);
            var authorKeywords = GetStringArray(control, SampleIndexSchema.CuratedKeywords);
            var docs = GetDocLinks(control);

            // Both dictionaries are served verbatim (not stop-word cleaned) so multi-word
            // intent terms like "css layout" survive; cleaning would drop "layout".
            if (keywords.Length > 0) tags[controlId] = keywords;
            if (authorKeywords.Length > 0) curatedKeywords[controlId] = authorKeywords;

            // Control-level usings are prepended to EACH sample's code so a snippet
            // compiles standalone. Sources are asked not to repeat them inside samples.
            //
            // That prepend is a multiplier: the fetch is byte-capped, but one oversized
            // usings block copied onto every sample of a control expands far past that
            // cap. Real usings are a handful of namespace names, so anything near this
            // limit is malformed or hostile and is worth more than dropping the prefix.
            var usingsPrefix = usings.Length > 0
                ? string.Concat(usings.Select(u => $"using {u};\n")) + "\n"
                : "";
            if (usingsPrefix.Length > MaxUsingsPrefixChars) usingsPrefix = "";

            if (!control.TryGetProperty(SampleIndexSchema.Samples, out var samples)
                || samples.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var index = 0;
            foreach (var sample in samples.EnumerateArray().Where(sample => sample.ValueKind == JsonValueKind.Object))
            {
                var header = GetString(sample, SampleIndexSchema.Header);
                var xaml = GetString(sample, SampleIndexSchema.Xaml);
                var code = GetString(sample, SampleIndexSchema.Code);

                // details/xmlnsImports are control-level defaults a sample may override:
                // sibling samples of one control legitimately differ (Toolkit gives each
                // sample its own description and its own namespace imports).
                var sampleDetails = sample.TryGetProperty(SampleIndexSchema.Details, out _)
                    ? GetString(sample, SampleIndexSchema.Details)
                    : details;
                var sampleXmlns = sample.TryGetProperty(SampleIndexSchema.XmlnsImports, out _)
                    ? GetStringArray(sample, SampleIndexSchema.XmlnsImports)
                    : xmlnsImports;

                // A sample with neither XAML nor code has no usable content. Guard on the
                // raw code (before the usings prefix) so a control that declares only
                // control-level usings can't slip a using-only stub through.
                // Code is pasted into a C# file, so drop it when tagged as another
                // language rather than emitting, say, C++ as if it were C#.
                var hasXaml = !string.IsNullOrWhiteSpace(xaml);
                var hasCode = !string.IsNullOrWhiteSpace(code) && IsCSharp(sample);
                if (!hasXaml && !hasCode) continue;

                // Ids are positional over KEPT samples, so they stay contiguous from 1.
                index++;
                scenarios.Add(new Scenario
                {
                    Id = $"{controlId}-{index}",
                    ControlId = controlId,
                    ControlName = controlName,
                    HeaderText = header,
                    Xaml = hasXaml ? xaml : null,
                    CSharp = hasCode ? usingsPrefix + code : null,
                    Source = source,
                    NuGetPackage = NullIfEmpty(nugetPackage),
                    ApiNamespace = NullIfEmpty(apiNamespace),
                    Description = NullIfEmpty(sampleDetails),
                    ControlDescription = NullIfEmpty(summary),
                    RelatedControls = relatedControls,
                    XmlnsImports = sampleXmlns,
                    Docs = docs,
                });
            }
        }

        return (scenarios.ToArray(), tags, curatedKeywords);
    }

    /// <summary>
    /// True when this reader understands the document's <c>schemaVersion</c>. An absent
    /// version is accepted as version 1: the contract was extracted from an index that
    /// predates it, and refusing that file would break the one source already publishing
    /// one. A version we don't know is refused rather than guessed at — reading a future
    /// document with today's field meanings is how a consumer silently ships wrong samples.
    /// </summary>
    private static bool IsSupportedVersion(JsonElement root)
    {
        if (!root.TryGetProperty(SampleIndexSchema.SchemaVersion, out var version)) return true;
        if (version.ValueKind != JsonValueKind.Number) return false;
        return version.TryGetDecimal(out var value) && value == SampleIndexSchema.Version;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static DocLink[] GetDocLinks(JsonElement control)
    {
        if (!control.TryGetProperty(SampleIndexSchema.Docs, out var docs)
            || docs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<DocLink>();
        foreach (var doc in docs.EnumerateArray().Where(doc => doc.ValueKind == JsonValueKind.Object))
        {
            var uri = GetString(doc, SampleIndexSchema.Uri);
            if (string.IsNullOrEmpty(uri)) continue;

            list.Add(new DocLink { Title = GetString(doc, SampleIndexSchema.Title), Uri = uri });
        }
        return [.. list];
    }

    // v1 carries C# only. An absent tag means C#; anything else is not usable as C#.
    // Read the property directly rather than through GetString, which collapses "absent"
    // and "present but not a string" into the same empty value: a tag of the wrong JSON
    // type is malformed network content, not an omission, so it must not inherit the
    // absent-means-C# default and be handed to the user as paste-ready C#.
    private static bool IsCSharp(JsonElement sample)
        => !sample.TryGetProperty(SampleIndexSchema.Language, out var v)
            || (v.ValueKind == JsonValueKind.String
                && string.Equals(v.GetString(), "csharp", StringComparison.OrdinalIgnoreCase));

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static string[] GetStringArray(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array) return [];

        var list = new List<string>();
        foreach (var item in v.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String))
        {
            var s = item.GetString();
            if (!string.IsNullOrEmpty(s)) list.Add(s);
        }
        return [.. list];
    }
}
