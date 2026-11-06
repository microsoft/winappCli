// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

/// <summary>
/// Reactor values follow the XAML rule: a prop, hook, context or applied property whose name ends in "Password" is
/// redacted. Nothing else is: not other secret-looking names, not types, and not the text of a value.
/// </summary>
[TestClass]
public sealed class ReactorSecretParityTests
{
    public sealed record ApiToken(string Value);

    public sealed record Login(string User, string Password);

    public sealed record LoginProps(string User, string UserPassword, string ApiToken);

    [TestMethod]
    [DataRow("UserPassword")]
    [DataRow("password")]
    [DataRow("PASSWORD")]
    public void NamesEndingInPassword_AreRedacted(string name)
    {
        Assert.IsTrue(DevToolsSecrets.IsSecretName(name));
        Assert.AreEqual(("<redacted>", true), ReactorComponents.Format(name, typeof(string), "hunter2"));
        Assert.AreEqual(("<redacted>", true), ReactorComponents.Format(name, typeof(string), null), "even when it holds nothing");
        Assert.AreEqual(("<redacted>", true), ReactorElementProps.Format(name, 42));
        Assert.IsFalse(ReactorComponents.IsEditableValue(name, typeof(string), redacted: false), "and never offered for editing");
    }

    [TestMethod]
    [DataRow("ApiToken")]
    [DataRow("clientSecret")]
    [DataRow("UserCredential")]
    [DataRow("ApiKey")]
    [DataRow("PrivateKey")]
    [DataRow("DbConnectionString")]
    [DataRow("PasswordHint")]
    public void OtherNames_AreShownAsXamlShowsThem(string name)
    {
        Assert.IsFalse(DevToolsSecrets.IsSecretName(name));
        Assert.AreEqual(("\"abc123\"", false), ReactorComponents.Format(name, typeof(string), "abc123"));
        Assert.AreEqual(("abc123", false), ReactorElementProps.Format(name, "abc123"));
        Assert.IsTrue(ReactorComponents.IsEditableValue(name, typeof(string), redacted: false));
    }

    [TestMethod]
    public void ComponentProps_RedactOnlyThePasswordRow()
    {
        var rows = ReactorElementProps.ComponentProps(new LoginProps("ada", "hunter2", "abc123"));

        Assert.AreEqual(("User", "ada", false), rows.Single(r => r.Name == "User"));
        Assert.AreEqual(("UserPassword", "<redacted>", true), rows.Single(r => r.Name == "UserPassword"));
        Assert.AreEqual(("ApiToken", "abc123", false), rows.Single(r => r.Name == "ApiToken"), "a prop named ApiToken is shown");
    }

    [TestMethod]
    public void ValueTextAndTypes_AreNeverScanned()
    {
        Assert.AreEqual(("\"token=abc123\"", false), ReactorComponents.Format("Status", typeof(string), "token=abc123"));
        Assert.AreEqual(("ApiToken { Value = a }", false), ReactorComponents.Format("", typeof(ApiToken), new ApiToken("a")));
        Assert.AreEqual(("Login { User = ada, Password = hunter2 }", false), ReactorElementProps.Format("Account", new Login("ada", "hunter2")));
        Assert.AreEqual("List<ApiToken> (1 items)", ReactorElementProps.Format("Items", new List<ApiToken> { new("a") }).Text);
    }

    [TestMethod]
    public void AppliedProperties_AreRedactedByTheirPropertyName()
    {
        Assert.AreEqual(("<redacted>", true), ReactorElementProps.FormatText("PasswordBox.Password", "hunter2"));
        Assert.AreEqual(("token=abc123", false), ReactorElementProps.FormatText("TextBlock.Text", "token=abc123"));
        Assert.AreEqual(("120", false), ReactorElementProps.FormatText("FrameworkElement.Width", "120"));
    }
}
