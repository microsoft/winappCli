// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Net.Http;

/// <summary>
/// microsoft-ui-reactor ReactorGallery scenarios. Reads the purpose-built
/// <c>reactor-search-index.json</c> (a schema the Reactor team owns) and maps it
/// onto the internal <see cref="Scenario"/> model. Reactor samples are C#-only
/// declarative WinUI (<c>UseMemo</c>, <c>DataGrid(...)</c>, <c>Column&lt;T&gt;(...)</c>) —
/// kept verbatim, never run through the gallery/toolkit sample cleaners. There is
/// no XAML, so <see cref="Scenario.Xaml"/> stays null.
///
/// Each control's curated <c>keywords</c> become the 3.0-weighted enrichment tag
/// field (<see cref="ProviderData.Tags"/>); they are served VERBATIM — not
/// stop-word cleaned — so multi-word intent terms like "css layout" survive
/// (cleaning would drop the TagOnly stop word "layout" and break searches such as
/// "flex layout"). The controls that declare control-level <c>usings</c>
/// (data-grid, docking, flex, property-grid) get those folded into each sample's
/// C# so the emitted snippet compiles standalone.
///
/// Unlike upstream winui-search, find-ui embeds no offline snapshot: the index is
/// always fetched from GitHub and cached per-user, so the curated keyword tags
/// ride along with the fetch rather than a baked-in <c>reactor-tags.json</c>.
/// </summary>
internal static class ReactorFetcher
{
    /// <summary>Value stamped onto <see cref="Scenario.Source"/>, matching
    /// <c>ReactorProvider.Id</c>. Set by us rather than read from the downloaded document so
    /// a source can't label its samples as another's.</summary>
    private const string SourceId = "reactor";

    private const string IndexUrl =
        "https://raw.githubusercontent.com/microsoft/microsoft-ui-reactor/main/samples/ReactorGallery/reactor-search-index.json";

    private static readonly HttpClient Http = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "winapp-find-ui/1.0" } },
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>Fetch fresh scenarios + tags from GitHub. Reactor C# and curated
    /// keywords are kept verbatim. The body is streamed through the shared
    /// byte-capped helper so an accidentally-huge upstream file can't exhaust
    /// memory.</summary>
    internal static async Task<(Scenario[] scenarios, Dictionary<string, string[]> tags)> FetchAsync(
        CancellationToken cancellationToken = default)
    {
        var json = await ControlsHttpHelper.GetStringCappedAsync(Http, IndexUrl, cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>Map the <c>reactor-search-index.json</c> document to
    /// <see cref="Scenario"/>[] + the per-control tag dictionary.</summary>
    /// <remarks>
    /// The mapping itself lives in <see cref="SampleIndexParser"/>, the shared reader for the
    /// index contract in <c>docs/winui-sample-index.schema.json</c>. That contract was
    /// generalized FROM this file, so Reactor's published index is what proves the shared
    /// reader works against real upstream data — and every source that publishes an index
    /// under <see href="https://github.com/microsoft/winappCli/issues/703">#703</see> gets a
    /// fetcher this size instead of a scraper.
    ///
    /// <para>Reactor publishes no <c>curatedKeywords</c>, so that slot is empty here and the
    /// tuple stays two-wide, exactly as <c>ReactorProvider</c> already expects. Its
    /// <c>keywords</c> continue to feed the 3.0-weighted tag field they always have.</para>
    /// </remarks>
    internal static (Scenario[] scenarios, Dictionary<string, string[]> tags) Parse(string json)
    {
        var (scenarios, tags, _) = SampleIndexParser.Parse(json, SourceId);
        return (scenarios, tags);
    }
}
