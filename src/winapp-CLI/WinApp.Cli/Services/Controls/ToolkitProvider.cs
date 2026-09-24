// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text.RegularExpressions;

/// <summary>
/// Windows Community Toolkit scenarios (<c>CommunityToolkit/Windows</c>). Where the
/// scenarios come from lives in <see cref="ToolkitFetcher"/>; making them pasteable lives
/// here, in <see cref="NormalizeForPaste"/>. Toolkit cleans its tag dictionary on write,
/// so it re-cleans on cache read too.
/// </summary>
internal sealed partial class ToolkitProvider : CachedProviderBase
{
    public ToolkitProvider(string cacheRoot) : base(cacheRoot) { }

    public override string Id => "toolkit";
    public override string DisplayName => "CommunityToolkit";

    protected override Dictionary<string, string[]> NormalizeTagsOnRead(
        Dictionary<string, string[]> tags) => StopWords.CleanTagDictionary(tags);

    protected override async Task<ProviderData> FetchAsync(CancellationToken cancellationToken)
    {
        var (scenarios, tags, keywords) = await ToolkitFetcher.FetchAsync(cancellationToken);

        foreach (var scenario in scenarios)
        {
            NormalizeForPaste(scenario);
        }

        return scenarios.Length > 0
            ? new ProviderData(scenarios, tags, keywords)
            : ProviderData.Empty;
    }

    /// <summary>The Toolkit's own license header, in the three shapes its sample files use.</summary>
    [GeneratedRegex(@"^//\s*Licensed to.*?(?=\n[^/])", RegexOptions.Singleline)]
    private static partial Regex LicenseBlockRegex();

    [GeneratedRegex(@"^//\s*The \.NET Foundation.*?\n", RegexOptions.Multiline)]
    private static partial Regex LicenseFoundationRegex();

    [GeneratedRegex(@"^//\s*See the LICENSE.*?\n", RegexOptions.Multiline)]
    private static partial Regex LicenseSeeAlsoRegex();

    /// <summary>The sample's own class declaration, whatever it derives from. The Toolkit
    /// names the class after the file, so this is the identifier the rest of the sample uses
    /// to refer to itself — and the one a user pasting the sample has to rename.</summary>
    [GeneratedRegex(@"\b(?:public\s+|internal\s+)?(?:sealed\s+)?partial\s+class\s+(\w+)")]
    private static partial Regex SampleClassRegex();

    /// <summary>The attributes the Toolkit's docs source generator reads to build the sample
    /// browser and its options pane. They are build-time metadata for an app the user is not
    /// building, and they reference a generator package a pasted sample does not reference.</summary>
    [GeneratedRegex(@"\[Toolkit(?:Sample|SampleOptionsPane|SampleMultiChoiceOption|SampleNumericOption|SampleBoolOption|SampleTextOption)\b[^\]]*\][\r\n]*", RegexOptions.Singleline)]
    private static partial Regex ToolkitSampleAttributeRegex();

    [GeneratedRegex(@"\[SuppressMessage[^\]]*\][\r\n]*", RegexOptions.Singleline)]
    private static partial Regex SuppressMessageAttributeRegex();

    /// <summary>A <c>namespace</c> declaration putting the sample in the Toolkit's own
    /// namespace, in either block or file-scoped form.</summary>
    [GeneratedRegex(@"namespace\s+[\w.]+\s*(?:;|\{)\s*")]
    private static partial Regex ToolkitNamespaceRegex();

    /// <summary>The docs-only <c>ConvertStringTo…</c> helpers that back the option-pane
    /// attributes stripped above: a single expression-bodied static returning a switch
    /// expression. With the attributes gone nothing calls them.</summary>
    [GeneratedRegex(@"public\s+static\s+[\w?<>]+\s+ConvertString\w+\s*\([^)]*\)\s*=>\s*\w+\s+switch\s*\{[^}]*\}\s*;", RegexOptions.Singleline)]
    private static partial Regex OptionConverterHelperRegex();

    /// <summary>Three or more consecutive newlines, left behind by the removals above.</summary>
    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex BlankLineRunRegex();

    /// <summary>
    /// Make one Toolkit scenario pasteable.
    ///
    /// <para>A Toolkit sample is source from an app the user is not building: it declares
    /// itself in the Toolkit's namespace, names itself after a sample class, carries the
    /// <c>[ToolkitSample…]</c> attributes its docs generator consumes and the
    /// <c>ConvertStringTo…</c> helpers that back them, and multi-targets UWP and Uno behind
    /// <c>#if</c>. Pasted as-is into a WinAppSDK app, that does not compile.</para>
    ///
    /// <para>What is rewritten here is environment, never substance: identifiers, build-time
    /// metadata, and branches for platforms the user is not on. The sample still teaches
    /// exactly what upstream wrote it to teach.</para>
    ///
    /// <para><see cref="GalleryProvider.NormalizeForPaste"/> is the same job for Gallery.
    /// Both run on the provider rather than in fetching, because the source being normalized
    /// is upstream's verbatim text either way — which is what lets the Toolkit scraper be
    /// replaced by a published index (<see href="https://github.com/microsoft/winappCli/issues/810">#810</see>)
    /// without this work having to move again.</para>
    ///
    /// <para>Internal for direct testing: the corpus guards in <c>EmbeddedSnapshotTests</c>
    /// only prove the baked data is clean, which passes vacuously if upstream stops shipping
    /// the thing being rewritten.</para>
    /// </summary>
    internal static void NormalizeForPaste(Scenario scenario)
    {
        if (!string.IsNullOrEmpty(scenario.CSharp))
        {
            var csharp = scenario.CSharp;

            // Captured before anything is removed, so folding a branch or stripping an
            // attribute can't take the declaration this name is read from with it.
            var sampleClass = SampleClassRegex().Match(csharp) is { Success: true } m
                ? m.Groups[1].Value
                : null;

            csharp = LicenseBlockRegex().Replace(csharp, "");
            csharp = LicenseFoundationRegex().Replace(csharp, "");
            csharp = LicenseSeeAlsoRegex().Replace(csharp, "");

            // Fold platform #if/#else/#endif before the rest of the cleanup, so the removals
            // below don't spend work on text that is about to be discarded.
            csharp = FoldPreprocessorDirectives(csharp);

            csharp = ToolkitSampleAttributeRegex().Replace(csharp, "");
            csharp = SuppressMessageAttributeRegex().Replace(csharp, "");
            csharp = ToolkitNamespaceRegex().Replace(csharp, "namespace YourApp;\n\n");

            if (!string.IsNullOrEmpty(sampleClass))
            {
                csharp = Regex.Replace(csharp, $@"\b{Regex.Escape(sampleClass)}\b", "YourPage");
            }

            csharp = OptionConverterHelperRegex().Replace(csharp, "");
            csharp = BlankLineRunRegex().Replace(csharp, "\n\n");

            scenario.CSharp = string.IsNullOrWhiteSpace(csharp) ? null : csharp.Trim();
        }

        if (!string.IsNullOrEmpty(scenario.Xaml))
        {
            // Strip handlers against the C# settled above, so an attribute survives only
            // when the code we actually serve declares its method. Scenarios split out of a
            // multi-instance sample carry no code-behind at all, so for those this drops
            // every bare-method handler — which is correct: none of them are backed.
            scenario.Xaml = ControlSnippetText.StripUnbackedEventHandlers(scenario.Xaml!, scenario.CSharp);
        }
    }

    /// <summary>
    /// Compile-time preprocessor folding for toolkit samples. Agents target WinAppSDK,
    /// so we evaluate <c>#if WINAPPSDK</c> as true (and <c>HAS_UNO</c> / <c>WINUI2</c> /
    /// <c>UWP</c> / <c>NETFX_CORE</c> as false), keep the live branch's lines, and drop
    /// the directives + dead branches. Unknown symbols are treated as true (conservative:
    /// keep code rather than silently delete it). Supports <c>#if</c>, <c>#elif</c>,
    /// <c>#else</c>, <c>#endif</c>, single <c>!</c> negation, and nested blocks.
    /// </summary>
    internal static string FoldPreprocessorDirectives(string cs)
    {
        if (cs.IndexOf("#if", StringComparison.Ordinal) < 0) return cs;

        // Treat WinAppSDK-targeting symbols as true; UWP/Uno/legacy as false.
        // Anything else: true (preserve code we don't recognize).
        static bool Eval(string expr)
        {
            expr = expr.Trim();
            bool negate = false;
            if (expr.StartsWith('!'))
            {
                negate = true;
                expr = expr[1..].Trim();
            }
            bool value = expr switch
            {
                "WINAPPSDK" or "WINUI3" or "NET" => true,
                "HAS_UNO" or "WINUI2" or "UWP" or "NETFX_CORE" => false,
                _ => true,
            };
            return negate ? !value : value;
        }

        var lines = cs.Replace("\r\n", "\n").Split('\n');
        var output = new List<string>(lines.Length);
        // Stack frame: (anyBranchTakenYet, currentlyEmittingThisBranch).
        // Parent's emitting state is tracked separately via parentEmit.
        var stack = new Stack<(bool taken, bool emit)>();

        bool ParentEmitting()
        {
            foreach (var f in stack)
                if (!f.emit) return false;
            return true;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("#if ", StringComparison.Ordinal) || trimmed == "#if")
            {
                var expr = trimmed.Length > 3 ? trimmed[3..].Trim() : "";
                bool parentEmit = ParentEmitting();
                bool take = parentEmit && Eval(expr);
                stack.Push((take, take));
                continue;
            }
            if (trimmed.StartsWith("#elif ", StringComparison.Ordinal))
            {
                if (stack.Count == 0) continue; // malformed, drop
                var (taken, _) = stack.Pop();
                var expr = trimmed[5..].Trim();
                bool parentEmit = ParentEmitting();
                bool take = parentEmit && !taken && Eval(expr);
                stack.Push((taken || take, take));
                continue;
            }
            if (trimmed.StartsWith("#else", StringComparison.Ordinal))
            {
                if (stack.Count == 0) continue;
                var (taken, _) = stack.Pop();
                bool parentEmit = ParentEmitting();
                bool take = parentEmit && !taken;
                stack.Push((true, take));
                continue;
            }
            if (trimmed.StartsWith("#endif", StringComparison.Ordinal))
            {
                if (stack.Count > 0) stack.Pop();
                continue;
            }

            if (ParentEmitting()) output.Add(line);
        }

        return string.Join('\n', output);
    }
}
