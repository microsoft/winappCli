// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers <see cref="MsBuildPropertyValidator.Validate"/>, which gates every <c>-p</c> reaching MSBuild
/// from <c>run</c> and <c>unregister</c>.
/// </summary>
[TestClass]
public class MsBuildPropertyValidatorTests
{
    [TestMethod]
    [DataRow("WindowsPackageType=None", DisplayName = "simple Name=Value")]
    [DataRow("WinAppPackageName=com.contoso.counter", DisplayName = "dotted value")]
    [DataRow("Description=a value with spaces", DisplayName = "spaces in the value")]
    [DataRow("DefineConstants=A%3BB", DisplayName = "escaped semicolon in the value")]
    [DataRow("Version=", DisplayName = "empty value clears a property")]
    [DataRow("Path=C:\\a=b", DisplayName = "'=' inside the value")]
    public void Validate_WellFormedProperty_IsAccepted(string property)
    {
        Assert.IsNull(MsBuildPropertyValidator.Validate([property]));
    }

    [TestMethod]
    public void Validate_NoProperties_IsAccepted()
    {
        Assert.IsNull(MsBuildPropertyValidator.Validate([]));
    }

    [TestMethod]
    public void Validate_PackedProperties_AreRejected()
    {
        // MSBuild splits a -p token on ';' into multiple properties, which would smuggle a property that
        // has its own dedicated flag past the name-only forwarding filter and override the architecture
        // winapp conveys through the RID.
        var error = MsBuildPropertyValidator.Validate(["A=1;RuntimeIdentifier=win-arm64"]);

        Assert.IsNotNull(error);
        Assert.Contains("'A'", error, "The message should name the offending property");
        Assert.Contains("%3B", error, "The message should name the escape for a literal ';'");
    }

    [TestMethod]
    public void Validate_CommaPackedProperties_AreRejected()
    {
        // MSBuild splits on ',' as well as ';'. A publish-profile value like 'win-arm64,Extra=true' would
        // otherwise become two properties silently — which is why the escaped form '%2C' exists.
        var error = MsBuildPropertyValidator.Validate(["PublishProfile=win-arm64,Extra=true"]);

        Assert.IsNotNull(error);
        Assert.Contains("'PublishProfile'", error);
        Assert.Contains("%2C", error, "The message should name the escape for a literal ','");
    }

    [TestMethod]
    public void Validate_EscapedSeparatorsInAValue_AreAccepted()
    {
        // The percent-escaped forms are how a literal separator is carried inside one value.
        Assert.IsNull(MsBuildPropertyValidator.Validate(["PublishProfile=win-arm64%2CExtra=true"]));
        Assert.IsNull(MsBuildPropertyValidator.Validate(["DefineConstants=A%3BB"]));
    }

    [TestMethod]
    public void Validate_PackedPropertyWithNoEquals_NamesTheLeadingSegment()
    {
        // IndexOfAny finds the ';' before any '=', so the name must still be extracted, not throw.
        var error = MsBuildPropertyValidator.Validate(["A;B"]);

        Assert.IsNotNull(error);
        Assert.Contains("'A'", error);
    }

    [TestMethod]
    [DataRow("NoEqualsSign", "'NoEqualsSign'", DisplayName = "missing '='")]
    [DataRow("=Value", "(empty)", DisplayName = "missing name")]
    [DataRow(" =Value", "(empty)", DisplayName = "whitespace-only name")]
    [DataRow("", "(empty)", DisplayName = "empty token")]
    public void Validate_MalformedProperty_IsRejectedNamingIt(string property, string expectedInError)
    {
        var error = MsBuildPropertyValidator.Validate([property]);

        Assert.IsNotNull(error);
        Assert.Contains(expectedInError, error);
        Assert.Contains("Name=Value", error, "The message should show the expected shape");
    }

    [TestMethod]
    [DataRow(";B=2", DisplayName = "leading ';'")]
    [DataRow("=1;B=2", DisplayName = "leading '='")]
    public void Validate_PackedPropertyWithNoLeadingName_SaysEmptyRatherThanNothing(string property)
    {
        // "Invalid --property ''" gives the user nothing to act on.
        var error = MsBuildPropertyValidator.Validate([property]);

        Assert.IsNotNull(error);
        Assert.Contains("(empty)", error);
        Assert.IsFalse(error.Contains("--property ''", StringComparison.Ordinal),
            "An empty name must render as a sentinel, not as empty quotes");
    }

    [TestMethod]
    public void Validate_MalformedProperty_NeverEchoesTheValue()
    {
        // A property value can hold a secret, so the error text names the property only.
        var error = MsBuildPropertyValidator.Validate(["=super-secret"]);

        Assert.IsNotNull(error);
        Assert.IsFalse(error.Contains("super-secret", StringComparison.Ordinal),
            "The value must not reach the error message");
    }

    [TestMethod]
    public void Validate_ReportsTheFirstMalformedProperty()
    {
        var error = MsBuildPropertyValidator.Validate(["Good=1", "AlsoBad", "Bad;Packed"]);

        Assert.IsNotNull(error);
        Assert.Contains("'AlsoBad'", error, "Validation stops at the first offender");
    }
}
