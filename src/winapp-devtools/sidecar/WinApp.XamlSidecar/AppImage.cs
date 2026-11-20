// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace WinApp.XamlSidecar;

/// <summary>
/// A read-only metadata image of the app's current code, for the XAML compiler's pass 2 (x:Bind paths and event handlers
/// checked against members just added). Built with Roslyn's public API from the compiler arguments the build recorded;
/// no hot reload API is involved: dotnet watch applies the code.
/// </summary>
internal sealed class AppImage
{
    private readonly string _projectDirectory;
    private readonly CSharpCommandLineArguments _arguments;
    private readonly List<MetadataReference> _references;
    private readonly Version? _version;
    private readonly AnalyzerConfigSet _configs;
    private GeneratorDriver _driver;
    private readonly Dictionary<string, (DateTime Stamp, SyntaxTree Tree)> _trees = new(StringComparer.OrdinalIgnoreCase);

    private AppImage(string projectDirectory, CSharpCommandLineArguments arguments, Version? version)
    {
        _projectDirectory = projectDirectory;
        _arguments = arguments;
        _version = version;
        _references = arguments.MetadataReferences
            .Select(r => (MetadataReference)MetadataReference.CreateFromFile(Path.GetFullPath(Path.Combine(projectDirectory, r.Reference)), r.Properties))
            .ToList();
        _configs = AnalyzerConfigSet.Create(arguments.AnalyzerConfigPaths.Where(File.Exists)
            .Select(p => AnalyzerConfig.Parse(SourceText.From(File.ReadAllText(p)), p)).ToImmutableArray());
        var generators = arguments.AnalyzerReferences
            .Select(r => Path.GetFullPath(Path.Combine(projectDirectory, r.FilePath)))
            .Where(File.Exists)
            .SelectMany(path =>
            {
                try
                {
                    return new AnalyzerFileReference(path, Loader.Instance).GetGenerators(LanguageNames.CSharp);
                }
                catch (Exception)
                {
                    return [];
                }
            })
            .ToImmutableArray();
        var additional = arguments.AdditionalFiles.Where(f => File.Exists(f.Path))
            .Select(f => (AdditionalText)new FileText(f.Path)).ToImmutableArray();
        _driver = CSharpGeneratorDriver.Create(generators, additional, arguments.ParseOptions, new Options(_configs));
    }

    public static AppImage Load(string rspPath, string projectDirectory, Version? version)
    {
        var args = File.ReadAllLines(rspPath).Where(l => l.Length > 0).ToArray();
        var parsed = CSharpCommandLineParser.Default.Parse(args, projectDirectory, sdkDirectory: null);
        return new AppImage(projectDirectory, parsed, version);
    }

    /// <summary>The app's metadata (private members included) with <paramref name="overrides"/> (path to text) in place of the files on disk.</summary>
    public byte[] Emit(IReadOnlyDictionary<string, string> overrides, out string? error)
    {
        var paths = _arguments.SourceFiles.Select(f => Path.GetFullPath(Path.Combine(_projectDirectory, f.Path))).ToList();
        // C# files created since the build (a new page's code-behind) and the generated parts of new pages.
        foreach (var file in Directory.EnumerateFiles(_projectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (!file.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) && !file.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) &&
                !paths.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(file);
            }
        }
        paths.AddRange(overrides.Keys.Where(k => !paths.Contains(k, StringComparer.OrdinalIgnoreCase)));
        var trees = new List<SyntaxTree>();
        foreach (var path in paths)
        {
            if (overrides.TryGetValue(path, out var text))
            {
                trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(text, System.Text.Encoding.UTF8), _arguments.ParseOptions, path));
                continue;
            }
            if (!File.Exists(path))
            {
                continue;
            }
            var stamp = File.GetLastWriteTimeUtc(path);
            if (!_trees.TryGetValue(path, out var cached) || cached.Stamp != stamp)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                cached = (stamp, CSharpSyntaxTree.ParseText(SourceText.From(stream), _arguments.ParseOptions, path));
                _trees[path] = cached;
            }
            trees.Add(cached.Tree);
        }
        Compilation compilation = CSharpCompilation.Create(_arguments.CompilationName, trees, _references, _arguments.CompilationOptions);
        _driver = _driver.RunGeneratorsAndUpdateCompilation(compilation, out compilation, out _);
        if (_version is { } version && compilation.Assembly.Identity.Version != version)
        {
            compilation = WithAssemblyVersion(compilation, version);
        }
        using var image = new MemoryStream();
        var result = compilation.Emit(image, options: new EmitOptions(metadataOnly: true, includePrivateMembers: true));
        if (!result.Success)
        {
            error = string.Join(Environment.NewLine + "  ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(3).Select(Format));
            return [];
        }
        error = null;
        return image.ToArray();
    }

    private string Format(Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        var file = span.Path is { Length: > 0 } p ? Path.GetRelativePath(_projectDirectory, p) : "";
        return $"{file}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): error {d.Id}: {d.GetMessage()}";
    }

    /// <summary>The build's pass 2 read an app assembly with this version (often 0.0.0.0); XamlTypeInfo's helper names hash it.</summary>
    private static Compilation WithAssemblyVersion(Compilation compilation, Version version)
    {
        static bool IsVersion(AttributeSyntax a) => a.Name.ToString().Split('.')[^1] is "AssemblyVersion" or "AssemblyVersionAttribute";
        foreach (var tree in compilation.SyntaxTrees.ToList())
        {
            var root = tree.GetRoot();
            var lists = root.DescendantNodes().OfType<AttributeListSyntax>()
                .Where(l => l.Target?.Identifier.IsKind(SyntaxKind.AssemblyKeyword) == true && l.Attributes.Any(IsVersion)).ToList();
            if (lists.Count == 0)
            {
                continue;
            }
            root = root.ReplaceNodes(lists, (list, _) => list.WithAttributes(SyntaxFactory.SeparatedList(list.Attributes.Where(a => !IsVersion(a)))));
            root = root.RemoveNodes(root.DescendantNodes().OfType<AttributeListSyntax>().Where(l => l.Attributes.Count == 0), SyntaxRemoveOptions.KeepNoTrivia)!;
            compilation = compilation.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(root, tree.Options));
        }
        return version == new Version(0, 0, 0, 0) ? compilation : compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            $"[assembly: System.Reflection.AssemblyVersion(\"{version}\")]", (CSharpParseOptions)compilation.SyntaxTrees.First().Options));
    }

    private sealed class FileText(string path) : AdditionalText
    {
        public override string Path => path;

        public override SourceText? GetText(CancellationToken cancellationToken = default) => SourceText.From(File.ReadAllText(path));
    }

    private sealed class Options(AnalyzerConfigSet configs) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Values(configs.GlobalConfigOptions.AnalyzerOptions);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Values(configs.GetOptionsForSourcePath(tree.FilePath).AnalyzerOptions, configs.GlobalConfigOptions.AnalyzerOptions);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Values(configs.GetOptionsForSourcePath(textFile.Path).AnalyzerOptions, configs.GlobalConfigOptions.AnalyzerOptions);
    }

    private sealed class Values(ImmutableDictionary<string, string> values, ImmutableDictionary<string, string>? fallback = null) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) =>
            values.TryGetValue(key, out value!) || (fallback is not null && fallback.TryGetValue(key, out value!));
    }

    /// <summary>Loads each analyzer folder in a context of its own; Roslyn itself comes from the default context.</summary>
    private sealed class Loader : IAnalyzerAssemblyLoader
    {
        public static readonly Loader Instance = new();
        private readonly Dictionary<string, AssemblyLoadContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

        public void AddDependencyLocation(string fullPath)
        {
        }

        public Assembly LoadFromPath(string fullPath)
        {
            var directory = Path.GetDirectoryName(fullPath)!;
            lock (_contexts)
            {
                if (!_contexts.TryGetValue(directory, out var context))
                {
                    context = new FolderContext(directory);
                    _contexts[directory] = context;
                }
                return context.LoadFromAssemblyPath(fullPath);
            }
        }

        private sealed class FolderContext(string directory) : AssemblyLoadContext(directory)
        {
            protected override Assembly? Load(AssemblyName name)
            {
                if (name.Name is { } simple && (simple.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) || simple.StartsWith("System.", StringComparison.Ordinal)))
                {
                    return null;
                }
                var path = Path.Combine(directory, name.Name + ".dll");
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }
        }
    }
}
