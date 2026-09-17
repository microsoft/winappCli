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
