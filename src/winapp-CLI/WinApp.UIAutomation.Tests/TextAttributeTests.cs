// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class TextAttributeTests
{
    private static readonly string[] AttributeNames =
        ["FontWeight", "FontName", "FontSize", "ForegroundColor", "IsItalic", "StrikethroughStyle"];

    [TestCleanup]
    public void ResetSeams() => UiAutomationService.ResetNativeSeams();

    [TestMethod]
    public void FormatTextAttribute_ValuesAreInvariantStrings()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            foreach (var (value, expected) in new (ComVariant, string)[]
            {
                (ComVariant.Create(700), "700"), (ComVariant.Create("Courier New"), "Courier New"),
                (ComVariant.Create(15.5), "15.5"), (ComVariant.Create(3678732), "3678732"),
                (ComVariant.Create(true), "True"), (ComVariant.Create(false), "False"),
                (ComVariant.Create(0), "0"), (ComVariant.Create(1), "1"),
            })
            {
                using var variant = value;
                Assert.AreEqual(expected, UiAutomationService.FormatTextAttribute(variant, new object(), new object()));
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [TestMethod]
    public unsafe void FormatTextAttribute_RecognizesActualReservedComTokens()
    {
        var automation = CUIAutomation8.CreateInstance<IUIAutomation>();
        var mixed = automation.get_ReservedMixedAttributeValue();
        var unsupported = automation.get_ReservedNotSupportedValue();
        foreach (var (token, expected) in new[] { (mixed, "Mixed"), (unsupported, "NotSupported") })
        {
            using var variant = ComVariant.CreateRaw(VarEnum.VT_UNKNOWN,
                (nint)ComInterfaceMarshaller<object>.ConvertToUnmanaged(token));
            Assert.AreEqual(VarEnum.VT_UNKNOWN, variant.VarType);
            Assert.AreEqual(expected, UiAutomationService.FormatTextAttribute(variant, mixed, unsupported));
        }
    }

    [TestMethod]
    public void FormatTextAttribute_NullBstrIsEmptyString()
    {
        using var variant = ComVariant.CreateRaw(VarEnum.VT_BSTR, nint.Zero);
        Assert.AreEqual(string.Empty, UiAutomationService.FormatTextAttribute(variant, new object(), new object()));
    }

    [TestMethod]
    public void FormatTextAttribute_RejectsUnexpectedVariant()
    {
        using var variant = ComVariant.Create(1L);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            UiAutomationService.FormatTextAttribute(variant, new object(), new object()));
    }

    [TestMethod]
    public async Task GetProperties_AllWithoutLiveElement_PreservesCachedBasics()
    {
        UiAutomationService.s_getRootElement = (_, _, _) => null;
        var service = new UiAutomationService(NullLogger<UiAutomationService>.Instance, new UiSelectorParser());
        var model = new UiElement { Name = "Cached document" };
        var all = await service.GetPropertiesAsync(new UiTarget(), model, null, CancellationToken.None);
        Assert.AreEqual(7, all.Count);
        Assert.AreEqual("Cached document", all["Name"]);
        foreach (var name in AttributeNames)
        {
            Assert.IsFalse(all.ContainsKey(name));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.GetPropertiesAsync(new UiTarget(), model, name, CancellationToken.None));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetProperties_AllMalformedAttribute_PreservesOtherValues(bool foreignToken)
    {
        var automation = CUIAutomation8.CreateInstance<IUIAutomation>();
        var range = Proxy<IUIAutomationTextRange>((_, args) =>
            (UIA_TEXTATTRIBUTE_ID)args![0]! == UIA_TEXTATTRIBUTE_ID.UIA_FontWeightAttributeId
                ? foreignToken ? UnknownVariant(automation) : ComVariant.Create(700L)
                : ComVariant.Create("valid"));
        var pattern = Proxy<IUIAutomationTextPattern>((_, _) => range);
        var service = ServiceWithElement((method, args) => method.Name switch
        {
            "GetCurrentPropertyValue" => ComVariant.Create(true),
            "GetCurrentPattern" when (UIA_PATTERN_ID)args![0]! == UIA_PATTERN_ID.UIA_TextPatternId => pattern,
            _ => throw new COMException(),
        });
        var model = new UiElement { AutomationId = "document", Name = "Cached document" };
        var all = await service.GetPropertiesAsync(new UiTarget(), model, null, CancellationToken.None);
        Assert.AreEqual("Cached document", all["Name"]);
        Assert.IsFalse(all.ContainsKey("FontWeight"));
        foreach (var name in AttributeNames.Skip(1))
        {
            Assert.AreEqual("valid", all[name]);
        }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.GetPropertiesAsync(new UiTarget(), model, "FontWeight", CancellationToken.None));
    }

    private static unsafe ComVariant UnknownVariant(object token) =>
        ComVariant.CreateRaw(VarEnum.VT_UNKNOWN, (nint)ComInterfaceMarshaller<object>.ConvertToUnmanaged(token));

    [TestMethod]
    public async Task GetProperties_NoPattern_ReturnsUnavailableInFullAndSingleResults()
    {
        var service = ServiceWithElement((method, _) => method.Name == "GetCurrentPropertyValue"
            ? ComVariant.Create(false) : throw new COMException());
        var target = new UiTarget();
        var model = new UiElement { AutomationId = "document", Name = "Document" };
        var all = await service.GetPropertiesAsync(target, model, null, CancellationToken.None);
        Assert.AreEqual("Document", all["Name"]);
        foreach (var name in AttributeNames)
        {
            Assert.AreEqual("Unavailable", all[name]);
            var single = await service.GetPropertiesAsync(target, model, name, CancellationToken.None);
            Assert.AreEqual(1, single.Count);
            Assert.AreEqual("Unavailable", single[name]);
        }
    }

    [TestMethod]
    [DataRow("DefinitelyMissing")]
    [DataRow("fontweight")]
    [DataRow("")]
    public async Task GetProperties_UnknownName_IsRejectedBeforeProviderAccess(string name)
    {
        var service = ServiceWithElement((_, _) => throw new AssertFailedException("Provider must not be queried."));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.GetPropertiesAsync(new UiTarget(), new UiElement(), name, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("availability")]
    [DataRow("pattern")]
    [DataRow("range")]
    [DataRow("attribute")]
    public async Task GetProperties_ProviderFailuresPropagate(string stage)
    {
        var failure = new COMException("provider failure", unchecked((int)0x80040201));
        var range = Proxy<IUIAutomationTextRange>((_, _) => throw failure);
        var pattern = Proxy<IUIAutomationTextPattern>((_, _) => stage == "range" ? throw failure : range);
        var service = ServiceWithElement((method, args) => method.Name switch
        {
            "GetCurrentPropertyValue" => stage == "availability" ? throw failure : ComVariant.Create(true),
            "GetCurrentPattern" when (UIA_PATTERN_ID)args![0]! == UIA_PATTERN_ID.UIA_TextPatternId =>
                stage == "pattern" ? throw failure : pattern,
            _ => throw new COMException(),
        });
        foreach (var name in new string?[] { null, "FontWeight" })
        {
            var actual = await Assert.ThrowsExactlyAsync<COMException>(() =>
                service.GetPropertiesAsync(new UiTarget(), new UiElement { AutomationId = "document" }, name, CancellationToken.None));
            Assert.AreSame(failure, actual);
        }
        // An unrelated scalar request must not start a TextPattern read.
        var scalar = await service.GetPropertiesAsync(new UiTarget(), new UiElement { Name = "Document", AutomationId = "document" }, "Name", CancellationToken.None);
        Assert.AreEqual("Document", scalar["Name"]);
    }

    private static UiAutomationService ServiceWithElement(Func<MethodInfo, object?[]?, object?> handler)
    {
        var element = Proxy<IUIAutomationElement>(handler);
        var root = Proxy<IUIAutomationElement>((method, _) => method.Name == "FindFirst" ? element : throw new COMException());
        UiAutomationService.s_getRootElement = (_, _, _) => root;
        return new UiAutomationService(NullLogger<UiAutomationService>.Instance, new UiSelectorParser());
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, AttributeProxy>();
        ((AttributeProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private class AttributeProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
