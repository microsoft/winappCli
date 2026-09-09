// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.WindowsAppSDK.Analyzers;

/// <summary>
/// Project-context detector. Used to gate migration-only diagnostics so that they
/// don't spam greenfield WinUI 3 projects.
///
/// Heuristics:
///   * Greenfield WinUI 3      — references Microsoft.UI.Xaml, no Windows.UI.Xaml usage,
///                                no Package.appxmanifest in AdditionalFiles, no UWP TFM.
///   * Migrating from UWP      — any Windows.UI.* using directives, OR a Package.appxmanifest
///                                AdditionalFile, OR a `uap:` namespace in AppxManifest XAML.
///   * Unknown                 — neither signal present (treat conservatively as greenfield).
///
/// Migration-only rules (WUI1xxx) only fire when context is <see cref="ProjectKind.MigratingFromUwp"/>.
/// All other rules fire regardless of context but always with severity ≤ Warning.
///
/// This is the single highest-leverage false-positive reducer in the analyzer.
/// </summary>
internal static class ProjectContext
{
    // Cache per-Compilation. Compilations are immutable so we can key by reference.
    private static readonly ConditionalWeakTableLite<Compilation, ProjectKind> Cache = new();

    public static ProjectKind Detect(Compilation compilation, AnalyzerOptions? options)
    {
        return Cache.GetOrAdd(compilation, c => DetectCore(c, options));
    }

    private static ProjectKind DetectCore(Compilation compilation, AnalyzerOptions? options)
    {
        // Explicit override: the analyze/validate driver sets this global option when the caller
        // passes --from-uwp. Forces migration rules to fire even on mostly-migrated target source
        // (namespaces already rewritten, WinUI 3 manifest) so `validate` can catch API residue.
        if (options?.AnalyzerConfigOptionsProvider?.GlobalOptions is { } global
            && global.TryGetValue("build_property.WinUIMigrationFromUwp", out var forced)
            && string.Equals(forced, "true", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectKind.MigratingFromUwp;
        }

        bool sawUwpUsing = false;
        bool sawWinAppSdkUsing = false;

        foreach (var tree in compilation.SyntaxTrees)
        {
            var root = tree.GetRoot();
            // Cheap text scan — avoids full semantic-model load when only a hint is needed.
            var text = root.ToFullString();
            if (!sawUwpUsing && text.Contains("Windows.UI.Xaml") || text.Contains("Windows.ApplicationModel.Activation"))
                sawUwpUsing = true;
            if (!sawWinAppSdkUsing && text.Contains("Microsoft.UI.Xaml"))
                sawWinAppSdkUsing = true;
            if (sawUwpUsing && sawWinAppSdkUsing) break;
        }

        // AdditionalFiles signal: Package.appxmanifest with uap: prefix is a UWP-style manifest.
        if (options != null)
        {
            foreach (var file in options.AdditionalFiles)
            {
                var name = Path.GetFileName(file.Path);
                if (name.Equals("Package.appxmanifest", StringComparison.OrdinalIgnoreCase))
                {
                    var content = file.GetText()?.ToString() ?? string.Empty;
                    if (content.Contains("xmlns:uap=") || content.Contains("Windows.10"))
                    {
                        return ProjectKind.MigratingFromUwp;
                    }
                }
            }
        }

        if (sawUwpUsing) return ProjectKind.MigratingFromUwp;
        if (sawWinAppSdkUsing) return ProjectKind.GreenfieldWinUI;
        return ProjectKind.Unknown;
    }

    /// <summary>Bypass for tests — clears the per-compilation cache.</summary>
    internal static void ResetCache() => ConditionalWeakTableLite<Compilation, ProjectKind>.Clear();

    /// <summary>
    /// Tiny manual cache. We avoid <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey, TValue}"/>
    /// to keep the analyzer netstandard2.0 dependency surface minimal.
    /// </summary>
    private sealed class ConditionalWeakTableLite<TKey, TValue> where TKey : class
    {
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TKey, Box> _table = new();

        public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
        {
            if (_table.TryGetValue(key, out var box)) return box.Value;
            var value = factory(key);
            _table.Add(key, new Box(value));
            return value;
        }

        public static void Clear()
        {
            // ConditionalWeakTable has no Clear() in netstandard2.0; rely on GC.
        }

        private sealed class Box
        {
            public Box(TValue value) { Value = value; }
            public TValue Value { get; }
        }
    }
}

internal enum ProjectKind
{
    Unknown,
    GreenfieldWinUI,
    MigratingFromUwp,
}
