// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public class ReactorSourceEverywhereTests
{
    // CA1861: expectations read best inline; the params array is built per call on purpose.
    private static string[] Expected(params string[] values) => values;

    // Window 100: <App> root -> VStack -> [Component <Counter> -> HStack -> [TextBlock, Button key=inc],
    // 3 keyed TextBlocks created on one line]. Window 200: <SettingsPage> root -> VStack -> Component <Counter> -> ...
    private const string Described = """
        {"state":"ok","sourceMap":true,
         "nodes":[
           {"handle":"1","element":"VStack","file":0,"line":38,"column":16,"host":{"kind":"window","window":"100","title":"TaskBoard","root":{"kind":"class","name":"App","type":"App"},"mount":{"file":0,"line":7}}},
           {"handle":"2","parent":"1","element":"Component","file":0,"line":40,"component":{"kind":"class","name":"Counter","type":"Counter"}},
           {"handle":"3","parent":"2","element":"HStack","file":0,"line":73},
           {"handle":"4","parent":"3","element":"TextBlock","file":0,"line":74},
           {"handle":"5","parent":"3","element":"Button","key":"inc","file":0,"line":75,"column":13},
           {"handle":"6","parent":"1","element":"TextBlock","key":"t1","file":0,"line":47},
           {"handle":"7","parent":"1","element":"TextBlock","key":"t2","file":0,"line":47},
           {"handle":"8","element":"VStack","file":0,"line":86,"host":{"kind":"window","window":"200","title":"Settings","windowKey":"Settings","root":{"kind":"class","name":"SettingsPage"}}},
           {"handle":"9","parent":"8","element":"Component","file":0,"line":90,"component":{"kind":"class","name":"Counter","type":"Counter"}},
           {"handle":"10","parent":"9","element":"HStack","file":0,"line":73},
           {"handle":"11","parent":"10","element":"Button","key":"inc","file":0,"line":75,"column":13}
         ],
         "files":["C:\\src\\TaskBoard\\App.cs"],"unmapped":0,"truncated":false}
        """;

    private static ReactorDescription Reactor() => ReactorDescription.Parse(Described)!;

    private static string[] Match(string selector) =>
        [.. ReactorSelector.Parse(selector)!.Match(Reactor()).Select(node => node.Handle)];

    [TestMethod]
    public void Parse_RecognisesReactorSelectorsOnly()
    {
        Assert.IsNotNull(ReactorSelector.Parse("<Counter>"));
        Assert.IsNotNull(ReactorSelector.Parse("<Counter>/Button"));
        Assert.IsNotNull(ReactorSelector.Parse("key=inc"));
        Assert.IsNotNull(ReactorSelector.Parse("App.cs:75"));
        Assert.IsNotNull(ReactorSelector.Parse("App.cs:75:13"));
        Assert.IsNull(ReactorSelector.Parse("SubmitButton"), "an x:Name is not a Reactor selector");
        Assert.IsNull(ReactorSelector.Parse("button-submit-0123456789"), "a slug is not a Reactor selector");
        Assert.IsNull(ReactorSelector.Parse("12345"));
        Assert.IsNull(ReactorSelector.Parse("Button/<Counter>"), "a path starts with a component or a key");
        Assert.IsNull(ReactorSelector.Parse("<Counter>/not a kind"));
    }

    [TestMethod]
    public void Match_ComponentInEveryWindow_IsAmbiguous()
    {
        CollectionAssert.AreEquivalent(Expected("2", "9"), Match("<Counter>"));
        CollectionAssert.AreEqual(Expected("8"), Match("<SettingsPage>"));
    }

    [TestMethod]
    public void Match_PathNarrowsToElementsInsideAComponent()
    {
        CollectionAssert.AreEquivalent(Expected("5", "11"), Match("<Counter>/Button"));
        CollectionAssert.AreEqual(Expected("11"), Match("<SettingsPage>/<Counter>/Button"));
        CollectionAssert.AreEqual(Expected("4"), Match("<App>/<Counter>/textblock"), "element kinds match case-insensitively");
        CollectionAssert.AreEqual(Expected("11"), Match("<SettingsPage>/key=inc"));
    }

    [TestMethod]
    public void Match_KeyAndCallSite()
    {
        CollectionAssert.AreEqual(Expected("6"), Match("key=t1"));
        CollectionAssert.AreEquivalent(Expected("6", "7"), Match("App.cs:47"), "every element created on one line matches it");
        CollectionAssert.AreEquivalent(Expected("5", "11"), Match("app.CS:75:13"));
        Assert.AreEqual(0, Match("App.cs:75:14").Length);
        Assert.AreEqual(0, Match("Other.cs:75").Length);
    }

    [TestMethod]
    public void Candidate_PrintsTheSlugLabelKeySiteAndWindow()
    {
        var reactor = Reactor();
        var live = new VisualTreeNode("5", "", "Microsoft.UI.Xaml.Controls.Button", null, "0123456789", false, 0, [], 0);
        var candidate = DevToolsSelector.ReactorCandidate(reactor, reactor.ByHandle["11"], (live, 4));

        Assert.AreEqual("button-0123456789", candidate.Selector, "candidates print a slug, not a raw handle");
        Assert.AreEqual("Button", candidate.Label);
        StringAssert.Contains(candidate.Detail, "key=inc");
        StringAssert.Contains(candidate.Detail, "App.cs:75");
        StringAssert.Contains(candidate.Detail, "window \"Settings\" (-w 200)");
        Assert.AreEqual("200", candidate.Window);
        Assert.AreEqual("<Counter>", DevToolsSelector.ReactorCandidate(reactor, reactor.ByHandle["2"], null).Label);
    }

    [TestMethod]
    public void Parse_ReadsColumnsAndMountSites()
    {
        var reactor = Reactor();
        Assert.AreEqual(13, reactor.ByHandle["5"].Column);
        Assert.AreEqual("App.cs:7", reactor.ByHandle["1"].Host!.ShortMount);
        Assert.IsNull(reactor.ByHandle["8"].Host!.ShortMount);
    }

    [TestMethod]
    public void RootLine_ShowsWhereTheHostWasMounted()
    {
        var annotations = new DevToolsTreeAnnotations(Reactor(), [], raw: false);
        var root = new VisualTreeNode("1", "", "Microsoft.UI.Xaml.Controls.StackPanel", null, null, false, 0, [], 0);
        StringAssert.Contains(annotations.RootLine(root), "App.cs:7");
    }

    [TestMethod]
    public void ElementInfo_ReadsOwnedPropertiesAndComponentProps()
    {
        var info = ReactorElementInfo.Parse("""
            {"state":"ok","tagged":true,"element":"Button","key":"inc","file":"C:\\src\\App.cs","line":75,"column":13,
             "owner":"Counter",
             "props":[{"name":"Label","property":"Content","value":"+1"}],
             "modifierSource":"reactor",
             "modifiers":[{"modifier":"Width","property":"Width","value":"120"}]}
            """)!;

        Assert.AreEqual("element", info.OwnerOf("content")!.From);
        Assert.AreEqual("modifier", info.OwnerOf("Width")!.From);
        Assert.IsNull(info.OwnerOf("Background"));
        Assert.AreEqual("App.cs:75", info.ShortSource);
        var warning = info.OwnedWarning("Width");
        StringAssert.Contains(warning, "set in C# (App.cs:75)");
        StringAssert.Contains(warning, "<Counter> re-renders with a changed value");

        var component = ReactorElementInfo.Parse("""
            {"state":"ok","tagged":true,"element":"Component","component":{"kind":"class","name":"Login"},"props":[],"modifiers":[],
             "componentProps":{"type":"LoginProps","values":[{"name":"User","value":"ada"},{"name":"UserPassword","value":"<redacted>","redacted":true}]}}
            """)!;
        Assert.AreEqual("LoginProps", component.ComponentPropsType);
        Assert.IsTrue(component.ComponentProps!.Single(prop => prop.Name == "UserPassword").Redacted);

        Assert.IsNull(ReactorElementInfo.Parse("""{"state":"ok","tagged":false,"props":[]}"""), "an untagged control has no Reactor facts");
        Assert.IsNull(ReactorElementInfo.Parse("""{"state":"absent"}"""));
    }

    [TestMethod]
    public void BuildFacts_WarnAboutMissingSourceMapAndTheDeprecatedPackage()
    {
        var assets = Path.Combine(Path.GetTempPath(), $"winapp-reactor-assets-{Guid.NewGuid():N}.json");
        File.WriteAllText(assets, """{"libraries":{"Microsoft.UI.Reactor/0.1.0":{},"Microsoft.UI.Reactor.Devtools/0.1.0":{}}}""");
        try
        {
            var off = ReactorBuildFacts.From(new Dictionary<string, string> { ["ReactorSourceMap"] = "false" }, assets)!;
            Assert.IsTrue(off.Referenced && off.DevtoolsPackage);
            var warnings = off.Warnings().ToArray();
            Assert.AreEqual(2, warnings.Length);
            StringAssert.Contains(warnings[0], "<ReactorSourceMap>false</ReactorSourceMap>");
            StringAssert.Contains(warnings[1], "deprecated");

            var release = ReactorBuildFacts.From(new Dictionary<string, string> { ["_MicrosoftUIReactorTargetsLoaded"] = "true" }, null)!;
            StringAssert.Contains(release.Warnings().Single(), "only in Debug");

            var debug = ReactorBuildFacts.From(new Dictionary<string, string>
            {
                ["_MicrosoftUIReactorTargetsLoaded"] = "true",
                ["ReactorSourceMap"] = "true",
                ["InterceptorsNamespaces"] = ";Microsoft.UI.Reactor.Generated",
                ["PathMap"] = @"C:\src\=/_/",
            }, null)!;
            Assert.AreEqual(0, debug.Warnings().Count());
            var environment = ReactorBuildFacts.ApplyEnvironment(new Dictionary<string, string?>(), debug);
            Assert.AreEqual(@"C:\src\=/_/", environment[ReactorBuildFacts.PathMapVariable]);

            Assert.IsNull(ReactorBuildFacts.From(new Dictionary<string, string>(), null), "a project without Reactor has no Reactor facts");
        }
        finally
        {
            File.Delete(assets);
        }
    }

    [TestMethod]
    public void CommentCapture_DoesNotAnchorToACSharpCallSite()
    {
        var (uri, file, line, column) = CommentSelectionCapture.ParseSource(
            """{"handle":5,"kind":"csharp","fileName":"C:\\src\\App.cs","lineNumber":75,"columnNumber":13,"authoredState":"available"}""");
        Assert.IsNull(uri);
        Assert.IsNull(file);
        Assert.IsNull(line);
        Assert.IsNull(column);
    }

    [TestMethod]
    public void SandboxSnapshot_AdmitsAReactorProjectWithoutXaml()
    {
        var directory = Directory.CreateTempSubdirectory("winapp-reactor-sandbox-");
        try
        {
            var project = new FileInfo(Path.Combine(directory.FullName, "App.csproj"));
            File.WriteAllText(project.FullName, "<Project />");
            Assert.ThrowsExactly<InvalidOperationException>(() => GuestSourceSnapshot.ValidateSources(project, []),
                "a XAML project without sources is still refused");
            Assert.AreEqual(0, GuestSourceSnapshot.ValidateSources(project, [], allowEmpty: true).Count);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
