// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
public sealed class UiAuditFieldTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Boolean_ExplicitFalseAndTrueRemainValues(bool expected)
    {
        using var native = ComVariant.Create(expected);
        var field = UiAuditFieldReader.Read(native, UiAuditFieldKind.Boolean);

        Assert.AreEqual("value", field.Status);
        Assert.AreEqual((bool?)expected, field.Boolean);
        Assert.IsNull(field.Number);
        Assert.AreEqual("VT_BOOL", field.NativeType);
        Assert.IsNull(field.Error);
    }

    [TestMethod]
    public void Boolean_UnavailableIsNotFalse()
    {
        using var empty = default(ComVariant);
        using var nil = ComVariant.Null;
        foreach (var native in new[] { empty, nil })
        {
            var field = UiAuditFieldReader.Read(native, UiAuditFieldKind.Boolean);
            Assert.AreEqual("unsupported", field.Status);
            Assert.IsNull(field.Boolean);
            Assert.IsNull(field.Number);
        }
    }

    [TestMethod]
    public void Boolean_InvalidVariantBooleanAndIntegerAreErrors()
    {
        using var malformed = ComVariant.CreateRaw(VarEnum.VT_BOOL, (short)1);
        using var integer = ComVariant.Create(0);
        foreach (var native in new[] { malformed, integer })
        {
            var field = UiAuditFieldReader.Read(native, UiAuditFieldKind.Boolean);
            Assert.AreEqual("error", field.Status);
            Assert.IsNull(field.Boolean);
            Assert.IsNotNull(field.Error);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(0x000000ff)]
    [DataRow(0x00ff0000)]
    [DataRow(0x00112233)]
    [DataRow(0x00ffffff)]
    public void Colors_PreserveRawColorrefWithoutRgbReordering(int colorref)
    {
        using var signed = ComVariant.Create(colorref);
        using var unsigned = ComVariant.Create((uint)colorref);
        foreach (var native in new[] { signed, unsigned })
        {
            var field = UiAuditFieldReader.Read(native, UiAuditFieldKind.Color);
            Assert.AreEqual("value", field.Status);
            Assert.AreEqual((double?)colorref, field.Number);
            Assert.IsNull(field.Boolean);
        }
    }

    [TestMethod]
    public void Colors_UnsupportedOrWrongNativeTypeNeverBecomeBlack()
    {
        using var empty = default(ComVariant);
        using var numericString = ComVariant.Create("0");
        using var floatingPoint = ComVariant.Create(0d);
        using var negative = ComVariant.Create(-1);
        using var alphaBits = ComVariant.Create(0x01000000);
        foreach (var native in new[] { empty, numericString, floatingPoint, negative, alphaBits })
        {
            var field = UiAuditFieldReader.Read(native, UiAuditFieldKind.Color);
            Assert.AreNotEqual("value", field.Status);
            Assert.IsNull(field.Number);
            Assert.IsNull(field.Boolean);
        }
    }

    [TestMethod]
    public void FontSize_RemainsPointsAndRequiresTheDocumentedDoubleType()
    {
        using var points = ComVariant.Create(12.5d);
        using var integer = ComVariant.Create(12);
        var field = UiAuditFieldReader.Read(points, UiAuditFieldKind.FontSize);

        Assert.AreEqual("value", field.Status);
        Assert.AreEqual((double?)12.5, field.Number);
        Assert.AreEqual("VT_R8", field.NativeType);
        Assert.AreEqual("error", UiAuditFieldReader.Read(integer, UiAuditFieldKind.FontSize).Status);
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(0d)]
    [DataRow(-1d)]
    public void FontSize_InvalidValuesCannotEnterJson(double points)
    {
        using var native = ComVariant.Create(points);
        var field = UiAuditFieldReader.Read(native, UiAuditFieldKind.FontSize);

        Assert.AreEqual("error", field.Status);
        Assert.IsNull(field.Number);
    }

    [TestMethod]
    public void FontWeight_IsAnIntegerAttributeNotAStringOrBoolean()
    {
        using var native = ComVariant.Create(700);
        using var text = ComVariant.Create("700");
        using var boolean = ComVariant.Create(true);

        Assert.AreEqual((double?)700, UiAuditFieldReader.Read(native, UiAuditFieldKind.FontWeight).Number);
        Assert.AreEqual("error", UiAuditFieldReader.Read(text, UiAuditFieldKind.FontWeight).Status);
        Assert.AreEqual("error", UiAuditFieldReader.Read(boolean, UiAuditFieldKind.FontWeight).Status);
    }

    [TestMethod]
    public void ErrorVariant_PreservesHresultWithoutAValue()
    {
        using var native = ComVariant.CreateRaw(VarEnum.VT_ERROR, unchecked((int)0x80004005));
        var field = UiAuditFieldReader.Read(native, UiAuditFieldKind.Color);

        Assert.AreEqual("error", field.Status);
        Assert.AreEqual("VT_ERROR", field.NativeType);
        Assert.IsNotNull(field.Error);
        StringAssert.Contains(field.Error, "0x80004005");
        Assert.IsNull(field.Number);
    }

    [TestMethod]
    public void WithholdingIsDistinctFromUnsupportedAndError()
    {
        var field = UiAuditFieldReader.Withheld();

        Assert.AreEqual("withheld", field.Status);
        Assert.IsNull(field.Number);
        Assert.IsNull(field.Boolean);
        Assert.IsNull(field.NativeType);
        Assert.IsNull(field.Error);
        Assert.AreEqual("unsupported", new UiAuditField().Status);
        Assert.AreEqual("error", UiAuditFieldReader.Failure("Read failed.").Status);
    }
}
