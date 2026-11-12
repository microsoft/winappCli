// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

/// <summary>
/// Reactor values follow the XAML rule: a prop, hook, context or applied property whose name ends in "Password" is
/// redacted. So is a value whose type has a public member with such a name, because its text (a record's
/// <c>ToString</c>) prints it. Nothing else is: not other secret-looking names, and not the text of a value.
/// </summary>
[TestClass]
public sealed class ReactorSecretParityTests
{
    public sealed record ApiToken(string Value);

    public sealed record Login(string User, string Password);

    public sealed record Session(string Id, Login Account);

    // A class without its own ToString prints only its type name, so a record holding it prints no password.
    public sealed class Vault
    {
        public string Password { get; set; } = "";
    }

    public sealed record Locker(string Name, Vault Vault);

    // A union-style state record: the declared member type has no password, the runtime one does.
    public abstract record AuthState;

    public sealed record SignedOut : AuthState;

    public sealed record SignedIn(string User, string Password) : AuthState;

    public sealed record AppState(AuthState Auth, int Count);

    public sealed record Wrapper(object Value);

    // A class with its own ToString that prints the record it holds.
    public sealed class Holder(AuthState auth)
    {
        public override string ToString() => $"Holder({auth})";
    }

    private static ReactorShownValue Shown(string name, string type, string value) =>
        ReactorShownValue.From(new ReactorSnapshotTests.DiagnosticValue(0, name, "state", type, value, true, false))!;

    [TestMethod]
    [DataRow("UserPassword")]
    [DataRow("password")]
    [DataRow("PASSWORD")]
    public void NamesEndingInPassword_AreRedacted(string name)
    {
        Assert.IsTrue(DevToolsSecrets.IsSecretName(name));
        Assert.AreEqual(("<redacted>", true), ReactorElementProps.Format(name, "hunter2"));
        Assert.AreEqual(("<redacted>", true), ReactorElementProps.Format(name, null), "even when it holds nothing");
        var hook = Shown(name, "string", "\"hunter2\"");
        Assert.AreEqual("<redacted>", hook.Value);
        Assert.IsFalse(hook.Editable, "and never offered for editing");
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
        Assert.AreEqual(("abc123", false), ReactorElementProps.Format(name, "abc123"));
        var hook = Shown(name, "string", "\"abc123\"");
        Assert.AreEqual("\"abc123\"", hook.Value);
        Assert.IsTrue(hook.Editable);
    }

    [TestMethod]
    public void ValuesWhoseTypeHoldsAPassword_AreRedacted()
    {
        Assert.AreEqual(("<redacted>", true), ReactorElementProps.Format("Account", new Login("ada", "hunter2")));
        Assert.AreEqual(("<redacted>", true), ReactorElementProps.Format("Logins", new List<Login> { new("ada", "hunter2") }), "a list of them");
        Assert.AreEqual(("<redacted>", true), ReactorElementProps.Format("Session", new Session("s1", new Login("ada", "hunter2"))),
            "a record that holds one prints it too");

        // Reactor formats hooks, props and contexts itself and names their type as C# reads it.
        var hook = Shown("", "Login", "Login { User = ada, Password = hunter2 }");
        Assert.AreEqual("<redacted>", hook.Value);
        Assert.IsTrue(hook.Redacted, "so set-state refuses it");
        Assert.IsFalse(hook.Editable);
        Assert.IsTrue(Shown("", "List<Login>", "List<Login> (1 item)").Redacted);
        Assert.IsTrue(Shown("", "Session?", "Session { Id = s1, Account = Login { User = ada, Password = hunter2 } }").Redacted);
    }

    // A self-contained app's own assemblies sit in the .NET runtime directory, so the app's types are found by the
    // assembly's name, not its folder.
    [TestMethod]
    [DataRow("System.Private.CoreLib", true)]
    [DataRow("System", true)]
    [DataRow("netstandard", true)]
    [DataRow("RxReleaseStudio", false)]
    [DataRow("Microsoft.UI.Reactor", false)]
    [DataRow("SystemTools", false)]
    public void DotNetAssemblies_AreRecognizedByName(string name, bool dotNet)
    {
        Assert.AreEqual(dotNet, DevToolsSecrets.IsDotNetAssembly(name));
    }

    [TestMethod]
    public void ARecordThatPrintsAPasswordOfItsRuntimeType_IsRedacted()
    {
        Assert.AreEqual(("<redacted>", true), ReactorElementProps.Format("State", new AppState(new SignedIn("ada", "hunter2"), 1)));
        Assert.AreEqual(("<redacted>", true), ReactorElementProps.Format("Box", new Wrapper(new Login("ada", "hunter2"))));
        Assert.IsFalse(ReactorElementProps.Format("State", new AppState(new SignedOut(), 1)).Redacted, "nothing to hide while signed out");
        AuthState signedIn = new SignedIn("ada", "hunter2");
        Assert.AreEqual(("<redacted>", true), ReactorElementProps.Format("Pair", (signedIn, 1)), "a tuple prints the record it holds");
        Assert.AreEqual(("<redacted>", true), ReactorElementProps.Format("Entry", new KeyValuePair<string, AuthState>("k", signedIn)));
        Assert.AreEqual(("<redacted>", true), ReactorElementProps.Format("Holder", new Holder(signedIn)), "so does a type's own ToString");
        Assert.IsTrue(Shown("", "(AuthState, int)", "(SignedIn { User = ada, Password = hunter2 }, 1)").Redacted);

        // Reactor formats the hook itself and names only the declared type.
        var hook = Shown("", "AppState", "AppState { Auth = SignedIn { User = ada, Password = hunter2 }, Count = 1 }");
        Assert.AreEqual("<redacted>", hook.Value);
        Assert.IsFalse(hook.Editable);
        Assert.IsTrue(Shown("", "object", "{ User = ada, Password = hunter2 }").Redacted, "an anonymous type prints its members too");
        Assert.AreEqual(("<redacted>", true), ReactorElementProps.FormatText("Tag", "SignedIn { User = ada, Password = hunter2 }"));
        Assert.AreEqual(("Hello, Password = x", false), ReactorElementProps.FormatText("Text", "Hello, Password = x"), "plain text is not a record");
    }

    [TestMethod]
    public void ValuesWithoutAPasswordMember_AreShown()
    {
        Assert.AreEqual(("ApiToken { Value = a }", false), ReactorElementProps.Format("Token", new ApiToken("a")));
        Assert.AreEqual("List<ApiToken> (1 items)", ReactorElementProps.Format("Items", new List<ApiToken> { new("a") }).Text);
        Assert.IsFalse(ReactorElementProps.Format("Locker", new Locker("a", new Vault())).Redacted, "Vault prints only its type name");
        Assert.AreEqual("ApiToken { Value = a }", Shown("", "ApiToken", "ApiToken { Value = a }").Value);
        Assert.AreEqual(("token=abc123", false), ReactorElementProps.Format("Status", "token=abc123"), "value text is never scanned");
        Assert.IsFalse(Shown("", "string", "\"Password = hunter2\"").Redacted);
    }

    [TestMethod]
    public void AppliedProperties_AreRedactedByTheirPropertyName()
    {
        Assert.AreEqual(("<redacted>", true), ReactorElementProps.FormatText("PasswordBox.Password", "hunter2"));
        Assert.AreEqual(("token=abc123", false), ReactorElementProps.FormatText("TextBlock.Text", "token=abc123"));
        Assert.AreEqual(("120", false), ReactorElementProps.FormatText("FrameworkElement.Width", "120"));
    }
}