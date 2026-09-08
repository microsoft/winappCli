// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WinApp.Cli.Helpers;

/// <summary>
/// Produces the corpus that <c>EmbeddedSnapshot</c> serves. Lives in the build-time
/// <c>WinApp.Cli.SnapshotBaker</c> tool rather than in the CLI, so regenerating the
/// corpus is not reachable from the shipped product. Invoked by
/// <c>scripts/build-cli.ps1</c> on the release path.
///
/// Only the Brotli blob is written and committed. The corpus is a backup: whenever the
/// network is reachable the CLI serves live data, so the embedded copy exists for the
/// offline case and is refreshed wholesale at every release — which makes release time,
/// not any committed text file, the source of truth for what it contains.
/// <c>snapshot-manifest.json</c> stays readable and committed, so scenario counts per
/// source remain reviewable in a diff without carrying roughly 900 KB of duplicated JSON.
/// </summary>
internal static class SnapshotBaker
{
    /// <summary>Filename for a provider's committed snapshot — the Brotli blob that is
    /// embedded in the binary, and the only per-provider corpus file written.</summary>
    public static string SnapshotFileName(string providerId) => $"snapshot-{providerId}.json.br";

    public const string ManifestFileName = "snapshot-manifest.json";

    /// <summary>Filename for a source's generated upstream index — the artifact offered to
    /// the repository that owns the samples (see
    /// <see href="https://github.com/microsoft/winappCli/issues/703">#703</see>).</summary>
    public static string IndexFileName(string providerId) => $"index-{providerId}.json";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Brotli-compress <paramref name="json"/> to <paramref name="path"/>. Compression is
    /// deterministic for identical input, which is what lets the drift job compare a fresh
    /// bake against the committed blob without a second, uncompressed copy in the repo.
    /// </summary>
    private static async Task WriteSnapshotAsync(string path, string json, CancellationToken cancellationToken)
    {
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await brotli.WriteAsync(Utf8NoBom.GetBytes(json), cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllBytesAsync(path, compressed.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetch every provider fresh and write the snapshot set to <paramref name="outputDirectory"/>.
    /// Reports per-provider counts through <paramref name="report"/>.
    /// </summary>
    /// <returns>
    /// The ids of providers that could not be baked. Empty means a complete bake. A
    /// partial result is never written as if it were complete — the whole set is built in a
    /// staging directory and only moved into <paramref name="outputDirectory"/> once every
    /// provider and the manifest have succeeded, so a failed, cancelled, or crashed bake
    /// leaves the previous committed corpus exactly as it was.
    /// </returns>
    public static async Task<IReadOnlyList<string>> BakeAsync(
        string outputDirectory,
        Action<string> report,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        // Providers cache to disk as a side effect of loading. Point them at a throwaway
        // directory so a bake never reads from — or writes to — the developer's real
        // find-ui cache, which would let a warm local cache masquerade as a fresh fetch.
        var scratchCache = Path.Join(Path.GetTempPath(), $"winapp-bake-{Guid.NewGuid():N}");

        // Snapshots land here first and are moved into place only after the whole set
        // succeeds. Writing them straight into outputDirectory would leave a provider that
        // succeeded next to a manifest from the previous bake if a later provider failed —
        // fresh scenarios stamped with a stale bake time. Staged *inside* outputDirectory
        // so the publish below is a same-volume move rather than a cross-volume copy, and
        // dot-prefixed so it can't be picked up by the `snapshot-*` globs in the csproj or
        // in build-cli.ps1.
        var staging = Path.Join(outputDirectory, $".bake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        var failures = new List<string>();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        try
        {
            foreach (var provider in ProviderRegistry.CreateProviders(scratchCache))
            {
                cancellationToken.ThrowIfCancellationRequested();
                report($"Fetching {provider.DisplayName}…");

                var data = await provider.LoadAsync(forceRefresh: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                // LoadAsync falls back through cache and embedded snapshot when a fetch
                // fails. For a bake that fallback is a trap: it would re-emit the corpus
                // already in the binary and look like a successful refresh. Only a genuine
                // network result is acceptable here.
                if (data.Origin != CorpusOrigin.Network || data.Scenarios.Length == 0)
                {
                    report($"  FAILED: {provider.DisplayName} returned no fetched data.");
                    failures.Add(provider.Id);
                    continue;
                }

                var snapshot = new ProviderSnapshot
                {
                    Scenarios = data.Scenarios,
                    Tags = new SortedDictionary<string, string[]>(data.Tags, StringComparer.Ordinal),
                    Keywords = new SortedDictionary<string, string[]>(data.Keywords, StringComparer.Ordinal)
                };

                var path = Path.Join(staging, SnapshotFileName(provider.Id));
                await WriteSnapshotAsync(
                    path,
                    JsonSerializer.Serialize(snapshot, ControlsSnapshotWriteContext.Default.ProviderSnapshot),
                    cancellationToken).ConfigureAwait(false);

                counts[provider.Id] = data.Scenarios.Length;
                report($"  {provider.Id}: {data.Scenarios.Length} scenarios → {Path.GetFileName(path)}");
            }

            if (failures.Count > 0)
            {
                return failures;
            }

            var manifest = new SnapshotManifest
            {
                BakedAtUtc = DateTime.UtcNow,
                CacheVersion = Controls.CacheVersion.Current,
                ScenarioCounts = counts
            };

            await PathSafety.AtomicWriteAllTextAsync(
                Path.Join(staging, ManifestFileName),
                JsonSerializer.Serialize(manifest, ControlsSnapshotWriteContext.Default.SnapshotManifest),
                Utf8NoBom,
                cancellationToken).ConfigureAwait(false);

            Publish(staging, outputDirectory);

            report($"Baked {counts.Count} sources at cache version {Controls.CacheVersion.Current}.");
            return failures;
        }
        finally
        {
            TryDeleteDirectory(scratchCache);
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Fetch every provider fresh and write each one's data as a sample index in the shared
    /// contract (<c>docs/winui-sample-index.schema.json</c>) to
    /// <paramref name="outputDirectory"/>.
    /// </summary>
    /// <remarks>
    /// <para>This is the reference generator behind
    /// <see href="https://github.com/microsoft/winappCli/issues/703">#703</see>. Its output
    /// belongs in the repository that owns the samples, not this one — so each upstream ask
    /// can be "here is the file, here is the schema it validates against, and here is the
    /// tested code that produced it" rather than a request that someone else design a
    /// format. Nothing it writes is committed here or shipped: the corpus we embed is the
    /// Brotli snapshot written by <see cref="BakeAsync"/>, and an index checked in
    /// alongside it would be the same data a second time.</para>
    ///
    /// <para>Deliberately separate from <see cref="BakeAsync"/> rather than an extra output
    /// of it. The bake runs on every release and its failure mode is a release-blocking
    /// error; generating an artifact for someone else's repository is occasional, manual
    /// work that must not be able to fail a release.</para>
    ///
    /// <para>This is a <em>usable-only</em> export, not a verbatim dump of what was fetched.
    /// Scenarios are sanitized and any left with neither XAML nor C# are omitted, so the
    /// counts it reports can be lower than the counts <see cref="BakeAsync"/> shows for the
    /// same sources. That is deliberate: those samples are already dropped before search, so
    /// writing them here would hand an upstream maintainer known-broken content to publish.
    /// Losslessness is a property of the <em>schema</em> — a scenario that survives is
    /// carried field for field, which is what the round-trip tests assert — not a promise
    /// that every fetched scenario appears.</para>
    /// </remarks>
    /// <returns>The ids of providers that could not be fetched. Empty means every source
    /// was written.</returns>
    public static async Task<IReadOnlyList<string>> EmitIndexesAsync(
        string outputDirectory,
        Action<string> report,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        // Same reasoning as BakeAsync: a warm local cache must not be able to masquerade
        // as a fresh fetch, so providers load through a throwaway cache directory.
        var scratchCache = Path.Join(Path.GetTempPath(), $"winapp-index-{Guid.NewGuid():N}");
        var failures = new List<string>();
        var staging = Path.Join(outputDirectory, $".index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            foreach (var provider in ProviderRegistry.CreateProviders(scratchCache))
            {
                cancellationToken.ThrowIfCancellationRequested();
                report($"Fetching {provider.DisplayName}…");

                var data = await provider.LoadAsync(forceRefresh: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (data.Origin != CorpusOrigin.Network || data.Scenarios.Length == 0)
                {
                    report($"  FAILED: {provider.DisplayName} returned no fetched data.");
                    failures.Add(provider.Id);
                    continue;
                }

                // provider.LoadAsync returns RAW scenarios; ScenarioSanitizer normally runs
                // later, in ControlsSearchService. Without it the reference artifact would
                // carry malformed XAML and unbalanced C# that every consumer then drops —
                // exactly the breakage a published index exists to eliminate.
                ScenarioSanitizer.SanitizeAll(data.Scenarios);
                var usable = data.Scenarios
                    .Where(s => s.Xaml is not null || s.CSharp is not null)
                    .ToArray();

                if (usable.Length == 0)
                {
                    report($"  FAILED: {provider.DisplayName} returned no usable samples.");
                    failures.Add(provider.Id);
                    continue;
                }

                var index = SampleIndexWriter.Write(usable, data.Tags, data.Keywords, provider.Id);
                var path = Path.Join(staging, IndexFileName(provider.Id));

                await PathSafety.AtomicWriteAllTextAsync(path, index, Utf8NoBom, cancellationToken)
                    .ConfigureAwait(false);

                var controls = usable.Select(s => s.ControlId).Distinct(StringComparer.Ordinal).Count();
                var dropped = data.Scenarios.Length - usable.Length;
                var note = dropped > 0 ? $" ({dropped} unusable sample(s) dropped)" : "";
                report($"  {provider.Id}: {controls} controls, {usable.Length} samples → {Path.GetFileName(path)}{note}");
            }

            if (failures.Count > 0)
            {
                return failures;
            }

            Publish(staging, outputDirectory);
            return failures;
        }
        finally
        {
            TryDeleteDirectory(scratchCache);
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Move a complete staged bake into its final home, overwriting the previous corpus.
    /// Called only once every provider and the manifest have been written, so the
    /// destination never holds a mix of the two bakes for longer than this loop.
    /// </summary>
    private static void Publish(string staging, string outputDirectory)
    {
        foreach (var staged in Directory.GetFiles(staging))
        {
            File.Move(staged, Path.Join(outputDirectory, Path.GetFileName(staged)), overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch { /* cleanup of a temp directory is best-effort */ }
    }
}
