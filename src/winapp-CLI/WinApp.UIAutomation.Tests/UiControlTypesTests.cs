// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
public class UiControlTypesTests
{
    [TestMethod]
    public void AllOfficialTypes_RoundTripCaseInsensitively()
    {
        var types = Enum.GetValues<UIA_CONTROLTYPE_ID>();
        Assert.HasCount(41, types);
        foreach (var type in types)
        {
            var name = type.ToString()["UIA_".Length..^"ControlTypeId".Length];
            Assert.AreEqual((int)type, UiControlTypes.GetId(name));
            Assert.AreEqual((int)type, UiControlTypes.GetId(name.ToLowerInvariant()));
            Assert.AreEqual((int)type, UiControlTypes.GetId(name.ToUpperInvariant()));
            Assert.AreEqual((int)type, UiAutomationService.MapControlType(name.ToLowerInvariant()));
            Assert.AreEqual(name, UiAutomationService.GetControlTypeName(type));
        }
    }

    [TestMethod]
    [DataRow("TextBox", 50004)]
    [DataRow("tExTbLoCk", 50020)]
    [DataRow("button", 50000)]
    [DataRow("50000", 0)]
    [DataRow("UIA_ButtonControlTypeId", 0)]
    [DataRow("Button*", 0)]
    [DataRow("TextField", 0)]
    [DataRow("Button ", 0)]
    [DataRow("", 0)]
    public void OnlyDocumentedTypesAndAliases(string name, int id) =>
        Assert.AreEqual(id, UiControlTypes.GetId(name));
}
