// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task SlugCollision_StrictSelectionRejectsBeforePatterns(bool externalIdentity, bool nameless)
    {
        using var tree = new SlugCollisionTree(nameless);
        var svc = NewService();
        if (externalIdentity)
        {
            await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(() => svc.InvokeAsync(tree.Target,
                new UiElement { Selector = tree.Slug, AutomationId = "save" }, UiInvokeAction.Invoke, default));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(() =>
                svc.FindSingleElementAsync(tree.Target, new UiSelector { Slug = tree.Slug }, requireUnique: true, default));
        }
        Assert.AreEqual(0, tree.PatternReads);
        Assert.AreEqual(0, tree.Invocations);
    }

    [TestMethod]
    [DataRow(true, "First")]
    [DataRow(true, "Second")]
    [DataRow(true, "absent")]
    [DataRow(false, "First")]
    [DataRow(false, "Second")]
    [DataRow(false, null)]
    public async Task SlugCollision_PredicatesAndLegacyFirstMatch(bool requireUnique, string? className)
    {
        using var tree = new SlugCollisionTree();
        var svc = NewService();
        var selected = await svc.FindSingleElementAsync(tree.Target,
            new UiSelector { Slug = tree.Slug, ClassName = className }, requireUnique, default);
        if (className == "absent" || (!requireUnique && className == "Second"))
        {
            Assert.IsNull(selected);
        }
        else
        {
            Assert.IsNotNull(selected);
            Assert.AreSame(className == "Second" ? tree.Second : tree.First, selected.Context!.AutomationElement);
            Assert.AreEqual(tree.Slug, selected.Selector);
            Assert.AreEqual(42L, selected.WindowHandle);
            if (requireUnique)
            {
                Assert.IsNull(selected.InvokableAncestor);
                await svc.InvokeAsync(tree.Target, selected, UiInvokeAction.Invoke, default);
                Assert.AreEqual(1, tree.Invocations);
                Assert.AreEqual(0, svc.SerializedElementResolutionCount);
            }
        }
    }

    [TestMethod]
    [DataRow("boundary", true, false)]
    [DataRow("self", false, false)]
    [DataRow("wrong-type", false, false)]
    [DataRow("boundary", true, true)]
    [DataRow("self", false, true)]
    [DataRow("wrong-type", false, true)]
    public async Task SlugCollision_StrictRootAndTypeBounds(string scope, bool found, bool nameless)
    {
        using var tree = new SlugCollisionTree(nameless);
        var selector = new UiSelector
        {
            Slug = tree.Slug,
            ControlType = scope == "wrong-type" ? "Text" : "Button",
            Root = scope == "self"
                ? new UiSelector { Slug = tree.Slug, ClassName = "First" }
                : new UiSelector { Slug = "btn-boundary-0210" },
        };
        var selected = await NewService().FindSingleElementAsync(tree.Target, selector, requireUnique: true, default);
        Assert.AreEqual(found, selected is not null);
        if (found) { Assert.AreSame(tree.First, selected!.Context!.AutomationElement); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SlugCollision_WindowBoundsExcludeOtherWindow(bool externalIdentity)
    {
        using var tree = new SlugCollisionTree();
        UiAutomationService.s_elementFromHandle = (_, hwnd) => hwnd == 43 ? tree.Boundary : tree.Root;
        UiAutomationService.s_getAllAppWindows = (_, _) =>
            throw new AssertFailedException("An explicit source HWND must not search another window.");
        var svc = NewService();
        if (externalIdentity)
        {
            await svc.InvokeAsync(tree.Target, new UiElement
            {
                Selector = tree.Slug, AutomationId = "save", WindowHandle = 43,
            }, UiInvokeAction.Invoke, default);
            Assert.AreEqual(1, tree.Invocations);
            Assert.AreEqual(1, svc.SerializedElementResolutionCount);
        }
        else
        {
            var target = new UiTarget
            {
                ProcessId = Environment.ProcessId, WindowHandle = 43, IsExplicitWindow = true,
            };
            var selected = await svc.FindSingleElementAsync(target,
                new UiSelector { Slug = tree.Slug }, requireUnique: true, default);
            Assert.IsNotNull(selected);
            Assert.AreSame(tree.First, selected.Context!.AutomationElement);
        }
    }

    [TestMethod]
    public async Task SlugCollision_StrictRootRejectsAmbiguityBeforePatterns()
    {
        using var tree = new SlugCollisionTree();
        await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(() => NewService().FindSingleElementAsync(
            tree.Target, new UiSelector { Query = "anything", Root = new UiSelector { Slug = tree.Slug } },
            requireUnique: true, default));
        Assert.AreEqual(0, tree.PatternReads);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task SlugCollision_StrictScanPropagatesLateFailure(bool cancel, bool externalIdentity)
    {
        using var tree = new SlugCollisionTree();
        using var cancellation = new CancellationTokenSource();
        var failure = new COMException("Later collision provider failed.");
        var getBstr = UiAutomationService.s_getCurrentBstr;
        UiAutomationService.s_getCurrentBstr = (element, property) =>
        {
            if (ReferenceEquals(element, tree.Second) && property == UIA_PROPERTY_ID.UIA_NamePropertyId)
            {
                if (!cancel) { throw failure; }
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            return getBstr(element, property);
        };
        var svc = NewService();
        Task Act() => externalIdentity
            ? svc.InvokeAsync(tree.Target, new UiElement { Selector = tree.Slug, AutomationId = "save" },
                UiInvokeAction.Invoke, cancellation.Token)
            : svc.FindSingleElementAsync(tree.Target, new UiSelector { Slug = tree.Slug }, true, cancellation.Token);
        if (cancel)
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(Act);
        }
        else
        {
            var error = await Assert.ThrowsExactlyAsync<COMException>(Act);
            Assert.AreSame(failure, error);
        }
        Assert.AreEqual(0, tree.PatternReads);
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow(0L, false)]
    [DataRow(null, true)]
    [DataRow(0L, true)]
    public async Task SlugCollision_ExternalAppSlug_FindsSecondaryWithoutSource(long? sourceHwnd, bool nameless)
    {
        using var tree = new SlugCollisionTree(nameless);
        tree.ConfigureAppWindows(mainMatch: false, collision: false);
        var svc = NewService();

        await svc.InvokeAsync(tree.AppTarget, new UiElement
        {
            Selector = tree.Slug, WindowHandle = sourceHwnd,
        }, UiInvokeAction.Invoke, default);

        CollectionAssert.AreEqual(new nint[] { 42, 43, 44 }, tree.BoundHandles);
        Assert.AreEqual(1, tree.Invocations);
        Assert.AreEqual(1, svc.SerializedElementResolutionCount);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task SlugCollision_AppWindows_RejectCollisionBeforePatterns(bool externalIdentity, bool nameless)
    {
        using var tree = new SlugCollisionTree(nameless);
        tree.ConfigureAppWindows(mainMatch: true, collision: true);
        var svc = NewService();
        await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(() => externalIdentity
            ? svc.InvokeAsync(tree.AppTarget, new UiElement { Selector = tree.Slug }, UiInvokeAction.Invoke, default)
            : svc.FindSingleElementAsync(tree.AppTarget, new UiSelector { Slug = tree.Slug }, true, default));

        CollectionAssert.AreEqual(new nint[] { 42, 43, 44 }, tree.BoundHandles);
        Assert.AreEqual(0, tree.PatternReads);
        Assert.AreEqual(0, tree.Invocations);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SlugCollision_AppWindows_DeduplicateOwnedProviderBeforePatterns(bool externalIdentity)
    {
        using var tree = new SlugCollisionTree();
        tree.ConfigureAppWindows(mainMatch: true, collision: false);
        var svc = NewService();
        if (externalIdentity)
        {
            await svc.InvokeAsync(tree.AppTarget, new UiElement { Selector = tree.Slug }, UiInvokeAction.Invoke, default);
        }
        else
        {
            var selected = await svc.FindSingleElementAsync(tree.AppTarget,
                new UiSelector { Slug = tree.Slug }, true, default);
            Assert.IsNotNull(selected);
            Assert.AreSame(tree.First, selected.Context!.AutomationElement);
            await svc.InvokeAsync(tree.AppTarget, selected, UiInvokeAction.Invoke, default);
        }

        CollectionAssert.AreEqual(new nint[] { 42, 43, 44 }, tree.BoundHandles);
        Assert.IsGreaterThan(0, tree.Comparisons);
        Assert.AreEqual(1, tree.Invocations);
        Assert.AreEqual(externalIdentity ? 1 : 0, svc.SerializedElementResolutionCount);
    }

    [TestMethod]
    [DataRow(false, "found")]
    [DataRow(true, "found")]
    [DataRow(false, "missing")]
    [DataRow(true, "missing")]
    [DataRow(false, "closed")]
    [DataRow(true, "closed")]
    [DataRow(false, "failed")]
    [DataRow(true, "failed")]
    public async Task SlugCollision_ExternalAppSlug_WindowBoundaryNeverRecovers(bool recordedSource, string state)
    {
        using var tree = new SlugCollisionTree();
        tree.ConfigureAppWindows(mainMatch: true, collision: true);
        var hwnd = state switch { "found" => 42L, "missing" => 44L, "closed" => 45L, _ => 46L };
        var target = recordedSource ? tree.AppTarget : new UiTarget
        {
            ProcessId = Environment.ProcessId, WindowHandle = hwnd, IsExplicitWindow = true,
        };
        var element = new UiElement { Selector = tree.Slug, WindowHandle = recordedSource ? hwnd : null };
        UiAutomationService.s_getAllAppWindows = (_, _) =>
            throw new AssertFailedException("A source or explicit HWND must not search siblings.");
        UiAutomationService.s_getRootElement = (_, _, _) =>
            throw new AssertFailedException("A source or explicit HWND must not recover.");
        var svc = NewService();
        if (state == "found")
        {
            await svc.InvokeAsync(target, element, UiInvokeAction.Invoke, default);
            Assert.AreEqual(1, tree.Invocations);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                svc.InvokeAsync(target, element, UiInvokeAction.Invoke, default));
            Assert.AreEqual(0, tree.PatternReads);
            Assert.AreEqual(0, tree.Invocations);
        }
        CollectionAssert.AreEqual(new nint[] { (nint)hwnd }, tree.BoundHandles);
    }

    [TestMethod]
    [DataRow(false, "provider")]
    [DataRow(true, "provider")]
    [DataRow(false, "missing")]
    [DataRow(true, "missing")]
    [DataRow(false, "cancel")]
    [DataRow(true, "cancel")]
    public async Task SlugCollision_AppWindows_LateFailureNeverInvokesFirstMatch(bool externalIdentity, string failure)
    {
        using var tree = new SlugCollisionTree();
        tree.ConfigureAppWindows(mainMatch: true, collision: false);
        using var cancellation = new CancellationTokenSource();
        var bind = UiAutomationService.s_elementFromHandle;
        var error = new COMException("Later app window failed.");
        UiAutomationService.s_elementFromHandle = (service, hwnd) =>
        {
            var root = bind(service, hwnd);
            if (hwnd != 44) { return root; }
            if (failure == "provider") { throw error; }
            if (failure == "missing") { return null; }
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return root;
        };
        var svc = NewService();
        Task Act() => externalIdentity
            ? svc.InvokeAsync(tree.AppTarget, new UiElement { Selector = tree.Slug }, UiInvokeAction.Invoke, cancellation.Token)
            : svc.FindSingleElementAsync(tree.AppTarget, new UiSelector { Slug = tree.Slug }, true, cancellation.Token);
        if (failure == "provider")
        {
            Assert.AreSame(error, await Assert.ThrowsExactlyAsync<COMException>(Act));
        }
        else if (failure == "missing")
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(Act);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(Act);
        }
        CollectionAssert.AreEqual(new nint[] { 42, 43, 44 }, tree.BoundHandles);
        Assert.AreEqual(0, tree.PatternReads);
        Assert.AreEqual(0, tree.Invocations);
    }

    [TestMethod]
    public async Task SlugCollision_ExternalAppSlug_AutoKeepsFirstMatch()
    {
        using var tree = new SlugCollisionTree();
        tree.ConfigureAppWindows(mainMatch: true, collision: true);
        UiAutomationService.s_getAllAppWindows = (_, _) =>
            throw new AssertFailedException("Auto must retain its main-window-first behavior.");
        var svc = NewService();

        Assert.AreEqual("InvokePattern",
            await svc.InvokeAsync(tree.AppTarget, new UiElement { Selector = tree.Slug }, default));

        Assert.AreEqual(1, tree.Invocations);
        Assert.AreEqual(1, svc.SerializedElementResolutionCount);
    }

    [TestMethod]
    public async Task SlugCollision_RetainedSlug_DoesNotRebindAcrossAppWindows()
    {
        using var tree = new SlugCollisionTree();
        var svc = NewService();
        var selected = await svc.FindSingleElementAsync(tree.Target,
            new UiSelector { Slug = tree.Slug, ClassName = "First" }, true, default);
        Assert.IsNotNull(selected);
        tree.ConfigureAppWindows(mainMatch: true, collision: true);
        UiAutomationService.s_getAllAppWindows = (_, _) =>
            throw new AssertFailedException("A retained runtime slug must not enumerate windows.");
        UiAutomationService.s_elementFromHandle = (_, _) =>
            throw new AssertFailedException("A retained runtime slug must not rebind.");

        await svc.InvokeAsync(tree.AppTarget, selected, UiInvokeAction.Invoke, default);

        Assert.AreEqual(1, tree.Invocations);
        Assert.AreEqual(0, svc.SerializedElementResolutionCount);
    }

    private sealed unsafe class SlugCollisionTree : IDisposable
    {
        private static readonly Type ElementProxyType = CreateElementProxyType();
        private readonly SAFEARRAY* _firstId = SlugGeneratorTests.CreateInt32SafeArray([1]);
        private readonly SAFEARRAY* _secondId = SlugGeneratorTests.CreateInt32SafeArray([65537]);
        public IUIAutomationElement First { get; }
        public IUIAutomationElement Second { get; }
        public IUIAutomationElement Root { get; }
        public IUIAutomationElement Boundary { get; }
        public string Slug { get; }
        public int PatternReads { get; private set; }
        public int Invocations { get; private set; }
        public int Comparisons { get; private set; }
        public List<nint> BoundHandles { get; } = [];
        public UiTarget AppTarget { get; } = new()
        {
            ProcessId = Environment.ProcessId, ProcessName = "fake", WindowHandle = 42,
        };
        public UiTarget Target { get; } = new()
        {
            ProcessId = Environment.ProcessId, ProcessName = "fake", WindowHandle = 42, IsExplicitWindow = true,
        };

        public SlugCollisionTree(bool nameless = false)
        {
            var automationId = nameless ? "" : "save";
            Slug = SlugGenerator.GenerateSlugFromSafeArray("Button", automationId, null, _firstId);
            Assert.AreEqual(nameless ? "btn-0210" : "btn-save-0210", Slug);
            Assert.AreEqual(Slug, SlugGenerator.GenerateSlugFromSafeArray("Button", automationId, null, _secondId));
            First = MakeElement(automationId, "First", _firstId);
            Second = MakeElement(automationId, "Second", _secondId);
            Root = MakeElement("root", "Root", _firstId);
            Boundary = MakeElement("boundary", "Boundary", _firstId);
            Assert.AreEqual("0210", SlugGenerator.ComputeHashFromSafeArray(First.GetRuntimeId()));
            Assert.AreEqual("0210", SlugGenerator.ComputeHashFromSafeArray(Second.GetRuntimeId()));
            var walker = ComProxy<IUIAutomationTreeWalker>((method, args) => method.Name switch
            {
                "GetFirstChildElement" => ReferenceEquals(args![0], Root) ? Boundary
                    : ReferenceEquals(args![0], Boundary) ? First : null,
                "GetNextSiblingElement" => ReferenceEquals(args![0], Boundary) ? Second : null,
                "GetParentElement" => ReferenceEquals(args![0], Root) ? null : Root,
                _ => ThrowCom(),
            });
            UiAutomationService.s_getRootElement = (_, _, _) => Root;
            UiAutomationService.s_elementFromHandle = (_, _) => Root;
            UiAutomationService.s_getExplicitIdentityWalker = _ => walker;
            UiAutomationService.s_getControlViewWalker = _ => walker;
            UiAutomationService.s_getAllAppWindows = (_, _) => [];
            UiAutomationService.s_compareElements = (_, left, right) => ReferenceEquals(left, right);
            UiAutomationService.s_getElementProcessId = _ => Environment.ProcessId;
        }

        public void ConfigureAppWindows(bool mainMatch, bool collision)
        {
            var lastRoot = MakeElement("last", "Last", _firstId);
            var walker = ComProxy<IUIAutomationTreeWalker>((method, args) => method.Name switch
            {
                "GetFirstChildElement" => ReferenceEquals(args![0], Root) ? (mainMatch ? First : null)
                    : ReferenceEquals(args![0], Boundary) ? (collision ? Second : First) : null,
                "GetNextSiblingElement" => null,
                "GetParentElement" => ReferenceEquals(args![0], Root) ? null : Root,
                _ => ThrowCom(),
            });
            UiAutomationService.s_getExplicitIdentityWalker = _ => walker;
            UiAutomationService.s_getControlViewWalker = _ => walker;
            UiAutomationService.s_getAllAppWindows = (_, _) =>
                [(42, Environment.ProcessId, "Main"), (43, Environment.ProcessId, "Secondary"), (44, Environment.ProcessId, "Last")];
            UiAutomationService.s_elementFromHandle = (_, hwnd) =>
            {
                BoundHandles.Add(hwnd);
                if (hwnd == 44)
                {
                    Assert.AreEqual(0, PatternReads, "Complete the app scan before probing any candidate patterns.");
                }
                if (hwnd == 46) { throw new COMException("Window closed", unchecked((int)0x80040201)); }
                return hwnd == 42 ? Root : hwnd == 43 ? Boundary : hwnd == 44 ? lastRoot : null;
            };
            UiAutomationService.s_compareElements = (_, left, right) =>
            {
                Comparisons++;
                return ReferenceEquals(left, right);
            };
        }

        private IUIAutomationElement MakeElement(string automationId, string className, SAFEARRAY* runtimeId)
        {
            var proxy = ComProxy<IUIAutomationElement>((method, args) => method.Name switch
            {
                "get_CurrentAutomationId" => StringBstr(automationId),
                "get_CurrentName" => StringBstr(automationId),
                "get_CurrentClassName" => StringBstr(className),
                "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId,
                "get_CurrentBoundingRectangle" => new RECT(),
                "get_CurrentIsEnabled" => new BOOL(true),
                "get_CurrentIsOffscreen" => new BOOL(false),
                "get_CurrentNativeWindowHandle" => new HWND(42),
                "GetCurrentPattern" => ReadPattern((UIA_PATTERN_ID)args![0]!),
                _ => ThrowCom(),
            });
            var element = (IUIAutomationElement)Activator.CreateInstance(ElementProxyType)!;
            ElementProxyType.GetField("RuntimeId")!.SetValue(element, (nint)runtimeId);
            ElementProxyType.GetField("Inner")!.SetValue(element, proxy);
            return element;
        }

        private static Type CreateElementProxyType()
        {
            // DispatchProxy cannot unbox pointer returns. Implement only GetRuntimeId in IL;
            // all other COM methods retain the suite's ordinary deterministic proxy behavior.
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("SlugCollisionProxy"), AssemblyBuilderAccess.Run);
            var proxyAssembly = ComProxy<IUIAutomationElement>((_, _) => null).GetType().Assembly;
            foreach (var access in proxyAssembly.GetCustomAttributesData()
                .Where(a => a.AttributeType.Name == "IgnoresAccessChecksToAttribute"))
            {
                assembly.SetCustomAttribute(new CustomAttributeBuilder(access.Constructor,
                    access.ConstructorArguments.Select(a => a.Value).ToArray()!));
            }
            var module = assembly.DefineDynamicModule("Main");
            var type = module.DefineType("SlugElementProxy", TypeAttributes.Public, typeof(object), [typeof(IUIAutomationElement)]);
            var field = type.DefineField("RuntimeId", typeof(nint), FieldAttributes.Public);
            var inner = type.DefineField("Inner", typeof(IUIAutomationElement), FieldAttributes.Public);
            foreach (var member in typeof(IUIAutomationElement).GetMethods())
            {
                var parameters = member.GetParameters();
                var method = type.DefineMethod(member.Name, MethodAttributes.Public | MethodAttributes.Virtual,
                    member.ReturnType, parameters.Select(p => p.ParameterType).ToArray());
                var il = method.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, member.Name == "GetRuntimeId" ? field : inner);
                if (member.Name != "GetRuntimeId")
                {
                    for (var i = 0; i < parameters.Length; i++) { il.Emit(OpCodes.Ldarg, i + 1); }
                    il.Emit(OpCodes.Callvirt, member);
                }
                il.Emit(OpCodes.Ret);
                type.DefineMethodOverride(method, member);
            }
            return type.CreateType()!;
        }

        private object? ReadPattern(UIA_PATTERN_ID pattern)
        {
            PatternReads++;
            return pattern == UIA_PATTERN_ID.UIA_InvokePatternId
                ? ComProxy<IUIAutomationInvokePattern>((_, _) => { Invocations++; return null; })
                : null;
        }

        public void Dispose()
        {
            // Hashing borrows these arrays; the fixture owns and destroys each exactly once.
            _ = SlugGeneratorTests.SafeArrayDestroy(_firstId);
            _ = SlugGeneratorTests.SafeArrayDestroy(_secondId);
        }
    }
}
