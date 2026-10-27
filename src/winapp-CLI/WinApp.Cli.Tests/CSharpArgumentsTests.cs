// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

/// <summary>
/// C# source text that DevTools shows or stores: a string passed to a parameter whose name ends in "Password" is
/// redacted even when the statement never says "password", found through the called declaration's parameter list.
/// </summary>
[TestClass]
public class CSharpArgumentsTests
{
    private static readonly CSharpDeclarations Models = CSharpDeclarations.FromText(
        """
        record Login2Props(string User, string UserPassword, Creds Account);
        public record Creds(string Name, string Password);

        static class Factories
        {
            public static Element Component<TComponent, TProps>(TProps props, string? key = null) where TComponent : Component<TProps> => null!;
            public static Element LoginBox(string user, string adminPassword, int width = 0) => null!;
            public static Element Pin(this Element element, string label, string pinPassword) => element;
            public static Element Pair(string a, string bPassword) => null!;
            public static Element Pair(string a, string b, int c = 0) => null!;
        }

        class Vault
        {
            public Vault(string name, string masterPassword) { }
        }
        """);

    [TestMethod]
    public void Redact_PositionalArgumentsOfARecordConstructor_AndNestedConstructors()
    {
        var code = "Component<Login2, Login2Props>(new Login2Props(\"bob\", \"pos-secret-1\", new Creds(\"alice\", \"nested-pw-1\"))),";
        Assert.AreEqual(
            "Component<Login2, Login2Props>(new Login2Props(\"bob\", \"<redacted>\", new Creds(\"alice\", \"<redacted>\"))),",
            CSharpSourceText.Redact(code, Models));
    }

    [TestMethod]
    public void Redact_TargetTypedNew_FromTheParameterOrTheDeclaredType()
    {
        Assert.AreEqual(
            "Component<Login2, Login2Props>(new(\"bob\", \"<redacted>\", new(\"alice\", \"<redacted>\")))",
            CSharpSourceText.Redact("Component<Login2, Login2Props>(new(\"bob\", \"pw-1\", new(\"alice\", \"pw-2\")))", Models),
            "the type argument says what new(...) creates, and the record says what its arguments are");
        Assert.AreEqual(
            "Login2Props props = new(\"bob\", \"<redacted>\", null!);",
            CSharpSourceText.Redact("Login2Props props = new(\"bob\", \"pw-3\", null!);", Models));
        Assert.AreEqual(
            "var props = new(\"bob\", \"pw-4\", null!);",
            CSharpSourceText.Redact("var props = new(\"bob\", \"pw-4\", null!);", Models), "no type is known: unchanged");
    }

    [TestMethod]
    public void Redact_MethodAndConstructorCalls()
    {
        Assert.AreEqual("LoginBox(\"bob\", \"<redacted>\", 120)", CSharpSourceText.Redact("LoginBox(\"bob\", \"s3cret\", 120)", Models));
        Assert.AreEqual("Factories.LoginBox(\"bob\", \"<redacted>\")", CSharpSourceText.Redact("Factories.LoginBox(\"bob\", \"s3cret\")", Models));
        Assert.AreEqual("new Vault(\"home\", \"<redacted>\")", CSharpSourceText.Redact("new Vault(\"home\", \"s3cret\")", Models));
        Assert.AreEqual("TextBlock(\"x\").Pin(\"PIN\", \"<redacted>\")", CSharpSourceText.Redact("TextBlock(\"x\").Pin(\"PIN\", \"1234\")", Models),
            "an extension method called on its receiver skips its `this` parameter");
    }

    [TestMethod]
    public void Redact_NamedArgumentsAndInitializers_ByTheirName()
    {
        Assert.AreEqual("LoginBox(adminPassword: \"<redacted>\", user: \"<redacted>\")",
            CSharpSourceText.Redact("LoginBox(adminPassword: \"s3cret\", user: \"bob\")", Models),
            "the line names a password, so all of its text values are hidden");
        Assert.AreEqual("Unknown(userPassword: \"<redacted>\")", CSharpSourceText.Redact("Unknown(userPassword: \"s3cret\")", CSharpDeclarations.None));
        Assert.AreEqual("new Options { User = \"<redacted>\", ApiPassword = \"<redacted>\" }",
            CSharpSourceText.Redact("new Options { User = \"bob\", ApiPassword = \"s3cret\" }", CSharpDeclarations.None));
    }

    [TestMethod]
    public void Redact_LeavesArgumentsAloneWhenTheDeclarationIsUnknownOrAmbiguous()
    {
        const string unknown = "new Mystery(\"bob\", \"not-known\")";
        Assert.AreEqual(unknown, CSharpSourceText.Redact(unknown, Models), "no declaration: unchanged");
        Assert.AreEqual(unknown, CSharpSourceText.Redact(unknown, CSharpDeclarations.None));
        const string overloaded = "Pair(\"a\", \"b\")";
        Assert.AreEqual(overloaded, CSharpSourceText.Redact(overloaded, Models), "overloads that disagree: unchanged");
        Assert.AreEqual("Pair(\"a\", \"b\", 1)", CSharpSourceText.Redact("Pair(\"a\", \"b\", 1)", Models), "only the overload that fits is used");
        const string plain = "TextBlock(\"Done today\")";
        Assert.AreEqual(plain, CSharpSourceText.Redact(plain, Models));
    }

    [TestMethod]
    public void RedactLines_ReadsACallThatContinuesOnLaterLines()
    {
        string[] lines =
        [
            "var login = new Login2Props(",
            "    \"bob\",",
            "    \"pos-secret-2\", // the second argument",
            "    new Creds(\"alice\", \"x\"));",
        ];
        var shown = CSharpSourceText.RedactLines(lines, Models);
        Assert.AreEqual(lines[0], shown[0]);
        Assert.AreEqual(lines[1], shown[1]);
        Assert.AreEqual("    \"<redacted>\", // the second argument", shown[2]);
        Assert.AreEqual("    new Creds(\"alice\", \"<redacted>\"));", shown[3]);
    }

    [TestMethod]
    public void Declarations_ReadParameterListsAndIgnoreCalls()
    {
        var index = CSharpDeclarations.FromText(
            """
            namespace App;

            public sealed record Box<T>(T Value, string? Label = null);
            class Calls
            {
                void Run(int count, params string[] names)
                {
                    Run(1, "a");
                    Helper(out var result, count);
                    var x = Compute(count, 2);
                }
                public Calls(string name) : this() { }
            }
            """);
        var box = index.Find("Box").Single();
        Assert.AreEqual('T', box.Kind);
        Assert.AreEqual("T", string.Join(",", box.TypeParameters));
        Assert.AreEqual("Value,Label", string.Join(",", box.Parameters.Select(p => p.Name)));
        Assert.IsTrue(box.Parameters[1].HasDefault);
        Assert.IsTrue(index.Find("Run").Single().Parameters[1].IsParams, "calls to Run are not declarations");
        Assert.AreEqual('C', index.Find("Calls").Single().Kind);
        Assert.AreEqual(0, index.Find("Helper").Count + index.Find("Compute").Count);
    }

    [TestMethod]
    public void Declarations_ForSource_ReadsTheProjectAndTheProjectsItReferences()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "winapp-decl-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root, "App")).FullName;
            var lib = Directory.CreateDirectory(Path.Combine(root, "Lib")).FullName;
            File.WriteAllText(Path.Combine(app, "App.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup></Project>""");
            File.WriteAllText(Path.Combine(lib, "Lib.csproj"), """<Project Sdk="Microsoft.NET.Sdk" />""");
            File.WriteAllText(Path.Combine(lib, "Models.cs"), "public record Account(string Name, string Password);");
            Directory.CreateDirectory(Path.Combine(app, "obj"));
            File.WriteAllText(Path.Combine(app, "obj", "Generated.cs"), "record Ignored(string ObjPassword);");
            var page = Path.Combine(app, "Page.cs");
            File.WriteAllText(page, "class Page { Element Render() => Text(new Account(\"a\", \"b\").Name); }");

            var index = CSharpDeclarations.ForSource(page);
            Assert.AreEqual(1, index.Find("Account").Count, "a referenced project's declarations are read");
            Assert.AreEqual(0, index.Find("Ignored").Count, "build output is not");
            Assert.AreEqual("new Account(\"a\", \"<redacted>\")", CSharpSourceText.Redact("new Account(\"a\", \"b\")", index));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
