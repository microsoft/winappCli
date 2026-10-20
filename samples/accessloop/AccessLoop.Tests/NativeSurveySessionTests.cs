using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fake = AccessLoop.Tests.NativeSurveySessionTestFixture;

namespace AccessLoop.Tests;

[TestClass]
public sealed class NativeSurveySessionTests
{
    private string directory = null!;
    private static CancellationToken Token => CancellationToken.None;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Environment.CurrentDirectory, ".native-survey-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, recursive: true);

    [TestMethod]
    public void PublicConstructorMatchesTheExistingSessionContract()
    {
        Assert.IsNotNull(typeof(NativeSurveySession).GetConstructor(
            [typeof(SurveyProfile), typeof(IAuditPlatform), typeof(AuditTarget), typeof(string), typeof(bool)]));
        Assert.IsFalse(typeof(IDisposable).IsAssignableFrom(typeof(NativeSurveySession)));
        Assert.IsFalse(typeof(IAsyncDisposable).IsAssignableFrom(typeof(NativeSurveySession)));
    }

    [TestMethod]
    public void PreflightReadsIdentityAndDesktopWithoutRequiringForegroundOrAttachingAgain()
    {
        var fake = new Fake();
        var session = fake.Session();
        session.Preflight();
        Assert.AreEqual(fake.Native.Context, session.GetContext());
        Assert.AreEqual(fake.Target, session.Target);
        Assert.IsNull(fake.Focused);
        Assert.HasCount(0, fake.Calls);
        Assert.AreEqual(0, fake.AttachCalls);
        Assert.IsTrue(fake.VerifyCalls > 0);
        Assert.IsTrue(fake.Native.WindowReads > 0);
    }

    [TestMethod]
    [DataRow("pid")]
    [DataRow("window")]
    [DataRow("start")]
    [DataRow("process")]
    public void ChangedIdentityIsNonResumableBeforeNativeCommands(string changed)
    {
        var fake = new Fake();
        var target = changed switch
        {
            "pid" => fake.Target with { ProcessId = fake.Target.ProcessId + 1 },
            "window" => fake.Target with { WindowHandle = fake.Target.WindowHandle + 1 },
            "start" => fake.Target with { ProcessStartedAtUtc = fake.Target.ProcessStartedAtUtc.AddSeconds(1) },
            _ => fake.Target with { ProcessName = "OtherApp" }
        };
        var session = new NativeSurveySession(Fake.Profile, fake, target, "fixture", false, fake.Native);
        var error = Assert.ThrowsExactly<SurveyBlockedException>(session.Preflight);
        Assert.AreEqual("target-changed", error.Code);
        Assert.IsFalse(error.CanResume);
        Assert.HasCount(0, fake.Calls);
    }

    [TestMethod]
    [DataRow("minimized", "window-minimized")]
    [DataRow("hidden", "window-not-visible")]
    [DataRow("input", "input-desktop-unavailable")]
    [DataRow("current", "input-desktop-unavailable")]
    [DataRow("target", "input-desktop-unavailable")]
    [DataRow("station", "input-desktop-unavailable")]
    [DataRow("width", "window-metrics-unavailable")]
    [DataRow("dpi", "window-metrics-unavailable")]
    public void UnavailableNavigationEnvironmentBlocksWithoutInput(string changed, string code)
    {
        var fake = new Fake();
        fake.Native.Window = changed switch
        {
            "minimized" => fake.Native.Window with { Minimized = true },
            "hidden" => fake.Native.Window with { Visible = false },
            "input" => fake.Native.Window with { InputDesktop = null },
            "current" => fake.Native.Window with { CurrentDesktop = "Winlogon" },
            "target" => fake.Native.Window with { TargetDesktop = "Winlogon" },
            "station" => fake.Native.Window with { InteractiveWindowStation = false },
            "width" => fake.Native.Window with { Width = 0 },
            _ => fake.Native.Window with { Dpi = 0 }
        };
        var error = Assert.ThrowsExactly<SurveyBlockedException>(fake.Session().Preflight);
        Assert.AreEqual(code, error.Code);
        Assert.IsTrue(error.CanResume);
        Assert.HasCount(0, fake.Calls);
    }

    [TestMethod]
    public async Task PaneSelectorFallbackIsRejectedBeforeAnyMutation()
    {
        var fake = new Fake { EditTree = tree => Fake.Find(tree, "PaneRoot")!["automationId"] = "WholeWindowRoot" };
        var error = await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => fake.Session().ReadNavigationStateAsync(Token));
        StringAssert.Contains(error.Message, "fallback");
        Assert.IsFalse(error.CanResume);
        Assert.HasCount(0, fake.Invoked);
        Assert.HasCount(0, fake.Keys);
        Assert.IsTrue(fake.Calls.All(call => call.Arguments[0] == "inspect"));
    }

    [TestMethod]
    public async Task ExactWinUiSplitViewPaneMayExposeTheWindowRole()
    {
        var fake = new Fake
        {
            EditTree = tree =>
            {
                Fake.Find(tree, "PaneRoot")!["type"] = "Window";
                Fake.Find(tree, "PaneRoot")!["className"] = "SplitViewPane";
            }
        };
        var navigation = await fake.Session().ReadNavigationStateAsync(Token);
        Assert.AreEqual(Fake.Profile.GroupIds.Count, navigation.ExpandedGroups.Count);
        Assert.HasCount(0, fake.Keys);
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    public async Task WindowRoleBelowTheExactPaneRootIsStillRejected()
    {
        var fake = new Fake { EditTree = tree => Fake.Find(tree, "MenuItemsHost")!["type"] = "Window" };
        var error = await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => fake.Session().ReadNavigationStateAsync(Token));
        StringAssert.Contains(error.Message, "additional window");
        Assert.HasCount(0, fake.Keys);
    }

    [TestMethod]
    [DataRow("PaneRoot")]
    [DataRow("MenuItemsHost")]
    [DataRow("GeneralNavItem")]
    public async Task AnyTruncatedNavigationDescendantIsRejected(string id)
    {
        var fake = new Fake { EditTree = tree => Fake.Find(tree, id)!["hasMoreChildren"] = true };
        var error = await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => fake.Session().ReadNavigationStateAsync(Token));
        StringAssert.Contains(error.Message, "truncated");
        Assert.HasCount(0, fake.Keys);
    }

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("missing-id")]
    [DataRow("missing-group")]
    [DataRow("wrong-window")]
    [DataRow("wrong-process")]
    public async Task MalformedOrForeignNavigationCannotEstablishAnInventory(string defect)
    {
        var fake = new Fake
        {
            EditTree = tree =>
            {
                var menu = Fake.Find(tree, "MenuItemsHost")!["children"]!.AsArray();
                switch (defect)
                {
                    case "duplicate":
                        menu.Add(Fake.Find(tree, "GeneralNavItem")!.DeepClone());
                        break;
                    case "missing-id":
                        Fake.Find(tree, "GeneralNavItem")!.Remove("automationId");
                        break;
                    case "missing-group":
                        menu.Remove(Fake.Find(tree, "SystemToolsNavItem"));
                        break;
                    case "wrong-window":
                        tree["windows"]![0]!["hwnd"] = 999;
                        break;
                    default:
                        tree["windows"]![0]!["processId"] = 999;
                        break;
                }
            }
        };
        await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => fake.Session().ReadNavigationStateAsync(Token));
        Assert.HasCount(0, fake.Keys);
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    public async Task DiscoveryRefinesCollapsedSelectionWithoutInventingOriginalGroupFlags()
    {
        var fake = new Fake { Selected = "AwakeNavItem" };
        fake.Groups["InputOutputNavItem"] = true;
        var session = fake.Session();
        var original = await session.ReadNavigationStateAsync(Token);
        Assert.IsNull(original.SelectedPageId);
        Assert.IsTrue(original.ExpandedGroups["InputOutputNavItem"]);
        Assert.IsFalse(original.ExpandedGroups["SystemToolsNavItem"]);
        Assert.HasCount(0, fake.Keys);
        var inventory = await session.DiscoverAsync(original, Token);
        Assert.AreEqual("AwakeNavItem", inventory.OriginalNavigation.SelectedPageId);
        Assert.HasCount(5, inventory.OriginalNavigation.ExpandedGroups);
        foreach (var group in Fake.Profile.GroupIds)
            Assert.AreEqual(original.ExpandedGroups[group], inventory.OriginalNavigation.ExpandedGroups[group]);
        Assert.IsNull(original.SelectedPageId);
        Assert.HasCount(7, inventory.Pages);
        Assert.HasCount(3, inventory.Excluded);
        Assert.HasCount(15, inventory.NavigationIds);
        CollectionAssert.AreEqual(new[] { "SystemToolsNavItem" },
            inventory.Pages.Single(page => page.Id == "AwakeNavItem").GroupIds.ToArray());
        Assert.HasCount(4, fake.Keys);
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    public async Task AutomationIdInspectionCanResolveToACanonicalActionSelector()
    {
        var fake = new Fake { ReturnResolvedSelectors = true };
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        var result = await session.NavigateAsync(Page(inventory, "GeneralNavItem"), Token);
        Assert.IsTrue(result.Selected && result.HeadingMatched);
        Assert.IsTrue(fake.Calls.Where(call => call.Arguments[0] is "focus" or "invoke" or "scroll-into-view")
            .All(call => call.Arguments[1].StartsWith("resolved-", StringComparison.Ordinal)));
        Assert.IsTrue(fake.Keys.All(args => args[5].StartsWith("resolved-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ProfileCanExplicitlyAuthorizeAnIdlessHeadingOutsideTheNavigationPane()
    {
        var fake = new Fake { HeadingIdOverride = "" };
        var profile = Fake.Profile with { HeadingWithoutIdPageIds = ["GeneralNavItem"] };
        var session = fake.Session(profile);
        var inventory = await fake.DiscoverAsync(session);
        var ready = await session.NavigateAsync(Page(inventory, "GeneralNavItem"), Token);
        Assert.IsTrue(ready.HeadingMatched && ready.Selected);
        Assert.AreEqual("visible-name-outside-navigation", ready.EvidenceKind);
    }

    [TestMethod]
    public async Task IdlessNavigationLabelCannotMasqueradeAsPageContent()
    {
        var fake = new Fake { HeadingIdOverride = "", HeadingX = 10 };
        var profile = Fake.Profile with { HeadingWithoutIdPageIds = ["GeneralNavItem"] };
        var session = fake.Session(profile);
        var inventory = await fake.DiscoverAsync(session);
        var ready = await session.NavigateAsync(Page(inventory, "GeneralNavItem"), Token);
        Assert.IsFalse(ready.HeadingMatched);
    }

    [TestMethod]
    public async Task MatchingBodyTextBelowTheProfiledHeadingRegionDoesNotProveReadiness()
    {
        var fake = new Fake { HeadingIdOverride = "", HeadingY = 400 };
        var profile = Fake.Profile with { HeadingWithoutIdPageIds = ["GeneralNavItem"] };
        var session = fake.Session(profile);
        var inventory = await fake.DiscoverAsync(session);
        var ready = await session.NavigateAsync(Page(inventory, "GeneralNavItem"), Token);
        Assert.IsFalse(ready.HeadingMatched);
    }

    [TestMethod]
    public async Task LowerDuplicateLabelDoesNotHideTheUniqueProfiledHeader()
    {
        var fake = new Fake
        {
            HeadingIdOverride = "", DuplicateHeading = true,
            EditResponse = (args, response) =>
            {
                if (args[0] == "search" && args[1] == "General")
                    response["matches"]![1]!["y"] = 400;
            }
        };
        var profile = Fake.Profile with { HeadingWithoutIdPageIds = ["GeneralNavItem"] };
        var session = fake.Session(profile);
        var inventory = await fake.DiscoverAsync(session);
        Assert.IsTrue((await session.NavigateAsync(Page(inventory, "GeneralNavItem"), Token)).HeadingMatched);
    }

    [TestMethod]
    public async Task IdlessHeadingIsNotAcceptedForPagesWithoutExplicitProfilePermission()
    {
        var fake = new Fake { HeadingIdOverride = "" };
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        var ready = await session.NavigateAsync(Page(inventory, "GeneralNavItem"), Token);
        Assert.IsFalse(ready.HeadingMatched);
    }

    [TestMethod]
    public async Task DeclaredGroupsUseFocusedRealEnterAndRestoreInReverseOrder()
    {
        var fake = new Fake();
        fake.Offscreen.Add("SystemToolsNavItem");
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        Assert.HasCount(5, fake.Keys);
        foreach (var keys in fake.Keys)
            CollectionAssert.AreEqual(new[] { "send-keys", "enter", "--via", "send-input", "--target", keys[5] }, keys);
        Assert.IsTrue(fake.Calls.Any(call => call.Arguments.SequenceEqual(new[] { "scroll-into-view", "SystemToolsNavItem" })));
        Assert.IsTrue(fake.Calls.Where(call => call.Arguments[0] == "focus")
            .All(call => Fake.Profile.GroupIds.Contains(call.Arguments[1])));
        var restored = await session.RestoreAsync(inventory.OriginalNavigation, Token);
        Assert.AreEqual("restored-navigation", restored.Status);
        Assert.IsTrue(fake.Groups.Values.All(expanded => !expanded));
        CollectionAssert.AreEqual(Fake.Profile.GroupIds.Reverse().ToArray(), fake.Keys.Skip(5).Select(keys => keys[5]).ToArray());
        Assert.AreEqual("GeneralNavItem", fake.Selected);
        StringAssert.Contains(restored.Message, "Scroll position is not guaranteed");
        Assert.IsFalse(fake.Calls.Any(call => call.Arguments[0] is "click" or "set-value" or "close" or "launch"));
    }

    [TestMethod]
    public async Task NewNestedGroupsAreNotImplicitPermissionToExpand()
    {
        var fake = new Fake();
        fake.EditTree = tree =>
        {
            if (!fake.Groups["SystemToolsNavItem"])
                return;
            var unexpected = fake.Navigation("SurpriseNavItem", "Surprise");
            unexpected["expandState"] = "collapsed";
            Fake.Find(tree, "SystemToolsNavItem")!["children"]!.AsArray().Add(unexpected);
        };
        var session = fake.Session();
        var original = await session.ReadNavigationStateAsync(Token);
        var error = await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => session.DiscoverAsync(original, Token));
        StringAssert.Contains(error.Message, "Unexpected navigation group");
        Assert.HasCount(1, fake.Keys);
        Assert.AreEqual("SystemToolsNavItem", fake.Keys[0][5]);
        var commands = fake.Calls.Count;
        var restored = await session.RestoreAsync(original, Token);
        Assert.AreEqual("incomplete", restored.Status);
        Assert.IsTrue(fake.Calls.Skip(commands).All(call => call.Arguments[0] == "inspect"));
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    public async Task OutOfMenuAndExplicitlyExcludedEntriesAreNeverInvoked()
    {
        var fake = new Fake();
        fake.EditTree = tree =>
        {
            var footer = Fake.Find(tree, "FooterMenuItemsHost")!["children"]!.AsArray();
            var feedback = Fake.Find(tree, "FeedbackNavItem")!;
            footer.Remove(feedback);
            Fake.Find(tree, "MenuItemsHost")!["children"]!.AsArray().Add(feedback);
            footer.Add(fake.Navigation("OutsideMenuNavItem", "External destination"));
        };
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        Assert.IsTrue(inventory.NavigationIds.Contains("OutsideMenuNavItem"));
        Assert.IsTrue(inventory.Excluded.Any(item => item.Id == "FeedbackNavItem"));
        var before = fake.Calls.Count;
        foreach (var excluded in inventory.Excluded)
            await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() =>
                session.NavigateAsync(new(excluded.Id, excluded.Name, []), Token));
        Assert.AreEqual(before, fake.Calls.Count);
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    public async Task APreviouslyApprovedLeafMovedOutsideTheMenuIsRefusedOnFreshInspection()
    {
        var fake = new Fake();
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        fake.EditTree = tree =>
        {
            var awake = Fake.Find(tree, "AwakeNavItem")!;
            Fake.Find(tree, "SystemToolsNavItem")!["children"]!.AsArray().Remove(awake);
            Fake.Find(tree, "FooterMenuItemsHost")!["children"]!.AsArray().Add(awake);
        };
        var before = fake.Calls.Count;
        await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => session.NavigateAsync(Page(inventory, "AwakeNavItem"), Token));
        CollectionAssert.AreEqual(new[] { "inspect" }, fake.Calls.Skip(before).Select(call => call.Arguments[0]).ToArray());
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    [DataRow("AwakeNavItem", "唤醒")]
    [DataRow("AlwaysOnTopNavItem", "前端显示")]
    [DataRow("GrabAndMoveNavItem", "Grab And Move")]
    [DataRow("GeneralNavItem", "General")]
    public async Task OnlyExactNamesOrExplicitProfileAliasesConfirmHeadings(string id, string heading)
    {
        var fake = new Fake { HeadingName = heading };
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        var ready = await session.NavigateAsync(Page(inventory, id), Token);
        Assert.IsTrue(ready.Selected);
        Assert.IsTrue(ready.HeadingMatched);
        Assert.AreEqual(heading, ready.Heading);
        Assert.IsTrue(ready.ObservedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.AreEqual(id, fake.Invoked.Single());
    }

    [TestMethod]
    [DataRow("unexpected")]
    [DataRow("duplicate")]
    [DataRow("wrong-type")]
    [DataRow("empty")]
    public async Task SelectionAndTreeChangesAloneCannotConfirmReadiness(string defect)
    {
        var fake = new Fake
        {
            HeadingName = defect == "unexpected" ? "Awake with additional text" : "Awake",
            DuplicateHeading = defect == "duplicate",
            HeadingType = defect == "wrong-type" ? "Edit" : "Text",
            EmptyHeadingSearch = defect == "empty"
        };
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        var before = fake.Calls.Count;
        var ready = await session.NavigateAsync(Page(inventory, "AwakeNavItem"), Token);
        Assert.IsTrue(ready.Selected);
        Assert.IsFalse(ready.HeadingMatched);
        Assert.HasCount(1, fake.Invoked);
        Assert.IsTrue(fake.Calls.Count - before < 30);
        Assert.IsFalse(fake.Calls.Skip(before).Any(call => call.Arguments[0] is "click" or "send-keys"));
    }

    [TestMethod]
    public async Task TruncatedHeadingSearchCannotProveUniqueness()
    {
        var fake = new Fake { TruncatedHeadingSearch = true };
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        var error = await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() =>
            session.NavigateAsync(Page(inventory, "AwakeNavItem"), Token));
        StringAssert.Contains(error.Message, "truncated");
    }

    [TestMethod]
    public async Task VerificationDoesOnlyThreeReadsWithoutRefocusingOrScrolling()
    {
        var fake = new Fake();
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        var page = Page(inventory, "AwakeNavItem");
        await session.NavigateAsync(page, Token);
        fake.Offscreen.Add(page.Id);
        var before = fake.Calls.Count;
        var ready = await session.VerifyPageAsync(page, Token);
        Assert.IsTrue(ready.Selected && ready.HeadingMatched);
        CollectionAssert.AreEqual(new[] { "inspect", "get-property", "search" },
            fake.Calls.Skip(before).Select(call => call.Arguments[0]).ToArray());
    }

    [TestMethod]
    [DataRow("AutomationId", "OtherNavItem")]
    [DataRow("ControlType", "Button")]
    [DataRow("ClassName", "OtherNavigationClass")]
    [DataRow("ToggleState", "Off")]
    [DataRow("ExpandCollapseState", "LeafNode")]
    [DataRow("Value", "PRIVATE-VALUE")]
    [DataRow("IsReadOnly", "False")]
    [DataRow("IsSelected", null)]
    public async Task LivePropertiesCannotChangeTheApprovedLeafIntoAnotherControl(string property, string? value)
    {
        var fake = new Fake();
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        fake.EditProperties = (id, properties) =>
        {
            if (id == "AwakeNavItem")
                properties[property] = value;
        };
        var error = await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => session.NavigateAsync(Page(inventory, "AwakeNavItem"), Token));
        Assert.HasCount(0, fake.Invoked);
        Assert.IsFalse(error.Message.Contains("PRIVATE-VALUE", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OffscreenLeafUsesScrollFocusAndSelectionRatherThanAClick()
    {
        var fake = new Fake();
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        fake.Offscreen.Add("AwakeNavItem");
        var before = fake.Calls.Count;
        var ready = await session.NavigateAsync(Page(inventory, "AwakeNavItem"), Token);
        Assert.IsTrue(ready.Selected && ready.HeadingMatched);
        CollectionAssert.AreEqual(new[] { "scroll-into-view", "focus", "invoke" },
            fake.Calls.Skip(before).Where(call => call.Arguments[0] is not ("inspect" or "get-property" or "search"))
                .Select(call => call.Arguments[0]).ToArray());
        Assert.AreEqual(1, fake.Calls.Skip(before).Count(call => call.Arguments[0] == "inspect"));
        Assert.IsTrue(fake.Calls.Count - before <= 10);
    }

    [TestMethod]
    [DataRow("disabled")]
    [DataRow("focus")]
    public async Task UnusableLeafDoesNotReceiveActivation(string defect)
    {
        var fake = new Fake();
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        fake.IgnoreFocus = defect == "focus";
        if (defect == "disabled")
            fake.EditProperties = (id, properties) => properties["IsEnabled"] = "False";
        var error = await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => session.NavigateAsync(Page(inventory, "AwakeNavItem"), Token));
        Assert.IsTrue(error.CanResume);
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    [DataRow("hwnd")]
    [DataRow("elementId")]
    [DataRow("pattern")]
    public async Task InvokeResponseMustIdentifyTheExactWindowElementAndSupportedPattern(string field)
    {
        var fake = new Fake();
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        fake.EditResponse = (args, response) =>
        {
            if (args[0] != "invoke")
                return;
            if (field == "hwnd")
                response[field] = 999;
            else
                response[field] = field == "elementId" ? "FeedbackNavItem" : "TogglePattern";
        };
        await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => session.NavigateAsync(Page(inventory, "AwakeNavItem"), Token));
        Assert.AreEqual("invoke", fake.Calls.Last().Arguments[0]);
        Assert.HasCount(1, fake.Invoked);
    }

    [TestMethod]
    public async Task IdentityLossAfterInvocationPreventsAllFurtherCommandsAndCleanupBypass()
    {
        var fake = new Fake();
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        fake.AfterCommand = args =>
        {
            if (args[0] == "invoke")
                fake.IdentityValid = false;
        };
        var error = await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => session.NavigateAsync(Page(inventory, "AwakeNavItem"), Token));
        Assert.AreEqual("target-changed", error.Code);
        Assert.IsFalse(error.CanResume);
        Assert.AreEqual("invoke", fake.Calls.Last().Arguments[0]);
        var before = fake.Calls.Count;
        var restoration = await session.RestoreAsync(inventory.OriginalNavigation, Token);
        Assert.AreEqual("incomplete", restoration.Status);
        Assert.AreEqual(before, fake.Calls.Count);
    }

    [TestMethod]
    public async Task GroupFocusMustBeConfirmedBeforeSendingEnter()
    {
        var fake = new Fake { IgnoreFocus = true };
        var session = fake.Session();
        var original = await session.ReadNavigationStateAsync(Token);
        var error = await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => session.DiscoverAsync(original, Token));
        Assert.AreEqual("navigation-focus-lost", error.Code);
        Assert.HasCount(0, fake.Keys);
        Assert.IsTrue(fake.Groups.Values.All(expanded => !expanded));
    }

    [TestMethod]
    [DataRow("target")]
    [DataRow("via")]
    [DataRow("keys")]
    [DataRow("hwnd")]
    public async Task GroupEnterResponseMustMatchTheExactApprovedInput(string field)
    {
        var fake = new Fake();
        fake.EditResponse = (args, response) =>
        {
            if (args[0] != "send-keys")
                return;
            if (field == "hwnd")
                response[field] = 999;
            else
                response[field] = field == "keys" ? "right" : field == "via" ? "post-message" : "FeedbackNavItem";
        };
        var session = fake.Session();
        var original = await session.ReadNavigationStateAsync(Token);
        await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => session.DiscoverAsync(original, Token));
        Assert.HasCount(1, fake.Keys);
        Assert.HasCount(0, fake.Invoked);
        Assert.AreEqual("send-keys", fake.Calls.Last().Arguments[0]);
    }

    [TestMethod]
    [DataRow("added")]
    [DataRow("missing")]
    [DataRow("renamed")]
    [DataRow("reparented")]
    public async Task ResumeCannotAddDropRenameOrReparentFrozenPages(string change)
    {
        var fake = new Fake();
        var inventory = await fake.DiscoverAsync(fake.Session());
        var index = fake.Pages.FindIndex(page => page.Id == "AwakeNavItem");
        switch (change)
        {
            case "added":
                fake.Pages.Add(new("UnapprovedNavItem", "Unapproved", []));
                break;
            case "missing":
                fake.Pages.RemoveAt(index);
                break;
            case "renamed":
                fake.Pages[index] = fake.Pages[index] with { Name = "New name" };
                break;
            default:
                fake.Pages[index] = fake.Pages[index] with { GroupIds = ["AdvancedNavItem"] };
                break;
        }
        var before = fake.Calls.Count;
        var error = await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => fake.Session().ValidateInventoryAsync(inventory, Token));
        Assert.IsFalse(error.CanResume);
        Assert.IsTrue(fake.Calls.Skip(before).All(call => call.Arguments[0] == "inspect"));
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    public async Task ResumeReexpandsDeclaredGroupsButRetainsTheFirstRestorationState()
    {
        var fake = new Fake();
        fake.Groups["InputOutputNavItem"] = true;
        var first = fake.Session();
        var inventory = await fake.DiscoverAsync(first);
        await first.RestoreAsync(inventory.OriginalNavigation, Token);
        var resumed = fake.Session();
        await resumed.ValidateInventoryAsync(inventory, Token);
        Assert.IsTrue(fake.Groups.Values.All(expanded => expanded));
        var restored = await resumed.RestoreAsync(inventory.OriginalNavigation, Token);
        Assert.AreEqual("restored-navigation", restored.Status);
        foreach (var group in Fake.Profile.GroupIds)
            Assert.AreEqual(inventory.OriginalNavigation.ExpandedGroups[group], fake.Groups[group]);
    }

    [TestMethod]
    public async Task AGroupThatDoesNotSettleReceivesOnlyOneEnterAndReportsIncompleteRestoration()
    {
        var fake = new Fake { IgnoreGroupKeys = true };
        var session = fake.Session();
        var original = await session.ReadNavigationStateAsync(Token);
        var error = await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => session.DiscoverAsync(original, Token));
        Assert.AreEqual("group-not-settled", error.Code);
        Assert.HasCount(1, fake.Keys);
        fake.Groups["AdvancedNavItem"] = true;
        var restoration = await session.RestoreAsync(original, Token);
        Assert.AreEqual("incomplete", restoration.Status);
        StringAssert.Contains(restoration.Message, "did not reach");
        Assert.HasCount(2, fake.Keys);
    }

    [TestMethod]
    public async Task APartiallyExpandedGroupIsPolledWithoutSendingAnotherEnter()
    {
        var fake = new Fake();
        var intermediateReads = 0;
        fake.EditProperties = (id, properties) =>
        {
            if (id == "SystemToolsNavItem" && fake.Groups[id] && intermediateReads++ == 0)
                properties["ExpandCollapseState"] = "PartiallyExpanded";
        };
        var session = fake.Session();
        await fake.DiscoverAsync(session);
        Assert.HasCount(5, fake.Keys);
        Assert.AreEqual(1, fake.Keys.Count(keys => keys[5] == "SystemToolsNavItem"));
        Assert.IsTrue(intermediateReads >= 2);
    }

    [TestMethod]
    public async Task CancellationKeepsAuditEvidenceAndAllowsASeparateCleanupToken()
    {
        var fake = new Fake();
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        using var cancellation = new CancellationTokenSource();
        fake.AfterCommand = args =>
        {
            if (args[0] == "audit-snapshot")
                cancellation.Cancel();
        };
        var report = await session.ScanAsync(directory, cancellation.Token);
        Assert.AreEqual("partial", report.Status);
        Assert.IsNotNull(report.Snapshot);
        Assert.AreEqual(0, fake.AxeCalls);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "scan.json")));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "report.html")));
        var restoration = await session.RestoreAsync(inventory.OriginalNavigation, Token);
        Assert.AreEqual("restored-navigation", restoration.Status);
    }

    [TestMethod]
    public async Task CancelledCleanupIsVisibleAndDoesNotBecomeAnUnboundedRetry()
    {
        var fake = new Fake();
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = fake.Calls.Count;
        var restoration = await session.RestoreAsync(inventory.OriginalNavigation, cancellation.Token);
        Assert.AreEqual("incomplete", restoration.Status);
        StringAssert.Contains(restoration.Message, "cleanup was cancelled");
        Assert.AreEqual(before, fake.Calls.Count);
    }

    [TestMethod]
    public async Task UnobservableOriginalSelectionIsNotReportedAsFullyRestored()
    {
        var fake = new Fake { Selected = "" };
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        Assert.IsNull(inventory.OriginalNavigation.SelectedPageId);
        var restoration = await session.RestoreAsync(inventory.OriginalNavigation, Token);
        Assert.AreEqual("incomplete", restoration.Status);
        StringAssert.Contains(restoration.Message, "unobservable");
        Assert.IsTrue(fake.Groups.Values.All(expanded => !expanded));
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    public async Task PartialScansRetainTheirExactCaptureStatusUnknownsAndNativeFailureEvidence()
    {
        var fake = new Fake { Snapshot = AuditFixture.Snapshot(partial: true), SnapshotExit = 1, AxeScan = AuditFixture.Axe(true) };
        var session = fake.Session();
        var report = await session.ScanAsync(directory, Token);
        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual("snapshot", report.Mode);
        Assert.AreEqual(1, report.RuleFailureCount);
        Assert.IsTrue(report.Results.Any(result => result.Status is "unsupported" or "not-tested"));
        Assert.AreEqual(report.RunId, AuditFiles.ReadVerified(Path.Combine(directory, "scan.json")).RunId);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "report.html")));
        CollectionAssert.AreEqual(new[] { "audit-snapshot" }, fake.Calls.Select(call => call.Arguments[0]).ToArray());
        CollectionAssert.AreEqual(new[] { "audit-snapshot", "--depth", "30", "--max-elements", "2000", "--timeout", "1000" },
            fake.Calls.Single().Arguments);
        Assert.AreEqual(fake.Calls.Count + fake.AxeCalls, session.Performance.NativeCommands);
        Assert.IsTrue(session.Performance.NativeMilliseconds >= 0);
        Assert.IsTrue(session.Performance.AuditMilliseconds >= 0);
    }

    [TestMethod]
    public async Task ScreenshotChoiceUsesPassiveExactWindowCaptureInTheProvidedAbsoluteDirectory()
    {
        var fake = new Fake();
        var session = fake.Session(screenshot: true);
        var report = await session.ScanAsync(directory, Token);
        Assert.AreEqual("completed", report.Status);
        var screenshot = fake.Calls.Single(call => call.Arguments[0] == "audit-screenshot");
        Assert.AreEqual(Path.Combine(directory, "screenshot.png"), screenshot.Arguments[2]);
        Assert.IsTrue(report.Artifacts.Any(artifact => artifact.Path == "screenshot.png"));
        Assert.IsTrue(fake.Calls.All(call => call.Target == fake.Target));
        Assert.AreEqual(fake.Calls.Count + fake.AxeCalls, session.Performance.NativeCommands);
    }

    [TestMethod]
    public async Task ScanRefusesRelativeOrNonemptyDirectoriesWithoutOverwritingEvidence()
    {
        var fake = new Fake();
        var session = fake.Session();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => session.ScanAsync("relative-output", Token));
        var existing = Path.Combine(directory, "scan.json");
        File.WriteAllText(existing, "original evidence");
        await Assert.ThrowsExactlyAsync<IOException>(() => session.ScanAsync(directory, Token));
        Assert.AreEqual("original evidence", File.ReadAllText(existing));
        Assert.HasCount(0, fake.Calls);
    }

    [TestMethod]
    public async Task NavigationPropertiesAndSearchValuesNeverBecomePersistentSurveyEvidence()
    {
        var fake = new Fake();
        var session = fake.Session();
        var inventory = await fake.DiscoverAsync(session);
        var ready = await session.NavigateAsync(Page(inventory, "AwakeNavItem"), Token);
        AuditFiles.WriteNew(Path.Combine(directory, "navigation-evidence.json"), new { inventory, ready });
        var json = File.ReadAllText(Path.Combine(directory, "navigation-evidence.json"));
        Assert.IsFalse(json.Contains("PRIVATE-", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"value\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("HelpText", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PageAndNativeCommandBudgetsBoundAnInvocationWithoutDisablingCleanup()
    {
        var fake = new Fake();
        fake.ExpandAll();
        var tooSmall = fake.Session(Fake.Profile with { MaxPages = 6 });
        await Assert.ThrowsExactlyAsync<SurveyBlockedException>(() => tooSmall.ReadNavigationStateAsync(Token));
        var session = fake.Session(Fake.Profile with { MaxPages = 7 });
        var inventory = await fake.DiscoverAsync(session);
        SurveyBlockedException? exhausted = null;
        for (var index = 0; index < 400; index++)
        {
            try { await session.VerifyPageAsync(Page(inventory, "GeneralNavItem"), Token); }
            catch (SurveyBlockedException error)
            {
                exhausted = error;
                break;
            }
        }
        Assert.IsNotNull(exhausted);
        Assert.AreEqual("command-budget-exceeded", exhausted.Code);
        var restoration = await session.RestoreAsync(inventory.OriginalNavigation, Token);
        Assert.AreEqual("restored-navigation", restoration.Status);
        Assert.AreEqual(fake.Calls.Count + fake.AxeCalls, session.Performance.NativeCommands + 1);
        Assert.IsTrue(fake.Calls.All(call => call.Arguments.Length <= 7
            && call.Target.ProcessId == fake.Target.ProcessId && call.Target.WindowHandle == fake.Target.WindowHandle));
    }

    private static SurveyPageDefinition Page(SurveyInventory inventory, string id) => inventory.Pages.Single(page => page.Id == id);
}
