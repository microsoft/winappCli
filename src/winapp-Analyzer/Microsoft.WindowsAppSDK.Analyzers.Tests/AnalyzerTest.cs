// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Microsoft.WindowsAppSDK.Analyzers.Tests;

/// <summary>
/// Lightweight analyzer test harness that runs an analyzer against an in-memory
/// compilation and asserts on the produced diagnostic IDs (and optionally severities/counts).
///
/// We deliberately avoid <c>Microsoft.CodeAnalysis.CSharp.Analyzer.Testing</c>'s strict
/// span-matching: our rule set frequently reports at heuristic locations (XAML
/// AdditionalFiles, fuzzy code-behind correlation) where pinning exact spans in tests
/// is more brittle than the rule itself. ID-and-count assertions remain a strong test
/// because every analyzer is exercised end-to-end through the real Roslyn pipeline.
/// </summary>
public sealed class AnalyzerTest<TAnalyzer> where TAnalyzer : DiagnosticAnalyzer, new()
{
    private readonly List<(string path, string content)> _sources = new();
    private readonly List<(string path, string content)> _additionalFiles = new();
    private readonly List<(string id, DiagnosticSeverity? severity)> _expected = new();
    private readonly List<(string id, string key, string value)> _expectedProps = new();
    private readonly List<(string id, string key)> _expectedAbsentProps = new();
    private bool _expectClean;
    private bool _forceMigration;

    /// <summary>
    /// Sets the global analyzer-config option that the analyze/validate driver uses (via --from-uwp)
    /// to force migration-only rules to fire regardless of source markers (B4).
    /// </summary>
    public AnalyzerTest<TAnalyzer> ForceMigration()
    {
        _forceMigration = true;
        return this;
    }

    public AnalyzerTest<TAnalyzer> WithSource(string source, string path = "Test0.cs")
    {
        _sources.Add((path, source));
        return this;
    }

    public AnalyzerTest<TAnalyzer> WithXaml(string path, string content)
    {
        _additionalFiles.Add((path, content));
        return this;
    }

    public AnalyzerTest<TAnalyzer> ExpectDiagnostic(string id, DiagnosticSeverity? severity = null)
    {
        _expected.Add((id, severity));
        return this;
    }

    /// <summary>Marker that this test should produce zero analyzer diagnostics.</summary>
    public AnalyzerTest<TAnalyzer> ExpectClean()
    {
        _expectClean = true;
        return this;
    }

    /// <summary>Assert that the diagnostic with <paramref name="id"/> carries a property.</summary>
    public AnalyzerTest<TAnalyzer> ExpectProperty(string id, string key, string value)
    {
        _expectedProps.Add((id, key, value));
        return this;
    }

    /// <summary>Assert that the diagnostic with <paramref name="id"/> does NOT carry a property key.</summary>
    public AnalyzerTest<TAnalyzer> ExpectPropertyAbsent(string id, string key)
    {
        _expectedAbsentProps.Add((id, key));
        return this;
    }

    public async Task RunAsync()
    {
        if (_sources.Count == 0)
        {
            // Always compile at least an empty unit so the analyzer can register.
            _sources.Add(("Empty.cs", "namespace _ { class _Empty {} }"));
        }

        var trees = _sources.Select(s => CSharpSyntaxTree.ParseText(
            SourceText.From(s.content, Encoding.UTF8),
            path: s.path)).ToImmutableArray();

        var references = GetMetadataReferences();

        var compilation = CSharpCompilation.Create(
            assemblyName: "Microsoft.WindowsAppSDK.Analyzers.Tests.Sample",
            syntaxTrees: trees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var additionalTexts = _additionalFiles
            .Select(f => (AdditionalText)new InMemoryAdditionalText(f.path, f.content))
            .ToImmutableArray();

        var analyzer = new TAnalyzer();
        var analyzerOptions = _forceMigration
            ? new AnalyzerOptions(additionalTexts, new ForceMigrationOptionsProvider())
            : new AnalyzerOptions(additionalTexts);
        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(analyzer),
            analyzerOptions);

        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None);

        // Filter to ours (analyzer-produced only).
        var actual = diagnostics
            .Where(d => analyzer.SupportedDiagnostics.Any(s => s.Id == d.Id))
            .ToList();

        if (_expectClean || _expected.Count == 0)
        {
            if (actual.Count != 0)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Expected no analyzer diagnostics but got {actual.Count}:{Environment.NewLine}" +
                    string.Join(Environment.NewLine, actual.Select(d => "  " + d.ToString())));
            }
            return;
        }

        // Match by ID with multiplicity. Verify expected severity if specified.
        var actualIds = actual.Select(d => d.Id).OrderBy(x => x).ToList();
        var expectedIds = _expected.Select(e => e.id).OrderBy(x => x).ToList();

        Assert.Equal(expectedIds, actualIds);

        foreach (var exp in _expected.Where(e => e.severity.HasValue))
        {
            var match = actual.FirstOrDefault(d => d.Id == exp.id);
            Assert.NotNull(match);
            Assert.Equal(exp.severity!.Value, match.Severity);
        }

        foreach (var ep in _expectedProps)
        {
            var match = actual.FirstOrDefault(d => d.Id == ep.id);
            Assert.NotNull(match);
            Assert.True(
                match!.Properties.TryGetValue(ep.key, out var v) && v == ep.value,
                $"Expected diagnostic {ep.id} to carry property {ep.key}={ep.value}, " +
                $"but got [{string.Join(", ", match.Properties.Select(p => $"{p.Key}={p.Value}"))}]");
        }

        foreach (var ep in _expectedAbsentProps)
        {
            var match = actual.FirstOrDefault(d => d.Id == ep.id);
            Assert.NotNull(match);
            Assert.False(
                match!.Properties.ContainsKey(ep.key),
                $"Expected diagnostic {ep.id} to NOT carry property {ep.key}, " +
                $"but got [{string.Join(", ", match.Properties.Select(p => $"{p.Key}={p.Value}"))}]");
        }
    }

    private static ImmutableArray<MetadataReference> GetMetadataReferences()
    {
        // Use the same trusted assemblies the test runtime uses.
        var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        return trustedAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;
        public InMemoryAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content, Encoding.UTF8);
        }
        public override string Path { get; }
        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }

    private sealed class ForceMigrationOptionsProvider : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new ForcedOptions();
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;

        private sealed class ForcedOptions : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                if (string.Equals(key, "build_property.WinUIMigrationFromUwp", StringComparison.Ordinal))
                {
                    value = "true";
                    return true;
                }
                value = null!;
                return false;
            }
        }
    }
}
