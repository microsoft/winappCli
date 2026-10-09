// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    public static IEnumerable<object[]> QueryGeneralPropertyFailures()
    {
        foreach (var property in new[] { "HasKeyboardFocus", "IsKeyboardFocusable", "AcceleratorKey", "AccessKey", "HelpText", "IsPassword" })
        {
            foreach (var strict in new[] { false, true })
            {
                foreach (var hresult in new[] { unchecked((int)0x80040201), unchecked((int)0x80004005) })
                {
                    yield return [property, strict, hresult];
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(QueryGeneralPropertyFailures))]
    public async Task Query_GeneralPropertyFailureIsStrictOnlyForConstrainedReads(string property, bool strict, int hresult)
    {
        var svc = NewService();
        var reads = 0;
        var failure = new COMException("General property getter failed.", hresult);
        var getter = $"get_Current{property}";
        var element = new UiElement
        {
            Type = "Edit",
            RequiresCurrentIdentity = strict,
            Context = new UiElementContext(ComProxy<IUIAutomationElement>((method, _) =>
            {
                if (method.Name == getter) { reads++; throw failure; }
                return method.Name switch
                {
                    "get_CurrentProcessId" => Environment.ProcessId,
                    "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_EditControlTypeId,
                    "get_CurrentAcceleratorKey" or "get_CurrentAccessKey" or "get_CurrentHelpText" => default(BSTR),
                    "GetCurrentPattern" => throw new COMException("Pattern unsupported.", unchecked((int)0x80040204)),
                    _ => new BOOL(false),
                };
            })),
        };

        if (strict)
        {
            Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<COMException>(
                () => svc.GetPropertiesAsync(new UiTarget(), element, property, default)));
        }
        else
        {
            Assert.IsNull((await svc.GetPropertiesAsync(new UiTarget(), element, property, default))[property]);
        }
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Query_IsPasswordComesFromProviderNotEditContentHeuristic(bool providerIsPassword)
    {
        var element = new UiElement
        {
            Type = "Edit",
            RequiresCurrentIdentity = true,
            Context = new UiElementContext(ComProxy<IUIAutomationElement>((method, _) => method.Name switch
            {
                "get_CurrentProcessId" => Environment.ProcessId,
                "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_EditControlTypeId,
                "get_CurrentIsContentElement" => new BOOL(true),
                "get_CurrentIsPassword" => new BOOL(providerIsPassword),
                "get_CurrentAcceleratorKey" or "get_CurrentAccessKey" or "get_CurrentHelpText" => default(BSTR),
                "GetCurrentPattern" => throw new COMException("Pattern unsupported.", unchecked((int)0x80040204)),
                _ => new BOOL(false),
            })),
        };

        var props = await NewService().GetPropertiesAsync(new UiTarget(), element, "IsPassword", default);

        Assert.AreEqual(providerIsPassword, props["IsPassword"]);
    }

    [TestMethod]
    public async Task Query_IsPasswordMatchesRealOrdinaryAndPasswordTextBoxes()
    {
        const string secret = "Fixture-Secret-694";
        using var fx = new UiaTestFixture(nonActivating: true);
        var passwordBox = fx.OnUiThread(() =>
        {
            var box = new System.Windows.Forms.TextBox
            {
                Name = "txtPassword",
                AccessibleName = "Password",
                Left = 10,
                Top = 600,
                Width = 200,
                UseSystemPasswordChar = true,
                Text = secret,
            };
            fx.Form.Controls.Add(box);
            return box;
        });
        var automation = CUIAutomation8.CreateInstance<IUIAutomation>();
        var nativeOrdinary = automation.ElementFromHandle(new HWND(fx.HandleOf(fx.ValueBox)));
        Assert.IsTrue((bool)nativeOrdinary.get_CurrentIsContentElement(), "The ordinary box must be a content Edit to exercise the old heuristic.");
        Assert.IsFalse((bool)nativeOrdinary.get_CurrentIsPassword());
        Assert.IsTrue((bool)automation.ElementFromHandle(new HWND(fx.HandleOf(passwordBox))).get_CurrentIsPassword());

        var svc = NewService();
        var target = SessionFor(fx);
        var ordinary = await ResolveAsync(svc, target, "txtValue");
        var password = await ResolveAsync(svc, target, "txtPassword");
        var ordinaryProps = await svc.GetPropertiesAsync(target, ordinary, null, default);
        var passwordProps = await svc.GetPropertiesAsync(target, password, null, default);

        Assert.AreEqual(false, ordinaryProps["IsPassword"]);
        Assert.AreEqual(true, passwordProps["IsPassword"]);
        Assert.AreEqual(true, (await svc.GetPropertiesAsync(target, password, "IsPassword", default))["IsPassword"]);
        Assert.IsFalse(passwordProps.Values.Any(v => v?.ToString()?.Contains(secret, StringComparison.Ordinal) == true),
            "A password control's text must never be exposed through its properties.");
    }

    [TestMethod]
    [DataRow("AcceleratorKey", false)]
    [DataRow("AcceleratorKey", true)]
    [DataRow("AccessKey", false)]
    [DataRow("AccessKey", true)]
    [DataRow("HelpText", false)]
    [DataRow("HelpText", true)]
    public async Task Query_GeneralStringNullAndEmptyBstrRemainEmpty(string property, bool allocated)
    {
        var element = new UiElement
        {
            Type = "Edit",
            RequiresCurrentIdentity = true,
            Context = new UiElementContext(ComProxy<IUIAutomationElement>((method, _) => method.Name switch
            {
                "get_CurrentProcessId" => Environment.ProcessId,
                "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_EditControlTypeId,
                "get_CurrentAcceleratorKey" or "get_CurrentAccessKey" or "get_CurrentHelpText" =>
                    allocated ? QueryBstr("") : default(BSTR),
                "GetCurrentPattern" => throw new COMException("Pattern unsupported.", unchecked((int)0x80040204)),
                _ => new BOOL(false),
            })),
        };
        Assert.AreEqual("", (await NewService().GetPropertiesAsync(new UiTarget(), element, property, default))[property]);
    }

    [TestMethod]
    [DataRow("AcceleratorKey", unchecked((int)0x80040201))]
    [DataRow("AcceleratorKey", unchecked((int)0x80004005))]
    [DataRow("AccessKey", unchecked((int)0x80040201))]
    [DataRow("AccessKey", unchecked((int)0x80004005))]
    [DataRow("HelpText", unchecked((int)0x80040201))]
    [DataRow("HelpText", unchecked((int)0x80004005))]
    public async Task Query_JsonRoundTripGeneralPropertyFailureRemainsStrict(string property, int hresult)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var element = await svc.FindSingleElementAsync(target,
            new UiSelector { Query = "txtValue", ControlType = "Edit" }, default);
        Assert.IsNotNull(element);
        var json = JsonSerializer.Serialize(element, QueryElementJsonContext.Default.UiElement);
        element = JsonSerializer.Deserialize(json, QueryElementJsonContext.Default.UiElement)!;
        Assert.IsNull(element.Context);
        Assert.IsFalse(element.RequiresCurrentIdentity);
        var nativeGetter = UiAutomationService.s_getCurrentBstr;
        var reads = 0;
        var failure = new COMException("Reconstructed property getter failed.", hresult);
        UiAutomationService.s_getCurrentBstr = (provider, requested) =>
        {
            if (requested.ToString() == $"UIA_{property}PropertyId") { reads++; throw failure; }
            return nativeGetter(provider, requested);
        };

        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<COMException>(
            () => svc.GetPropertiesAsync(target, element, property, default)));
        Assert.IsTrue(element.RequiresCurrentIdentity);
        Assert.AreEqual(1, reads);
        Assert.AreEqual(1, svc.SerializedElementResolutionCount);
    }
}
