// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

/// <summary>
/// Writes a WinUI sample index (<c>docs/winui-sample-index.schema.json</c>) from scenarios
/// we already hold. This is the reference generator behind the upstream ask in
/// <see href="https://github.com/microsoft/winappCli/issues/703">#703</see>: rather than
/// asking WinUI-Gallery and CommunityToolkit to design and build an index, each PR can
/// arrive as "here is the file, here is the tested code that produced it, here is the
/// schema it validates against."
///
/// <para>It is also the other half of the Phase 0 proof. Round-tripping our corpus through
/// <see cref="Write"/> and back through <see cref="SampleIndexParser.Parse"/> must return
/// the same scenarios — if it doesn't, the schema is missing a field and we'd have found
/// that out after an upstream maintainer merged it.</para>
///
/// <para>Output is deterministic: controls are ordered by id and no timestamp is written
/// unless one is asked for, so re-generating against unchanged upstream data produces a
/// byte-identical file and a drift check compares content rather than noise.</para>
///
/// <para>This lives in the build-only baker rather than the shipped CLI on purpose: winapp
/// <em>reads</em> published indexes at runtime (<see cref="SampleIndexParser"/>, which
/// <c>ReactorFetcher</c> already uses) and never writes one. Generating an index is a
/// maintenance task we run to produce the reference artifact, so it stays out of the
/// product the same way the corpus bake does.</para>
/// </summary>
internal static class SampleIndexWriter
{
    /// <summary>
    /// Serialize <paramref name="scenarios"/> (and their curated <paramref name="tags"/>) as
    /// an index document.
    /// </summary>
    /// <param name="scenarios">Scenarios to index. Grouped by
    /// <see cref="Scenario.ControlId"/>; control-level metadata is taken from the first
    /// scenario of each group.</param>
    /// <param name="tags">Supplementary per-control search terms, keyed by control id.</param>
    /// <param name="curatedKeywords">Author-written per-control search terms, keyed by
    /// control id. Written to a separate field so they keep their higher search weight.</param>
    /// <param name="source">Value for the document's <c>source</c> field. Defaults to the
    /// <see cref="Scenario.Source"/> of the first scenario.</param>
    /// <param name="generatedAtUtc">Optional provenance stamp. Omitted by default because a
    /// timestamp makes every regeneration a diff, which defeats drift detection.</param>
    internal static string Write(
        IEnumerable<Scenario> scenarios,
        IReadOnlyDictionary<string, string[]>? tags = null,
        IReadOnlyDictionary<string, string[]>? curatedKeywords = null,
        string? source = null,
        DateTimeOffset? generatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(scenarios);

        var ordered = scenarios
            .GroupBy(s => s.ControlId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            // The output is a JSON file read by JSON parsers, never interpolated into HTML
            // or script. Default escaping would render every '<' in a XAML sample as
            // \u003C, which makes the artifact unreviewable — and being reviewable as a
            // diff is the point of publishing it. Quotes, backslashes and control
            // characters are still escaped, and consumers keep ScenarioSanitizer as the
            // trust boundary on read.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber(SampleIndexSchema.SchemaVersion, SampleIndexSchema.Version);

            var sourceId = source ?? ordered.FirstOrDefault()?.First().Source;
            if (!string.IsNullOrEmpty(sourceId))
            {
                writer.WriteString(SampleIndexSchema.Source, sourceId);
            }

            if (generatedAtUtc is { } stamp)
            {
                writer.WriteString(SampleIndexSchema.GeneratedAtUtc, stamp.UtcDateTime.ToString("O"));
            }

            writer.WriteStartArray(SampleIndexSchema.Controls);
            foreach (var group in ordered)
            {
                WriteControl(writer, group.Key, [.. group], tags, curatedKeywords);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteControl(
        Utf8JsonWriter writer,
        string controlId,
        IReadOnlyList<Scenario> group,
        IReadOnlyDictionary<string, string[]>? tags,
        IReadOnlyDictionary<string, string[]>? curatedKeywords)
    {
        var first = group[0];

        // details/xmlnsImports are written on the control when every sample agrees, and on
        // each sample when they don't. Hoisting the shared case keeps the file compact
        // (Gallery repeats one description across all of a control's samples); falling back
        // to per-sample keeps it lossless where samples genuinely differ, as Toolkit's do.
        var sharedDetails = AllAgree(group, s => s.Description ?? "") ? first.Description ?? "" : null;
        var sharedXmlns = AllAgree(group, s => string.Join("\u001f", s.XmlnsImports)) ? first.XmlnsImports : null;

        writer.WriteStartObject();
        writer.WriteString(SampleIndexSchema.Id, controlId);
        WriteIfPresent(writer, SampleIndexSchema.Name, first.ControlName);
        WriteIfPresent(writer, SampleIndexSchema.Description, first.ControlDescription);
        WriteIfPresent(writer, SampleIndexSchema.Details, sharedDetails);
        WriteIfPresent(writer, SampleIndexSchema.ApiNamespace, first.ApiNamespace);
        WriteIfPresent(writer, SampleIndexSchema.NuGetPackage, first.NuGetPackage);
        WriteIfPresent(writer, SampleIndexSchema.RelatedControls, first.RelatedControls);
        if (sharedXmlns is not null)
        {
            WriteIfPresent(writer, SampleIndexSchema.XmlnsImports, sharedXmlns);
        }

        // No "usings" is emitted: control-level usings have already been folded into each
        // scenario's C# by the time we hold it, and emitting them as well would make a
        // reader prepend them a second time.

        if (tags is not null && tags.TryGetValue(controlId, out var keywords))
        {
            WriteIfPresent(writer, SampleIndexSchema.Keywords, keywords);
        }

        if (curatedKeywords is not null && curatedKeywords.TryGetValue(controlId, out var authorKeywords))
        {
            WriteIfPresent(writer, SampleIndexSchema.CuratedKeywords, authorKeywords);
        }

        if (first.Docs.Length > 0)
        {
            writer.WriteStartArray(SampleIndexSchema.Docs);
            foreach (var doc in first.Docs)
            {
                writer.WriteStartObject();
                WriteIfPresent(writer, SampleIndexSchema.Title, doc.Title);
                writer.WriteString(SampleIndexSchema.Uri, doc.Uri);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteStartArray(SampleIndexSchema.Samples);
        foreach (var scenario in group)
        {
            writer.WriteStartObject();
            WriteIfPresent(writer, SampleIndexSchema.Header, scenario.HeaderText);
            WriteIfPresent(writer, SampleIndexSchema.Xaml, scenario.Xaml);
            WriteIfPresent(writer, SampleIndexSchema.Code, scenario.CSharp);

            // Only written when it wasn't hoisted to the control above. An explicit empty
            // string is required when this sample has no details but a sibling does —
            // otherwise the reader would fall back to the control value and invent one.
            if (sharedDetails is null)
            {
                writer.WriteString(SampleIndexSchema.Details, scenario.Description ?? "");
            }

            if (sharedXmlns is null)
            {
                writer.WriteStartArray(SampleIndexSchema.XmlnsImports);
                foreach (var import in scenario.XmlnsImports)
                {
                    writer.WriteStringValue(import);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>True when <paramref name="selector"/> returns the same value for every
    /// scenario in <paramref name="group"/>.</summary>
    private static bool AllAgree(IReadOnlyList<Scenario> group, Func<Scenario, string> selector)
    {
        var first = selector(group[0]);
        for (int i = 1; i < group.Count; i++)
        {
            if (!string.Equals(selector(group[i]), first, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string[] values)
    {
        if (values.Length == 0)
        {
            return;
        }

        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
