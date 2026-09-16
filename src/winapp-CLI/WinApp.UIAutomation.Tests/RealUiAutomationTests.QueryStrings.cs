// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    public static IEnumerable<object[]> QueryStringFailures()
    {
        foreach (var path in new[] { "bulk", "walk", "root", "root-self", "metadata", "slug", "root-slug", "property", "value" })
        {
            foreach (var getter in new[] { "get_CurrentName", "get_CurrentAutomationId", "get_CurrentClassName" })
            {
                foreach (var hresult in new[] { unchecked((int)0x80040201), unchecked((int)0x80004005) })
                {
                    yield return [path, getter, hresult];
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(QueryStringFailures))]
    public async Task Query_StringGetterFailureCannotBecomeAbsence(string path, string getter, int hresult)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        await ResolveAsync(svc, target, "txtValue");
        var fail = false;
        var reads = 0;
        var failure = new COMException("String getter failed.", hresult);
        InstallQueryStringProvider(svc, fx, method =>
        {
            if (fail && method == getter) { reads++; throw failure; }
        }, path == "walk");
        var model = (await svc.SearchAsync(target,
            new UiSelector { ControlType = "Edit" }, 1, CancellationToken.None)).Single();
        if (path is "property" or "value")
        {
            model.Context = null; // These failures occur while re-resolving a serialized model.
        }
        if (path == "root-self")
        {
            var field = typeof(UiAutomationService).GetField("_automation", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var automation = (IUIAutomation)field.GetValue(svc)!;
            UiAutomationService.s_getRootElement = (_, _, _) =>
                automation.ElementFromHandle(new(fx.OnUiThread(() => fx.ValueBox.Handle)));
        }
        fail = true;
        var predicate = new UiSelector
        {
            Query = getter == "get_CurrentName" ? "query-name" : "query-id",
            ClassName = getter == "get_CurrentClassName" ? "" : null,
            ControlType = "Edit",
        };
        var selector = path switch
        {
            "metadata" => new UiSelector { ControlType = "Edit" },
            "slug" => new UiSelector { Slug = model.Selector, ClassName = "" },
            "root-slug" => new UiSelector { Root = new UiSelector { Slug = model.Selector }, ControlType = "Edit" },
            "root" or "root-self" => new UiSelector { Root = predicate, ControlType = "Edit" },
            _ => predicate,
        };

        var actual = await Assert.ThrowsExactlyAsync<COMException>(async () =>
        {
            if (path == "property") { await svc.GetPropertiesAsync(target, model, "ClassName", CancellationToken.None); }
            else if (path == "value") { await svc.GetTextAsync(target, model, CancellationToken.None); }
            else { await svc.SearchAsync(target, selector, 1, CancellationToken.None); }
        });
        Assert.AreSame(failure, actual);
        Assert.IsTrue(reads > 0);
    }

    [TestMethod]
    [DataRow(unchecked((int)0x80040201))]
    [DataRow(unchecked((int)0x80004005))]
    public async Task Query_RootMergeIdentityFailureCannotProveUniqueRoot(int hresult)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        await ResolveAsync(svc, target, "txtValue");
        var field = typeof(UiAutomationService).GetField("_automation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var automation = (IUIAutomation)field.GetValue(svc)!;
        var realElement = automation.ElementFromHandle(new(fx.OnUiThread(() => fx.ValueBox.Handle)));
        var failure = new COMException("Root merge identity failed.", hresult);
        var element = ComProxy<IUIAutomationElement>((method, args) =>
        {
            if (method.Name == "GetRuntimeId") { throw failure; }
            return method.Invoke(realElement, args);
        });
        UiAutomationService.s_findAllDescendants = (_, _) =>
            ComProxy<IUIAutomationElementArray>((method, _) => method.Name switch
            {
                "get_Length" => 1,
                "GetElement" => element,
                _ => throw new NotSupportedException(method.Name),
            });
        var actual = await Assert.ThrowsExactlyAsync<COMException>(() => svc.SearchAsync(target,
            new UiSelector { Root = new UiSelector { Query = "txtValue" }, ControlType = "Edit" },
            1, CancellationToken.None));
        Assert.AreSame(failure, actual);
    }

    [TestMethod]
    [DataRow("window", unchecked((int)0x80040201))]
    [DataRow("window", unchecked((int)0x80004005))]
    [DataRow("popup", unchecked((int)0x80040201))]
    [DataRow("popup", unchecked((int)0x80004005))]
    [DataRow("property", unchecked((int)0x80040201))]
    [DataRow("property", unchecked((int)0x80004005))]
    [DataRow("value", unchecked((int)0x80040201))]
    [DataRow("value", unchecked((int)0x80004005))]
    [DataRow("title", unchecked((int)0x80040201))]
    [DataRow("title", unchecked((int)0x80004005))]
    public async Task Query_WindowResolutionFailureCannotSelectAnotherWindow(string path, int hresult)
    {
        using var fx = new UiaTestFixture();
        using var other = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx, explicitWindow: path != "popup");
        await ResolveAsync(svc, target, "txtValue");
        var model = (await svc.SearchAsync(target,
            new UiSelector { Query = "txtValue", ControlType = "Edit" }, 1, CancellationToken.None)).Single();
        if (path is "property" or "value")
        {
            model.Context = null; // A retained provider does not need to resolve its window again.
        }
        var failure = new COMException("Window resolution failed.", hresult);
        var nativeFromHandle = UiAutomationService.s_elementFromHandle;
        if (path == "title")
        {
            target.WindowHandle = 0;
            target.WindowTitle = fx.Title;
            var nativeGetter = UiAutomationService.s_getCurrentBstr;
            UiAutomationService.s_getCurrentBstr = (element, property) =>
                property == UIA_PROPERTY_ID.UIA_NamePropertyId ? throw failure : nativeGetter(element, property);
        }
        else
        {
            UiAutomationService.s_elementFromHandle = (service, hwnd) =>
                path == "popup" && hwnd == fx.Hwnd ? nativeFromHandle(service, hwnd) : throw failure;
            UiAutomationService.s_getAllAppWindows = (_, _) => [(other.Hwnd, other.ProcessId, other.Title)];
        }
        var actual = await Assert.ThrowsExactlyAsync<COMException>(async () =>
        {
            if (path == "property") { await svc.GetPropertiesAsync(target, model, "ClassName", CancellationToken.None); }
            else if (path == "value") { await svc.GetTextAsync(target, model, CancellationToken.None); }
            else
            {
                await svc.SearchAsync(target, new UiSelector
                {
                    Query = "txtValue", ControlType = "Edit",
                    Root = path == "popup" ? new UiSelector { Query = "missing-root" } : null,
                }, 1, CancellationToken.None);
            }
        });
        Assert.AreSame(failure, actual);
    }

    [TestMethod]
    [DataRow(false, unchecked((int)0x80040201))]
    [DataRow(false, unchecked((int)0x80004005))]
    [DataRow(true, unchecked((int)0x80040201))]
    [DataRow(true, unchecked((int)0x80004005))]
    public void Query_MergeComparisonFailureIsStrictOnlyForConstrainedQueries(bool strict, int hresult)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var field = typeof(UiAutomationService).GetField("_automation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var automation = (IUIAutomation)field.GetValue(svc)!;
        var element = automation.ElementFromHandle(new(fx.Hwnd));
        var failure = new COMException("Identity comparison failed.", hresult);
        UiAutomationService.s_compareElements = (_, _, _) => throw failure;
        var contains = typeof(UiAutomationService).GetMethod("ContainsElement", BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (strict)
        {
            var actual = Assert.ThrowsExactly<TargetInvocationException>(() =>
                contains.Invoke(svc, [new List<IUIAutomationElement> { element }, element, strict]));
            Assert.AreSame(failure, actual.InnerException);
        }
        else
        {
            Assert.AreEqual(true, contains.Invoke(svc, [new List<IUIAutomationElement> { element }, element, strict]));
        }
    }

    [TestMethod]
    [DataRow("get_CurrentName")]
    [DataRow("get_CurrentAutomationId")]
    [DataRow("get_CurrentClassName")]
    public async Task Query_OmittedConstraintsKeepBestEffortStringMetadata(string getter)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        await ResolveAsync(svc, target, "txtValue");
        InstallQueryStringProvider(svc, fx, method =>
        {
            if (method == getter) { throw new COMException("Optional metadata unavailable."); }
        }, manualOnly: false);
        var matches = await svc.SearchAsync(target,
            new UiSelector { Query = "txtValue" }, 1, CancellationToken.None);
        Assert.HasCount(1, matches);
        Assert.IsNull(getter switch
        {
            "get_CurrentName" => matches[0].Name,
            "get_CurrentAutomationId" => matches[0].AutomationId,
            _ => matches[0].ClassName,
        });
        Assert.IsFalse(matches[0].RequiresCurrentIdentity);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Query_EmptyClassRemainsLiteralThroughSearchAndReads(bool manualOnly)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        await ResolveAsync(svc, target, "txtValue");
        InstallQueryStringProvider(svc, fx, _ => { }, manualOnly);

        var matches = await svc.SearchAsync(target,
            new UiSelector { Query = "query-id", ClassName = "" }, 1, CancellationToken.None);
        Assert.HasCount(1, matches);
        Assert.AreEqual("", matches[0].ClassName);
        var properties = await svc.GetPropertiesAsync(target, matches[0], "ClassName", CancellationToken.None);
        Assert.AreEqual("", properties["ClassName"]);
        Assert.AreEqual(fx.OnUiThread(() => fx.ValueBox.Text),
            await svc.GetTextAsync(target, matches[0], CancellationToken.None));
    }

    private static void InstallQueryStringProvider(UiAutomationService svc, UiaTestFixture fx,
        Action<string> beforeGetter, bool manualOnly)
    {
        var field = typeof(UiAutomationService).GetField("_automation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var automation = (IUIAutomation)field.GetValue(svc)!;
        var realRoot = automation.ElementFromHandle(new(fx.Hwnd));
        var element = automation.ElementFromHandle(new(fx.OnUiThread(() => fx.ValueBox.Handle)));
        var nativeGetter = UiAutomationService.s_getCurrentBstr;
        UiAutomationService.s_getCurrentBstr = (candidate, property) =>
        {
            if (!automation.CompareElements(element, candidate)) { return nativeGetter(candidate, property); }
            var method = property switch
            {
                UIA_PROPERTY_ID.UIA_NamePropertyId => "get_CurrentName",
                UIA_PROPERTY_ID.UIA_AutomationIdPropertyId => "get_CurrentAutomationId",
                UIA_PROPERTY_ID.UIA_ClassNamePropertyId => "get_CurrentClassName",
                _ => throw new NotSupportedException(property.ToString()),
            };
            beforeGetter(method);
            return QueryBstr(property switch
            {
                UIA_PROPERTY_ID.UIA_NamePropertyId => "query-name",
                UIA_PROPERTY_ID.UIA_AutomationIdPropertyId => "query-id",
                _ => "",
            });
        };
        UiAutomationService.s_getRootElement = (_, _, _) => realRoot;
        UiAutomationService.s_findAllDescendants = (_, _) => manualOnly ? null :
            ComProxy<IUIAutomationElementArray>((method, _) => method.Name switch
            {
                "get_Length" => 1,
                "GetElement" => element,
                _ => throw new NotSupportedException(method.Name),
            });
        UiAutomationService.s_getControlViewWalker = _ =>
            ComProxy<IUIAutomationTreeWalker>((method, args) => method.Name switch
            {
                "GetFirstChildElement" => ReferenceEquals(args![0], realRoot) ? element : null,
                "GetNextSiblingElement" => null,
                "GetParentElement" => ReferenceEquals(args![0], element) ? realRoot : null,
                _ => throw new NotSupportedException(method.Name),
            });
        UiAutomationService.s_findInvokableAncestor = (_, _, _) => null;
    }

    private static unsafe BSTR QueryBstr(string value) => new((char*)Marshal.StringToBSTR(value));
}
