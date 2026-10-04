// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

/// <summary>
/// Gates for the two things a real <c>winapp devtools</c> session got wrong before this:
/// <list type="number">
/// <item>
/// <b>The default view showed the framework, not the app.</b> A WinUI control template inserts
/// Border/ContentPresenter/Grid/ScrollViewer levels, so an app's own Button sits at raw depth 12-14 and a
/// default <c>--depth 4</c> listing showed nothing a developer wrote. Filtering could not fix it: the filter
/// ran AFTER a depth-limited enumerate, so the authored descendants were never fetched to be kept. The fix is
/// the ORDER — the tap projects, then bounds — and that is what these assert, by checking which request the
/// command actually sends.
/// </item>
/// <item>
/// <b>Selectors were opaque decimals.</b> Now they are the same vocabulary <c>winapp ui</c> uses: a
/// census-unique <c>x:Name</c>, else a <c>type-name-hash</c> slug validated against the live identity. The
/// validation is the point — a slug that no longer names the element it named must be REFUSED, not resolved
/// to the similar-looking element still there.
/// </item>
/// </list>
/// </summary>
[TestClass]
[DoNotParallelize]
public class DevToolsSelectorTests
{
    /// <summary>
    /// The authored projection the tap returns for <c>authored:true</c>: the app's Grid with its Button as a
    /// DIRECT child, even though the raw tree has four template levels between them. The raw reply below has
    /// the same two elements at their real depths, so a test can tell the two views apart by shape.
    /// </summary>
    private const string AuthoredTree =
        """
        [{"handle":"10","name":"Root","type":"Microsoft.UI.Xaml.Controls.Grid","file":"ms-appx:///MainWindow.xaml",
          "id":"aaaaaaaaaa","uniqueName":true,"childCount":1,"children":[
            {"handle":"42","name":"SubmitButton","type":"Microsoft.UI.Xaml.Controls.Button",
             "file":"ms-appx:///MainWindow.xaml","id":"bbbbbbbbbb","uniqueName":true,"childCount":0,"children":[]}]}]
        """;

    /// <summary>The raw tree: the same Button, buried under template parts, and past a default depth of 4.</summary>
    private const string RawTree =
        """
        [{"handle":"10","name":"Root","type":"Microsoft.UI.Xaml.Controls.Grid","id":"aaaaaaaaaa","uniqueName":true,
          "childCount":1,"children":[
            {"handle":"11","type":"Microsoft.UI.Xaml.Controls.Border","id":"cccccccccc","childCount":1,"children":[
              {"handle":"12","type":"Microsoft.UI.Xaml.Controls.ContentPresenter","id":"dddddddddd","childCount":1,"children":[
                {"handle":"13","type":"Microsoft.UI.Xaml.Controls.ScrollViewer","id":"eeeeeeeeee","childCount":1,"children":[]}]}]}]}]
        """;

    private const string SubmitButtonMatch =
        """
        {"matches":[{"handle":"42","name":"SubmitButton","type":"Microsoft.UI.Xaml.Controls.Button",
          "file":"ms-appx:///MainWindow.xaml","id":"bbbbbbbbbb","uniqueName":true,"depth":2}],
         "query":"q","appAuthoredOnly":false,"searchedNodes":2,"censusNodes":2,"truncated":false}
        """;

    /// <summary>Two rows of one DataTemplate: the SAME x:Name, so neither can be promoted to a bare name.</summary>
    private const string DuplicateNameMatches =
        """
        {"matches":[
          {"handle":"51","name":"DeleteButton","type":"Microsoft.UI.Xaml.Controls.Button","id":"1111111111","depth":6},
          {"handle":"52","name":"DeleteButton","type":"Microsoft.UI.Xaml.Controls.Button","id":"2222222222","depth":6}],
         "query":"button","appAuthoredOnly":false,"searchedNodes":2,"censusNodes":2,"truncated":false}
        """;

    private static bool IsAuthoredRequest(string request) => request.Contains("\"authored\":true", StringComparison.Ordinal);

    /// <summary>
    /// The authored reply shape: nodes plus the state of the classifier pass that produced them. Built here
    /// so a test states the classification facts and the tree it is talking about in ONE place — which is the
    /// property the envelope exists to guarantee.
    /// </summary>
    private static string AuthoredEnvelope(
        string nodes,
        bool sourceInstrumented = true,
        bool classificationTruncated = false,
        int classifiedNodes = 800,
        int censusNodes = 800) =>
        $$"""
        {"nodes":{{nodes}},"authored":true,"sourceInstrumented":{{(sourceInstrumented ? "true" : "false")}},
         "classificationTruncated":{{(classificationTruncated ? "true" : "false")}},
         "classifiedNodes":{{classifiedNodes}},"censusNodes":{{censusNodes}},"authoredNodes":2,
         "classifier":"allowlist"}
        """;

    private static string Find(string matches) =>
        $$"""
        {"matches":{{matches}},"query":"q","appAuthoredOnly":false,"searchedNodes":2,"censusNodes":2,"truncated":false}
        """;

    // ---- 1. authored-first inspect ------------------------------------------------------------------------

    /// <summary>
    /// The default asks the TAP to project, rather than fetching a raw tree and filtering it here. That
    /// ordering is the entire fix: a client-side filter over a depth-4 raw reply can only remove elements the
    /// depth already fetched, and it never fetched the app's Button.
    /// </summary>
    [TestMethod]
    public async Task Inspect_DefaultsToTheAuthoredProjection_AndAsksTheTapToProject()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredEnvelope(AuthoredTree), IsAuthoredRequest)
            .Answer("VisualTree.enumerate", RawTree);

        var (exit, output) = await RunAsync(new DevToolsInspectCommand(), agent, []);

        Assert.AreEqual(0, exit);
        Assert.IsTrue(agent.ReceivedRequests.Any(IsAuthoredRequest),
            "The default view must ask the tap for the authored projection, not filter a raw tree afterwards.");
        StringAssert.Contains(output, "SubmitButton",
            "An app-authored Button must be visible at the DEFAULT depth, which is the bug this fixes.");
        StringAssert.Contains(output, "your XAML", "The footer must say which view this is.");
    }

    /// <summary>
    /// <c>--depth</c> counts AUTHORED levels. The proof is that the authored reply's Button is a direct child
    /// of the Grid while the raw tree puts four template levels between them: rendering the authored reply at
    /// one indent level is only possible because the projection reparented it.
    /// </summary>
    [TestMethod]
    public async Task Inspect_AuthoredDepthCountsAuthoredLevels_NotTemplateLevels()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredEnvelope(AuthoredTree), IsAuthoredRequest)
            .Answer("VisualTree.enumerate", RawTree);

        var (_, authored) = await RunAsync(new DevToolsInspectCommand(), agent, ["--depth", "2", "--json"]);
        using var doc = JsonDocument.Parse(authored);
        var root = doc.RootElement.GetProperty("elements")[0];

        Assert.AreEqual("Root", root.GetProperty("name").GetString());
        Assert.AreEqual(
            "SubmitButton",
            root.GetProperty("children")[0].GetProperty("name").GetString(),
            "In the authored view the app's Button is a DIRECT child; the template levels are skipped, not counted.");
        Assert.AreEqual(1, root.GetProperty("childCount").GetInt32(),
            "childCount describes AUTHORED children in this view, so '+more' cannot promise template parts.");
    }

    /// <summary>--all is the opt-out, and it must reach the tap as a plain raw enumerate.</summary>
    [TestMethod]
    public async Task Inspect_AllShowsTheRawFrameworkTree()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredEnvelope(AuthoredTree), IsAuthoredRequest)
            .Answer("VisualTree.enumerate", RawTree);

        var (exit, output) = await RunAsync(new DevToolsInspectCommand(), agent, ["--all"]);

        Assert.AreEqual(0, exit);
        Assert.IsFalse(agent.ReceivedRequests.Any(IsAuthoredRequest),
            "--all must not ask for a projection; raw depth semantics are the whole point of the flag.");
        StringAssert.Contains(output, "ContentPresenter", "--all exposes the control-template internals.");
        StringAssert.Contains(output, "all elements");
    }

    /// <summary>
    /// An app that was already running when we attached carries no XAML source information, so nothing can be
    /// classified. That is a fact about the LAUNCH, not the markup: the command must fall back to the raw tree
    /// AND say why, because an empty result reads as "your app has no elements" and sends someone to debug
    /// XAML that is fine.
    /// </summary>
    [TestMethod]
    public async Task Inspect_UninstrumentedApp_FallsBackToTheRawTreeAndSaysWhy()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredEnvelope("[]", sourceInstrumented: false), IsAuthoredRequest)
            .Answer("VisualTree.enumerate", RawTree);

        var (exit, output) = await RunAsync(new DevToolsInspectCommand(), agent, []);

        Assert.AreEqual(0, exit, "A fallback is still a successful read of the tree.");
        StringAssert.Contains(output, "no XAML source information");
        StringAssert.Contains(output, "winapp run --devtools", "The message must say how to enable the authored view.");
        StringAssert.Contains(output, "ContentPresenter", "The raw tree is shown rather than nothing.");
    }

    /// <summary>The fallback has to be machine-readable too, or a --json caller silently believes it saw the app's XAML.</summary>
    [TestMethod]
    public async Task Inspect_FallbackIsExplicitInJson()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredEnvelope("[]", sourceInstrumented: false), IsAuthoredRequest)
            .Answer("VisualTree.enumerate", RawTree);

        var (_, output) = await RunAsync(new DevToolsInspectCommand(), agent, ["--json"]);

        using var doc = JsonDocument.Parse(output);
        Assert.IsTrue(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.IsFalse(doc.RootElement.GetProperty("appAuthored").GetBoolean(),
            "The payload must not claim an authored view it did not get.");
        Assert.IsTrue(doc.RootElement.GetProperty("fallback").GetBoolean());
        StringAssert.Contains(
            doc.RootElement.GetProperty("fallbackReason").GetString(),
            "no XAML source information");
    }

    /// <summary>
    /// A protocol failure that is NOT "nothing to classify" must surface as a failure. Retrying every error
    /// against the raw tree would turn a broken agent into a silently degraded one.
    /// </summary>
    [TestMethod]
    public async Task Inspect_ARealProtocolFailure_IsNotPaperedOverByTheFallback()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Error("VisualTree.enumerate", -32011, "no-tree", "no live visual tree");

        var (exit, output) = await RunAsync(new DevToolsInspectCommand(), agent, []);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "Error:");
        Assert.AreEqual(1, agent.ReceivedRequests.Count(r => r.Contains("VisualTree.enumerate", StringComparison.Ordinal)),
            "A real failure must not trigger a second, quieter attempt.");
    }

    // ---- 2. selectors -------------------------------------------------------------------------------------

    /// <summary>A census-unique x:Name is promoted whole, exactly as `winapp ui` promotes a unique AutomationId.</summary>
    [TestMethod]
    public void Display_PromotesAGloballyUniqueName()
    {
        Assert.AreEqual(
            "SubmitButton",
            DevToolsSelector.Display("42", "Microsoft.UI.Xaml.Controls.Button", "SubmitButton", "bbbbbbbbbb", uniqueName: true));
    }

    /// <summary>
    /// A DUPLICATE name cannot be promoted: two DataTemplate rows would print the same selector and neither
    /// would resolve. They fall to slugs, and the slugs differ because identity is a fold over the PATH.
    /// </summary>
    [TestMethod]
    public void Display_DuplicateNamesGetDistinctSlugs()
    {
        var first = DevToolsSelector.Display("51", "Microsoft.UI.Xaml.Controls.Button", "DeleteButton", "1111111111", uniqueName: false);
        var second = DevToolsSelector.Display("52", "Microsoft.UI.Xaml.Controls.Button", "DeleteButton", "2222222222", uniqueName: false);

        Assert.AreEqual("button-deletebutton-1111111111", first);
        Assert.AreEqual("button-deletebutton-2222222222", second);
        Assert.AreNotEqual(first, second, "Two same-named elements must be individually addressable.");
    }

    /// <summary>An unnamed element still gets a readable, re-typeable selector rather than a bare decimal.</summary>
    [TestMethod]
    public void Display_UnnamedElementGetsATypeAndHashSlug()
    {
        Assert.AreEqual(
            "contentpresenter-dddddddddd",
            DevToolsSelector.Display("12", "Microsoft.UI.Xaml.Controls.ContentPresenter", "", "dddddddddd", uniqueName: false));
    }

    /// <summary>
    /// Without an identity there is nothing to validate against, so the raw handle stays the printed form.
    /// Inventing a slug that cannot be checked would be worse than the decimal it replaced.
    /// </summary>
    [TestMethod]
    public void Display_FallsBackToTheHandleWhenTheTapGaveNoIdentity()
    {
        Assert.AreEqual("42", DevToolsSelector.Display("42", "Microsoft.UI.Xaml.Controls.Button", "Save", null, uniqueName: false));
    }

    /// <summary>
    /// The slug's own grammar. The 10-hex suffix is what makes a slug unmistakable: without it, an x:Name that
    /// happens to contain a dash would parse as a slug and be validated against an identity it never carried.
    /// </summary>
    [TestMethod]
    public void Parse_RequiresTheIdentitySuffix()
    {
        Assert.IsNotNull(DevToolsSlug.Parse("button-deletebutton-1111111111"));
        Assert.IsNotNull(DevToolsSlug.Parse("contentpresenter-dddddddddd"));
        Assert.IsNull(DevToolsSlug.Parse("submit-button"), "A hyphenated x:Name is not a slug.");
        Assert.IsNull(DevToolsSlug.Parse("button-111111111"), "Nine hex digits is not the identity width.");
        Assert.IsNull(DevToolsSlug.Parse("button-11111111111"), "Eleven hex digits is not the identity width.");
        Assert.IsNull(DevToolsSlug.Parse("button-ZZZZZZZZZZ"), "The identity is lowercase hex.");
        Assert.IsNull(DevToolsSlug.Parse("SubmitButton"));
    }

    /// <summary>
    /// The width is a shared contract with the tap (<c>DevToolsTreeLayout::kIdentityHexDigits</c>). If the two ever
    /// disagree, every slug silently degrades to a text query and resolves whatever happens to match — the
    /// exact failure mode selectors exist to remove.
    /// <para>
    /// Asserted through BEHAVIOUR rather than by comparing the constant to itself: what has to hold is that a
    /// token of the tap's width round-trips and one of a different width does not, which is the property a
    /// drifted constant would actually break.
    /// </para>
    /// </summary>
    [TestMethod]
    public void IdentityWidth_MatchesTheTapAndIsWideEnoughForTheCensus()
    {
        // 40 bits, as the tap mints it. A UIA slug's 16 bits collide essentially always over a 20,000-node
        // DevTools census (birthday bound ~300 nodes), so a narrower token would be worse than the raw handle.
        const string tapMintedIdentity = "3f2a91cd04";
        Assert.AreEqual(40, tapMintedIdentity.Length * 4, "The tap's token is 40 bits wide.");

        var slug = DevToolsSelector.Display("42", "Microsoft.UI.Xaml.Controls.Button", "", tapMintedIdentity, uniqueName: false);
        Assert.AreEqual("button-" + tapMintedIdentity, slug, "A tap identity must survive into the slug whole.");

        var parsed = DevToolsSlug.Parse(slug);
        Assert.IsNotNull(parsed, "The CLI must parse a slug built from the width the tap actually mints.");
        Assert.AreEqual(tapMintedIdentity, parsed.Value.Identity);

        Assert.IsNull(DevToolsSlug.Parse("button-" + tapMintedIdentity[..^1]),
            "One digit narrower is not the tap's identity and must not be read as one.");
        Assert.IsNull(DevToolsSlug.Parse("button-" + tapMintedIdentity + "0"),
            "One digit wider is not the tap's identity either.");
    }

    /// <summary>A slug resolves through the live tree, and the handle it yields is the tap's own.</summary>
    [TestMethod]
    public void Resolve_ASlugResolvesToItsElement()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.find", SubmitButtonMatch);
        var tap = new VisualTreeTap((uint)agent.Pid);

        var resolved = DevToolsSelector.Resolve(tap, "button-submitbutton-bbbbbbbbbb");

        Assert.IsTrue(resolved.Ok, resolved.Error);
        Assert.AreEqual("42", resolved.Handle);
    }

    private const string UnnamedButtons =
        """
        {"matches":[
          {"handle":"61","name":"","type":"Microsoft.UI.Xaml.Controls.Button","id":"3333333333","depth":4},
          {"handle":"62","name":"","type":"Microsoft.UI.Xaml.Controls.Button","id":"4444444444","depth":4}],
         "query":"StartButton","appAuthoredOnly":false,"searchedNodes":2,"censusNodes":2,"truncated":false}
        """;

    /// <summary>The AutomationId `winapp ui` selects by also selects the element in devtools; a shared one is refused.</summary>
    [TestMethod]
    [DataRow("""{"handle":"62","preview":"","automationId":"StartButton"}""", "62")]
    [DataRow("""{"handle":"61","preview":"","automationId":"StartButton"},{"handle":"62","preview":"","automationId":"StartButton"}""", null)]
    public void Resolve_AnAutomationIdResolvesWhenUnique(string previews, string? expected)
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", UnnamedButtons)
            .Answer("VisualTree.getPreviews", $$"""{"previews":[{{previews}}],"requested":2,"returned":2,"truncated":false}""");
        var tap = new VisualTreeTap((uint)agent.Pid);

        var resolved = DevToolsSelector.Resolve(tap, "StartButton");

        Assert.AreEqual(expected, resolved.Handle, resolved.Error);
        if (expected is null)
        {
            StringAssert.Contains(resolved.Error, "2 elements have AutomationId 'StartButton'");
            Assert.HasCount(2, resolved.Candidates);
        }
    }

    /// <summary>Inspect prints a confirmed declaration's line and the AutomationId, and JSON carries both.</summary>
    [TestMethod]
    public async Task Inspect_ShowsConfirmedLineAndAutomationId()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredEnvelope(AuthoredTree), IsAuthoredRequest)
            .Answer("VisualTree.getPreviews", """
                {"previews":[{"handle":"42","preview":"","automationId":"SubmitAid","file":"Views/MainWindow.xaml","line":24,"endLine":26,"column":13},
                             {"handle":"10","preview":""}],"requested":2,"returned":2,"truncated":false}
                """);

        var (_, human) = await RunAsync(new DevToolsInspectCommand(), agent, []);
        var (_, json) = await RunAsync(new DevToolsInspectCommand(), agent, ["--json"]);

        StringAssert.Contains(human, "[SubmitButton] Button Views/MainWindow.xaml:24 aid=SubmitAid");
        StringAssert.Contains(human, "[Root] Grid MainWindow.xaml" + Environment.NewLine, "An unconfirmed element shows its file only.");
        using var document = JsonDocument.Parse(json);
        var button = document.RootElement.GetProperty("elements")[0].GetProperty("children")[0];
        Assert.AreEqual("Views/MainWindow.xaml", button.GetProperty("file").GetString());
        Assert.AreEqual(24, button.GetProperty("line").GetInt32());
        Assert.AreEqual(26, button.GetProperty("endLine").GetInt32());
        Assert.AreEqual("SubmitAid", button.GetProperty("automationId").GetString());
        Assert.IsFalse(document.RootElement.GetProperty("elements")[0].TryGetProperty("line", out _));
    }

    /// <summary>
    /// The staleness gate. A slug whose type and name still match a live element but whose IDENTITY does not
    /// names an element that MOVED — and the similar one still there is not it. Resolving to it would be the
    /// silent wrong-element write the whole design exists to prevent.
    /// </summary>
    [TestMethod]
    public void Resolve_AStaleSlugIsRefused_NotResolvedToTheSimilarElement()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.find", SubmitButtonMatch);
        var tap = new VisualTreeTap((uint)agent.Pid);

        var resolved = DevToolsSelector.Resolve(tap, "button-submitbutton-0000000000");

        Assert.IsFalse(resolved.Ok, "A tampered/stale identity must never resolve.");
        StringAssert.Contains(resolved.Error, "stale");
        StringAssert.Contains(resolved.Error, "inspect", "The refusal must say how to get a current selector.");
        Assert.AreEqual(1, resolved.Candidates.Length, "The near miss is offered as a candidate, not silently used.");
        Assert.AreEqual("SubmitButton", resolved.Candidates[0].Selector);
    }

    /// <summary>A slug for an element that is simply gone is refused with rerun guidance, not a near-miss claim.</summary>
    [TestMethod]
    public void Resolve_AnUnknownSlugSaysToRerunInspect()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.find", SubmitButtonMatch)
            .Answer("VisualTree.enumerate", "[]");
        var tap = new VisualTreeTap((uint)agent.Pid);

        var resolved = DevToolsSelector.Resolve(tap, "checkbox-missing-9999999999");

        Assert.IsFalse(resolved.Ok);
        StringAssert.Contains(resolved.Error, "re-run");
        Assert.IsFalse(resolved.Error!.Contains("stale", StringComparison.OrdinalIgnoreCase),
            "Nothing of that type and name is live, so calling it stale would be a guess.");
    }

    /// <summary>Two rows of one DataTemplate are separately addressable by their slugs.</summary>
    [TestMethod]
    public void Resolve_DuplicateNamesAreIndividuallyAddressableBySlug()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.find", DuplicateNameMatches);
        var tap = new VisualTreeTap((uint)agent.Pid);

        Assert.AreEqual("51", DevToolsSelector.Resolve(tap, "button-deletebutton-1111111111").Handle);
        Assert.AreEqual("52", DevToolsSelector.Resolve(tap, "button-deletebutton-2222222222").Handle);
    }

    /// <summary>The selectors the two duplicate-named rows must be offered under.</summary>
    private static readonly string[] DuplicateRowSelectors =
        ["button-deletebutton-1111111111", "button-deletebutton-2222222222"];

    /// <summary>
    /// The bare NAME of a duplicated element is refused, and the refusal lists selectors that DO work — the
    /// `winapp ui` shape: never guess, always hand back something copy-pasteable.
    /// </summary>
    [TestMethod]
    public void Resolve_ADuplicatedNameIsRefusedWithUsableCandidates()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.find", DuplicateNameMatches);
        var tap = new VisualTreeTap((uint)agent.Pid);

        var resolved = DevToolsSelector.Resolve(tap, "DeleteButton");

        Assert.IsFalse(resolved.Ok);
        StringAssert.Contains(resolved.Error, "2 elements are named");
        CollectionAssert.AreEquivalent(
            DuplicateRowSelectors,
            resolved.Candidates.Select(c => c.Selector).ToArray(),
            "Candidates must be offered as SELECTORS, not as decimals the reader has to trust blindly.");
    }

    /// <summary>
    /// The narrowing search is an OPTIMISATION, never the last word. <c>VisualTree.find</c> matches a plain
    /// substring against the type as the runtime spells it, while the slug's type token has had every
    /// non-alphanumeric character stripped — so an ordinary <c>UserControl</c> class name like
    /// <c>My_Control</c> cannot match its own token, and the search returns nothing for a live, unmoved
    /// element. Reporting "no element matches, re-run inspect" there would be a loop: inspect reprints the
    /// same selector. The resolver must fall back to the whole tree before concluding anything.
    /// <para>
    /// The fake serves a find reply MISSING the element and an enumerate reply containing it — exactly the
    /// asymmetry the real tap produces for such a type.
    /// </para>
    /// </summary>
    [TestMethod]
    public void Resolve_ASlugTheNarrowedSearchCannotSee_IsFoundInTheWholeTree()
    {
        const string underscoreTree =
            """
            [{"handle":"70","type":"My_Control","id":"cafebabe01","childCount":0,"children":[]}]
            """;
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", Find("[]"))
            .Answer("VisualTree.enumerate", underscoreTree);
        var tap = new VisualTreeTap((uint)agent.Pid);

        var resolved = DevToolsSelector.Resolve(tap, "mycontrol-cafebabe01");

        Assert.IsTrue(resolved.Ok, resolved.Error);
        Assert.AreEqual("70", resolved.Handle);
        CollectionAssert.Contains(agent.Received, "VisualTree.enumerate",
            "A miss in the narrowed search must be re-checked against the whole tree, not reported as absent.");
    }

    /// <summary>
    /// The fallback must not turn a transport failure into a claim about the tree. When the whole-tree read
    /// itself fails there is nothing new to say, so the narrowed search's verdict stands.
    /// </summary>
    [TestMethod]
    public void Resolve_AnUnreadableTreeDoesNotBecomeAClaimThatTheElementIsGone()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", Find("[]"))
            .Error("VisualTree.enumerate", -32011, "no-tree", "no live visual tree");
        var tap = new VisualTreeTap((uint)agent.Pid);

        var resolved = DevToolsSelector.Resolve(tap, "mycontrol-cafebabe01");

        Assert.IsFalse(resolved.Ok);
        Assert.AreEqual(new DevToolsProtocolError(-32011, "no-tree", "no live visual tree"), resolved.RemoteError);
    }

    /// <summary>
    /// The classifier resolves missing source info in bounded batches, so a freshly attached app can be
    /// classified only partly — and the missing elements are the app's OWN. A view that omits them while
    /// saying nothing presents a partial answer as the whole truth, which is the failure the tap's own
    /// "truncation is REPORTED, never silent" rule exists to prevent.
    /// <para>
    /// The state comes from the SAME reply as the nodes, so this fixture cannot express the mismatch the old
    /// two-call shape allowed: a truncated pass and the tree it produced are one object.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Inspect_UnfinishedClassification_IsReportedInBothShapes()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer(
                "VisualTree.enumerate",
                AuthoredEnvelope(AuthoredTree, classificationTruncated: true, classifiedNodes: 100),
                IsAuthoredRequest)
            .Answer("VisualTree.enumerate", RawTree);

        var (exit, output) = await RunAsync(new DevToolsInspectCommand(), agent, []);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "not finished classifying");

        var (_, jsonOut) = await RunAsync(new DevToolsInspectCommand(), agent, ["--json"]);
        using var doc = JsonDocument.Parse(jsonOut);
        Assert.IsTrue(doc.RootElement.GetProperty("classificationTruncated").GetBoolean(),
            "A --json consumer must not read `truncated:false` for a demonstrably partial view.");
    }

    /// <summary>
    /// The completeness statement must come from the pass that produced the tree. Asking a SECOND time — the
    /// shape this replaced — lets a later pass finish the backfill and answer "complete" about a tree the
    /// FIRST pass had already rendered incomplete. The gate is that the command never asks again.
    /// </summary>
    [TestMethod]
    public async Task Inspect_ReadsCompletenessFromTheSamePassAsTheTree()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer(
                "VisualTree.enumerate",
                AuthoredEnvelope(AuthoredTree, classificationTruncated: true, classifiedNodes: 100),
                IsAuthoredRequest)
            .Answer("VisualTree.enumerate", RawTree)
            // A second pass that would claim the classification is finished. If the command consults it, the
            // warning above disappears and a partial tree is presented as whole.
            .Answer(
                "VisualTree.getAppAuthored",
                """
                {"appAuthored":["10","42"],"sourceInstrumented":true,"truncated":false,
                 "classifiedNodes":800,"censusNodes":800,"classifier":"allowlist"}
                """);

        var (_, output) = await RunAsync(new DevToolsInspectCommand(), agent, []);

        CollectionAssert.DoesNotContain(agent.Received, "VisualTree.getAppAuthored",
            "The tree's own reply carries its classification state; a second pass describes a different tree.");
        StringAssert.Contains(output, "not finished classifying",
            "The warning must follow the pass that produced these nodes, not a later, luckier one.");
    }

    /// <summary>
    /// An empty authored result while the classifier is still working is NOT "your app authored nothing".
    /// The tap therefore stops refusing with `no-authored` mid-classification, and the CLI must say which of
    /// the three empty cases it hit — otherwise a fresh attach tells a developer their markup is missing.
    /// </summary>
    [TestMethod]
    public async Task Inspect_EmptyAuthoredResultWhileClassifying_SaysNotFinished_NotThatNothingIsAuthored()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer(
                "VisualTree.enumerate",
                AuthoredEnvelope("[]", classificationTruncated: true, classifiedNodes: 100),
                IsAuthoredRequest)
            .Answer("VisualTree.enumerate", RawTree);

        var (exit, output) = await RunAsync(new DevToolsInspectCommand(), agent, ["--json"]);

        Assert.AreEqual(0, exit, "Falling back to the raw tree is still a successful read.");
        using var doc = JsonDocument.Parse(output);
        Assert.IsTrue(doc.RootElement.GetProperty("fallback").GetBoolean());
        var reason = doc.RootElement.GetProperty("fallbackReason").GetString();
        StringAssert.Contains(reason, "not finished classifying");
        Assert.IsFalse(reason!.Contains("classified no live element", StringComparison.Ordinal),
            "'Still working' must never be reported as 'your XAML contributed nothing'.");
    }

    /// <summary>
    /// An uninstrumented app is recognised as such on the FIRST call, even while the classifier is still
    /// backfilling. The runtime banks a source file as each element is added, so absence is evidence about
    /// the launch rather than about progress — and leading with "still classifying" would tell a developer to
    /// wait for something that will never arrive, when the fix is to relaunch with `--devtools`. This is the
    /// exact shape the live gate hit: the first inspect said "not finished", the second said the truth.
    /// </summary>
    [TestMethod]
    public async Task Inspect_UninstrumentedAndStillClassifying_NamesTheLaunch_NotTheProgress()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer(
                "VisualTree.enumerate",
                AuthoredEnvelope("[]", sourceInstrumented: false, classificationTruncated: true, classifiedNodes: 100),
                IsAuthoredRequest)
            .Answer("VisualTree.enumerate", RawTree);

        var (exit, output) = await RunAsync(new DevToolsInspectCommand(), agent, []);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "no XAML source information");
        StringAssert.Contains(output, "winapp run --devtools", "The actionable next step is a relaunch.");
        Assert.IsFalse(output.Contains("cannot yet say which elements are yours", StringComparison.Ordinal),
            "Waiting is the wrong advice for an app that will never report source info.");
    }

    /// <summary>The genuinely-all-framework case still reads as itself, so the distinction stays meaningful.</summary>
    [TestMethod]
    public async Task Inspect_EmptyAuthoredResultAfterAFinishedPass_SaysNothingIsAuthored()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredEnvelope("[]"), IsAuthoredRequest)
            .Answer("VisualTree.enumerate", RawTree);

        var (_, output) = await RunAsync(new DevToolsInspectCommand(), agent, ["--json"]);

        using var doc = JsonDocument.Parse(output);
        StringAssert.Contains(doc.RootElement.GetProperty("fallbackReason").GetString(), "classified no live element");
    }

    /// <summary>
    /// The empty-result path returns BEFORE the footer, so a warning that only lives in the footer never
    /// reaches the reader on exactly the run that needs it: "no elements matched" while the classifier is
    /// still backfilling, followed by a second run that finds them.
    /// </summary>
    [TestMethod]
    public async Task Inspect_EmptyFilterResultWhileClassifying_StillSaysTheClassifierIsUnfinished()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer(
                "VisualTree.enumerate",
                AuthoredEnvelope(AuthoredTree, classificationTruncated: true, classifiedNodes: 100),
                IsAuthoredRequest)
            .Answer("VisualTree.enumerate", RawTree);

        var (exit, output) = await RunAsync(
            new DevToolsInspectCommand(), agent, ["--filter", "__definitely_missing__"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "not finished classifying");

        var (_, jsonOut) = await RunAsync(
            new DevToolsInspectCommand(), agent, ["--filter", "__definitely_missing__", "--json"]);
        using var doc = JsonDocument.Parse(jsonOut);
        StringAssert.Contains(doc.RootElement.GetProperty("error").GetProperty("message").GetString(),
            "not finished classifying");
    }

    /// <summary>The same hole in search: an empty match list is where the reason matters most.</summary>
    [TestMethod]
    public async Task Search_EmptyResultWhileClassifying_StillSaysTheClassifierIsUnfinished()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer(
            "VisualTree.find",
            """
            {"matches":[],"query":"submit","appAuthoredOnly":true,"searchedNodes":2,"censusNodes":800,
             "truncated":false,"sourceInstrumented":true,"classificationTruncated":true}
            """);

        var (exit, output) = await RunAsync(new DevToolsSearchCommand(), agent, ["Submit"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "not finished classifying");

        var (_, jsonOut) = await RunAsync(new DevToolsSearchCommand(), agent, ["Submit", "--json"]);
        using var doc = JsonDocument.Parse(jsonOut);
        StringAssert.Contains(doc.RootElement.GetProperty("error").GetProperty("message").GetString(),
            "not finished classifying");
    }

    /// <summary>Items created from data have no XAML source; a search for their text must still find them.</summary>
    [TestMethod]
    public async Task Search_NoAuthoredMatch_FindsGeneratedItemsAndSaysSo()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find",
                """{"matches":[],"query":"phi","appAuthoredOnly":true,"searchedNodes":2,"censusNodes":20,"truncated":false,"sourceInstrumented":true,"classificationTruncated":false}""",
                request => request.Contains("\"appAuthoredOnly\":true", StringComparison.Ordinal))
            .Answer("VisualTree.find",
                """{"matches":[{"handle":"42","name":"","type":"Microsoft.UI.Xaml.Controls.NavigationViewItem","depth":9}],"query":"phi","appAuthoredOnly":false,"searchedNodes":20,"censusNodes":20,"truncated":false}""");

        var (exit, output) = await RunAsync(new DevToolsSearchCommand(), agent, ["Phi 3 Medium"]);
        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "NavigationViewItem");
        StringAssert.Contains(output, "Nothing in your XAML matches");

        var (_, jsonOut) = await RunAsync(new DevToolsSearchCommand(), agent, ["Phi 3 Medium", "--json"]);
        using var doc = JsonDocument.Parse(jsonOut);
        Assert.IsTrue(doc.RootElement.GetProperty("fallback").GetBoolean());
        Assert.AreEqual(1, doc.RootElement.GetProperty("matchCount").GetInt32());
    }

    [TestMethod]
    public async Task Search_NoMatchAnywhere_DoesNotClaimTextIsUnsearched()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.find",
            """{"matches":[],"query":"x","appAuthoredOnly":false,"searchedNodes":2,"censusNodes":2,"truncated":false,"sourceInstrumented":true,"classificationTruncated":false}""");
        var (exit, output) = await RunAsync(new DevToolsSearchCommand(), agent, ["__missing__"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "displayed text");
        Assert.IsFalse(output.Contains("not an element's rendered text", StringComparison.Ordinal));
    }

    /// <summary>
    /// The agent-facing shape has to be actionable. An ambiguity error that carries only a sentence ending in
    /// a colon leaves a `--json` caller with nothing to retry — on the duplicate-`x:Name` workflow, where
    /// every candidate has a distinct working selector.
    /// </summary>
    [TestMethod]
    public async Task AmbiguousSelector_JsonCarriesTheCandidatesNotJustTheSentence()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", DuplicateNameMatches)
            .Answer("VisualTree.enumerate", RawTree);

        var (exit, output) = await RunAsync(new DevToolsGetPropertyCommand(), agent, ["DeleteButton", "--json"]);

        Assert.AreEqual(1, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.IsFalse(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual("selector", doc.RootElement.GetProperty("error").GetProperty("token").GetString());

        var candidates = doc.RootElement.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.AreEqual(2, candidates.Length, "Both live elements must be offered.");
        CollectionAssert.AreEquivalent(
            DuplicateRowSelectors,
            candidates.Select(c => c.GetProperty("selector").GetString()).ToArray(),
            "A caller must be able to retry with a selector, not re-derive one from prose.");
        foreach (var candidate in candidates)
        {
            Assert.IsTrue(DevToolsSelector.IsHandle(candidate.GetProperty("handle").GetString()!),
                "The exact protocol identity travels alongside, so a stale selector is never a dead end.");
            Assert.IsTrue(candidate.TryGetProperty("type", out _));
            Assert.IsTrue(candidate.TryGetProperty("id", out _));
        }
    }

    /// <summary>
    /// A truncated narrowed search cannot support a confident "stale" verdict: the element the selector names
    /// may be outside the cap, and the similar one we happened to see is not evidence about it. The rescan has
    /// to run even when a near miss is present.
    /// </summary>
    [TestMethod]
    public void Resolve_ATruncatedSearchWithANearMissStillRescansTheWholeTree()
    {
        const string nearMissOnly =
            """
            {"matches":[{"handle":"90","name":"","type":"Microsoft.UI.Xaml.Controls.Button",
              "id":"9999999999","depth":3}],
             "query":"button","appAuthoredOnly":false,"searchedNodes":2,"censusNodes":20000,"truncated":true}
            """;
        const string treeWithTheRealOne =
            """
            [{"handle":"91","type":"Microsoft.UI.Xaml.Controls.Button","id":"abcdef0123","childCount":0,"children":[]}]
            """;
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", nearMissOnly)
            .Answer("VisualTree.enumerate", treeWithTheRealOne);
        var tap = new VisualTreeTap((uint)agent.Pid);

        var resolved = DevToolsSelector.Resolve(tap, "button-abcdef0123");

        Assert.IsTrue(resolved.Ok, resolved.Error);
        Assert.AreEqual("91", resolved.Handle);
    }

    /// <summary>
    /// Search carries the same fact from the tap's own reply, where it is a SEPARATE field from the
    /// 20,000-node flatten cap: the two are independent incompletenesses and collapsing them would report one
    /// while hiding the other.
    /// </summary>
    [TestMethod]
    public async Task Search_UnfinishedClassification_IsReportedSeparatelyFromTheNodeCap()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer(
            "VisualTree.find",
            """
            {"matches":[{"handle":"42","name":"SubmitButton","type":"Microsoft.UI.Xaml.Controls.Button",
              "id":"bbbbbbbbbb","uniqueName":true,"depth":2}],
             "query":"submit","appAuthoredOnly":true,"searchedNodes":2,"censusNodes":800,
             "truncated":false,"classificationTruncated":true}
            """);

        var (exit, output) = await RunAsync(new DevToolsSearchCommand(), agent, ["Submit"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "Source detection is incomplete");
        StringAssert.Contains(output, "--all");
        Assert.IsFalse(output.Contains("node limit truncated the search", StringComparison.Ordinal),
            "The node cap was not hit; only the classification is incomplete.");

        var (_, jsonOut) = await RunAsync(new DevToolsSearchCommand(), agent, ["Submit", "--json"]);
        using var doc = JsonDocument.Parse(jsonOut);
        Assert.IsFalse(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.IsTrue(doc.RootElement.GetProperty("classificationTruncated").GetBoolean());
    }

    /// <summary>The raw handle stays the escape hatch, and costs no lookup at all.</summary>
    [TestMethod]
    public void Resolve_ARawHandleStillWorksWithoutASearch()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.find", SubmitButtonMatch);
        var tap = new VisualTreeTap((uint)agent.Pid);

        var resolved = DevToolsSelector.Resolve(tap, "42");

        Assert.IsTrue(resolved.Ok);
        Assert.AreEqual("42", resolved.Handle);
        Assert.AreEqual(0, agent.Received.Count, "A handle is the protocol identity; resolving it needs no search.");
    }

    /// <summary>
    /// Both identities travel in <c>--json</c>: the readable one and the exact one. Carrying only the selector
    /// would leave a caller with no way to address an element whose selector has gone stale; carrying only the
    /// handle is the opaque-decimal problem this change set out to fix.
    /// </summary>
    [TestMethod]
    public async Task Inspect_JsonCarriesBothSelectorAndHandle()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredEnvelope(AuthoredTree), IsAuthoredRequest)
            .Answer("VisualTree.enumerate", AuthoredTree);

        var (_, output) = await RunAsync(new DevToolsInspectCommand(), agent, ["--json"]);

        using var doc = JsonDocument.Parse(output);
        var button = doc.RootElement.GetProperty("elements")[0].GetProperty("children")[0];
        Assert.AreEqual("SubmitButton", button.GetProperty("selector").GetString());
        Assert.AreEqual("42", button.GetProperty("handle").GetString());
        Assert.AreEqual("bbbbbbbbbb", button.GetProperty("id").GetString());
        Assert.IsTrue(button.GetProperty("uniqueName").GetBoolean());
    }

    /// <summary>The human tree prints the selector, and does not repeat it as an x:Name attribute as well.</summary>
    [TestMethod]
    public async Task Inspect_HumanOutputLeadsWithTheSelector()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredEnvelope(AuthoredTree), IsAuthoredRequest)
            .Answer("VisualTree.enumerate", AuthoredTree);

        var (_, output) = await RunAsync(new DevToolsInspectCommand(), agent, []);

        StringAssert.Contains(output, "[SubmitButton] Button");
        Assert.IsFalse(output.Contains("x:Name=\"SubmitButton\"", StringComparison.Ordinal),
            "The selector already IS the name; printing both says the same thing twice.");
    }

    /// <summary>Search speaks the same vocabulary, so a selector copied from a search result works verbatim.</summary>
    [TestMethod]
    public async Task Search_PrintsSelectorsAndCarriesBothIdentitiesInJson()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.find", SubmitButtonMatch);

        var (exit, output) = await RunAsync(new DevToolsSearchCommand(), agent, ["Submit", "--json"]);

        Assert.AreEqual(0, exit);
        using var doc = JsonDocument.Parse(output);
        var match = doc.RootElement.GetProperty("matches")[0];
        Assert.AreEqual("SubmitButton", match.GetProperty("selector").GetString());
        Assert.AreEqual("42", match.GetProperty("handle").GetString());
    }

    // Builds the command's handler by hand rather than through DI: these tests supply their own resolver, and
    // the console has to be the one whose output is asserted.
    private static async Task<(int ExitCode, string Output)> RunAsync(
        DevToolsLiveCommand command, FakeDevToolsProtocolAgent agent, string[] args)
    {
        var console = new TestConsole();
        console.Profile.Width = 200; // Don't let console wrapping split a phrase an assertion matches on.
        System.CommandLine.Invocation.AsynchronousCommandLineAction handler = command switch
        {
            DevToolsInspectCommand => new DevToolsInspectCommand.Handler(agent.Resolver(), console),
            DevToolsSearchCommand => new DevToolsSearchCommand.Handler(agent.Resolver(), console),
            DevToolsGetPropertyCommand => new DevToolsGetPropertyCommand.Handler(agent.Resolver(), console),
            _ => throw new NotSupportedException(command.Name),
        };

        command.Action = handler;
        var exitCode = await command.Parse(args).InvokeAsync();
        return (exitCode, console.Output);
    }
}
