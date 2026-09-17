// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    // Deliberately independent of the product's table: omissions/renames must fail these tests.
    private static readonly (string Name, UIA_TEXTATTRIBUTE_ID Id, string Uniform)[] ExpectedTextAttributes =
    [
        ("FontWeight", UIA_TEXTATTRIBUTE_ID.UIA_FontWeightAttributeId, "700"),
        ("FontName", UIA_TEXTATTRIBUTE_ID.UIA_FontNameAttributeId, "Courier New"),
        ("FontSize", UIA_TEXTATTRIBUTE_ID.UIA_FontSizeAttributeId, "15.5"),
        ("ForegroundColor", UIA_TEXTATTRIBUTE_ID.UIA_ForegroundColorAttributeId, "3678732"),
        ("IsItalic", UIA_TEXTATTRIBUTE_ID.UIA_IsItalicAttributeId, "True"),
        ("StrikethroughStyle", UIA_TEXTATTRIBUTE_ID.UIA_StrikethroughStyleAttributeId, "1"),
    ];

    [TestMethod]
    public async Task TextAttributes_UniformRichEdit_FullListAndSingleProperties()
    {
        using var fx = new UiaTestFixture(nonActivating: true);
        var document = fx.AddTextAttributesDocument();
        var svc = NewService();
        var target = SessionFor(fx);
        var model = await ResolveAsync(svc, target, document.Name);
        var automation = CUIAutomation8.CreateInstance<IUIAutomation>();
        var native = automation.ElementFromHandle(new HWND(fx.HandleOf(document)));
        using var availability = native.GetCurrentPropertyValue(UIA_PROPERTY_ID.UIA_IsTextPatternAvailablePropertyId);
        Assert.IsTrue(availability.As<bool>(), "RichEdit must expose an actual TextPattern.");
        var pattern = (IUIAutomationTextPattern)native.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextPatternId);
        var range = pattern.get_DocumentRange();
        Console.WriteLine($"Real provider: {native.get_CurrentClassName()}; HWND={fx.HandleOf(document)}");

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var all = await svc.GetPropertiesAsync(target, model, null, CancellationToken.None);
            foreach (var (name, id, expected) in ExpectedTextAttributes)
            {
                using var raw = range.GetAttributeValue(id);
                Console.WriteLine($"{name}: native VARIANT={raw.VarType}; service={all[name]}");
                Assert.AreEqual(expected, all[name], name);
                var single = await svc.GetPropertiesAsync(target, model, name, CancellationToken.None);
                Assert.AreEqual(1, single.Count, name);
                Assert.AreEqual(expected, single[name], name);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
        Assert.IsFalse(fx.WasActivated, "Text attribute reads must not activate the fixture.");
    }

    [TestMethod]
    public async Task TextAttributes_PlainRichEdit_FalseAndZeroRemainInvariantStrings()
    {
        using var fx = new UiaTestFixture(nonActivating: true);
        var document = fx.AddTextAttributesDocument();
        fx.OnUiThread(() =>
        {
            document.SelectAll();
            using var font = new System.Drawing.Font("Arial", 11.5f, System.Drawing.FontStyle.Regular);
            document.SelectionFont = font;
            document.SelectionColor = System.Drawing.Color.Black;
            document.Select(12, 1);
        });
        var svc = NewService();
        var target = SessionFor(fx);
        var model = await ResolveAsync(svc, target, document.Name);
        var all = await svc.GetPropertiesAsync(target, model, null, CancellationToken.None);
        foreach (var (name, expected) in new[]
        {
            ("FontWeight", "400"), ("FontName", "Arial"), ("FontSize", "11.5"),
            ("ForegroundColor", "0"), ("IsItalic", "False"), ("StrikethroughStyle", "0"),
        })
        {
            Assert.AreEqual(expected, all[name], name);
            var single = await svc.GetPropertiesAsync(target, model, name, CancellationToken.None);
            Assert.AreEqual(1, single.Count);
            Assert.AreEqual(expected, single[name], name);
            Console.WriteLine($"Plain RichEdit {name}={single[name]}");
        }
        Assert.IsFalse(fx.WasActivated, "Text attribute reads must not activate the fixture.");
    }

    [TestMethod]
    public async Task TextAttributes_MixedRichEdit_UsesWholeDocumentNotSelection()
    {
        using var fx = new UiaTestFixture(nonActivating: true);
        var document = fx.AddTextAttributesDocument(mixed: true);
        var svc = NewService();
        var target = SessionFor(fx);
        var model = await ResolveAsync(svc, target, document.Name);
        var automation = CUIAutomation8.CreateInstance<IUIAutomation>();
        var native = automation.ElementFromHandle(new HWND(fx.HandleOf(document)));
        var pattern = (IUIAutomationTextPattern)native.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextPatternId);
        var range = pattern.get_DocumentRange();

        // Both selections have uniform formatting, but the document spans two different runs.
        foreach (var selectionStart in new[] { 0, 12 })
        {
            fx.OnUiThread(() => document.Select(selectionStart, 1));
            var selections = pattern.GetSelection();
            Assert.AreEqual(1, selections.get_Length());
            using var selectedWeight = selections.GetElement(0).GetAttributeValue(UIA_TEXTATTRIBUTE_ID.UIA_FontWeightAttributeId);
            Assert.AreEqual(selectionStart == 0 ? 700 : 400, selectedWeight.As<int>());

            var all = await svc.GetPropertiesAsync(target, model, null, CancellationToken.None);
            foreach (var (name, id, _) in ExpectedTextAttributes)
            {
                using var raw = range.GetAttributeValue(id);
                AssertReservedTextToken(raw, automation.get_ReservedMixedAttributeValue(), name);
                Assert.AreEqual("Mixed", all[name], name);
                var single = await svc.GetPropertiesAsync(target, model, name, CancellationToken.None);
                Assert.AreEqual(1, single.Count);
                Assert.AreEqual("Mixed", single[name], name);
                Console.WriteLine($"{name}: native VT_UNKNOWN == UIA ReservedMixedAttributeValue; selection={selectionStart}; service=Mixed");
            }
        }
        Assert.IsFalse(fx.WasActivated, "Changing the fixture selection must not activate it.");
    }

    [TestMethod]
    public async Task TextAttributes_NoTextPattern_UnavailableAndExistingPropertiesUnchanged()
    {
        using var fx = new UiaTestFixture(nonActivating: true);
        var svc = NewService();
        var target = SessionFor(fx);
        var button = await ResolveAsync(svc, target, "btnInvoke");
        var automation = CUIAutomation8.CreateInstance<IUIAutomation>();
        var native = automation.ElementFromHandle(new HWND(fx.HandleOf(fx.InvokeButton)));
        using var available = native.GetCurrentPropertyValue(UIA_PROPERTY_ID.UIA_IsTextPatternAvailablePropertyId);
        Assert.IsFalse(available.As<bool>(), "A Button is the real no-TextPattern provider.");
        var all = await svc.GetPropertiesAsync(target, button, null, CancellationToken.None);
        foreach (var (name, _, _) in ExpectedTextAttributes)
        {
            Assert.AreEqual("Unavailable", all[name], name);
            var single = await svc.GetPropertiesAsync(target, button, name, CancellationToken.None);
            Assert.AreEqual(1, single.Count);
            Assert.AreEqual("Unavailable", single[name], name);
        }

        var box = await ResolveAsync(svc, target, "txtValue");
        var boxProperties = await svc.GetPropertiesAsync(target, box, null, CancellationToken.None);
        Assert.AreEqual("Value", boxProperties["Name"]);
        Assert.AreEqual("initial", boxProperties["Value"]);
        Assert.AreEqual($"{box.X},{box.Y},{box.Width},{box.Height}", boxProperties["BoundingRectangle"]);
        foreach (var name in new[] { "Name", "Value", "BoundingRectangle" })
        {
            Assert.IsInstanceOfType<string>(boxProperties[name]);
            var single = await svc.GetPropertiesAsync(target, box, name, CancellationToken.None);
            Assert.AreEqual(1, single.Count);
            Assert.AreEqual(boxProperties[name], single[name], name);
        }
        Console.WriteLine("Real WinForms Button: IsTextPatternAvailable=False; all six attributes=Unavailable.");
        Assert.IsFalse(fx.WasActivated, "Text attribute reads must not activate the fixture.");
    }

    [TestMethod]
    public async Task TextAttributes_RealCustomProvider_ReservedNotSupportedToken()
    {
        using var fx = new UiaTestFixture(nonActivating: true);
        var document = fx.AddUnsupportedTextDocument();
        var svc = NewService();
        var target = SessionFor(fx);
        var model = await ResolveAsync(svc, target, document.Name);
        var automation = CUIAutomation8.CreateInstance<IUIAutomation>();
        var native = automation.ElementFromHandle(new HWND(fx.HandleOf(document)));
        using var availability = native.GetCurrentPropertyValue(UIA_PROPERTY_ID.UIA_IsTextPatternAvailablePropertyId);
        Assert.IsTrue(availability.As<bool>(), "NotSupported requires a real available TextPattern.");
        var pattern = (IUIAutomationTextPattern)native.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextPatternId);
        var range = pattern.get_DocumentRange();
        var all = await svc.GetPropertiesAsync(target, model, null, CancellationToken.None);
        foreach (var (name, id, _) in ExpectedTextAttributes)
        {
            using var raw = range.GetAttributeValue(id);
            AssertReservedTextToken(raw, automation.get_ReservedNotSupportedValue(), name);
            Assert.AreEqual("NotSupported", all[name], name);
            var single = await svc.GetPropertiesAsync(target, model, name, CancellationToken.None);
            Assert.AreEqual(1, single.Count);
            Assert.AreEqual("NotSupported", single[name], name);
            Console.WriteLine($"{name}: native VT_UNKNOWN == UIA ReservedNotSupportedValue; service=NotSupported");
        }
        Assert.IsGreaterThanOrEqualTo(18, document.AttributeReadCount,
            "Six full-list, six direct COM, and six single-property reads must reach the real window provider.");
        Console.WriteLine($"WM_GETOBJECT provider: {native.get_CurrentClassName()}; attribute calls={document.AttributeReadCount}");
        Assert.IsFalse(fx.WasActivated, "Text attribute reads must not activate the fixture.");
    }

    private static unsafe void AssertReservedTextToken(ComVariant value, object token, string name)
    {
        Assert.AreEqual(VarEnum.VT_UNKNOWN, value.VarType, name);
        var iid = new Guid("00000000-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(value.GetRawDataRef<nint>(), in iid, out var identity));
        try
        {
            var reservedIdentity = ComInterfaceMarshaller<object>.ConvertToUnmanaged(token);
            try { Assert.AreEqual((nint)reservedIdentity, identity, $"{name}: canonical IUnknown token identity"); }
            finally { ComInterfaceMarshaller<object>.Free(reservedIdentity); }
        }
        finally { Marshal.Release(identity); }
    }
}
