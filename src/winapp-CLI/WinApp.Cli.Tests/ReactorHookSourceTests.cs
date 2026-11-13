// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

[TestClass]
public class ReactorHookSourceTests
{
    private static readonly string[] PasswordAndTheme = ["userPassword", "theme"];
    private static readonly string[] CountOnly = ["count"];
    private static readonly string[] SortAndRows = ["sort", "rows"];
    private static readonly string[] AOnly = ["a"];
    private static readonly string[] LabelOnly = ["label"];

    private static IReadOnlyList<string?>? Names(string source, int line, int column, params string[] kinds) =>
        ReactorHookSource.Names(CSharpSourceText.SplitLines(source), line, column, kinds);

    private const string Login = """
        using Microsoft.UI.Reactor;

        class Login2 : Component<Login2Props>
        {
            public override Element Render()
            {
                var (password, setPassword) = UseState("hook-pass-1");
                (var count, var setCount) = UseState(0);
                var dbPassword = UseContext(Ctx.DbPassword);
                UseEffect(() => { if (count > 0) { return; } }, count);
                var (_, setTick) = UseReducer(0);
                Ref<int> clicks = UseRef(0);
                if (count > 3)
                {
                    return TextBlock("many");
                }
                return TextBlock($"{password.Length}");
            }
        }
        """;

    [TestMethod]
    public void Names_ReadsDeconstructionsAssignmentsAndUnassignedHooksInOrder()
    {
        var names = Names(Login, 6, 5, "state", "state", "context", "effect", "reducer", "ref");
        CollectionAssert.AreEqual(new[] { "password", "count", "dbPassword", null, null, "clicks" }, names?.ToArray());
    }

    [TestMethod]
    public void Names_StartsFromTheFirstStatementWhenThePdbPointsThere()
    {
        // A Release build's first sequence point is the first statement, not the opening brace.
        var names = Names(Login, 7, 0, "state", "state", "context", "effect", "reducer", "ref");
        Assert.AreEqual("password", names?[0]);
    }

    [TestMethod]
    public void Names_ReadsCtxAndThisReceivers()
    {
        const string source = """
            static class App
            {
                public static Element Root() => Component(ctx =>
                {
                    var (userPassword, setUserPassword) = ctx.UseState("");
                    var theme = ctx.UseContext(Theme.Current);
                    return TextBlock(theme);
                });
            }

            class Counter : Component
            {
                public override Element Render()
                {
                    var (count, setCount) = this.UseState(0);
                    return TextBlock($"{count}");
                }
            }
            """;
        CollectionAssert.AreEqual(PasswordAndTheme, Names(source, 4, 9, "state", "context")?.ToArray(),
            "a function component's lambda body");
        CollectionAssert.AreEqual(CountOnly, Names(source, 14, 5, "state")?.ToArray(), "this.UseState");
    }

    [TestMethod]
    public void Names_ReadsAGenericComponentInAFileScopedNamespace()
    {
        const string source = """
            namespace TaskBoard.Views;

            public sealed class Grid<T> : Component<GridProps<T>> where T : notnull
            {
                public override Element Render()
                {
                    var (sort, setSort) = UseState<string?>(null);
                    List<T> rows = UseMemo(() => Props.Items.ToList(), Props.Items);
                    return TextBlock($"{rows.Count} {sort}");
                }
            }
            """;
        CollectionAssert.AreEqual(SortAndRows, Names(source, 6, 5, "state", "memo")?.ToArray());
    }

    [TestMethod]
    public void Names_RefusesHooksThatAreNotStraightLine()
    {
        static string Body(string statements) => "class C : Component\n{\n    public override Element Render()\n    {\n" + statements +
            "\n        return TextBlock(\"x\");\n    }\n}\n";

        Assert.IsNull(Names(Body("        var (a, setA) = UseState(0);\n        if (a > 0) { var (b, setB) = UseState(1); }"), 4, 5, "state", "state"),
            "a hook in a branch");
        Assert.IsNull(Names(Body("        foreach (var i in Items) { UseEffect(() => { }); }"), 4, 5, "effect"), "a hook in a loop");
        Assert.IsNull(Names(Body("        if (Props.Hidden) return Empty();\n        var (a, setA) = UseState(0);"), 4, 5, "state"),
            "a hook after an early return");
        Assert.IsNull(Names(Body("        var a = Props.Flag ? UseState(1).Value : 0;"), 4, 5, "state"), "a hook in a conditional expression");
        Assert.IsNull(Names(Body("        var (a, setA) = UseState(0);\n        var form = UseForm();"), 4, 5, "state", "state"),
            "a custom hook that cannot be expanded");
        Assert.IsNull(Names(Body("        var a = Compute(UseRef(0));"), 4, 5, "ref"), "a hook inside another call's arguments");
        Assert.IsNull(Names(Body("        var (a, setA) = UseState(0);"), 4, 5, "state", "state"), "fewer hook calls than live hooks");
        Assert.IsNull(Names(Body("        var (a, setA) = UseState(0);"), 4, 5, "ref"), "a kind that does not line up");
        Assert.IsNull(Names(Body("        var a = Helper.UseState(0);"), 4, 5, "state"), "a qualified call that is not ctx. or this.");
        CollectionAssert.AreEqual(AOnly,
            Names(Body("        var (a, setA) = UseState(0);\n        Action reset = () => { setA(0); return; };"), 4, 5, "state")?.ToArray(),
            "a return inside a lambda is not an early return");
    }

    [TestMethod]
    public void Names_IgnoresHookCallsInCommentsAndStrings()
    {
        const string source = """
            class C : Component
            {
                public override Element Render()
                {
                    // UseState(1) is how you add state
                    var (label, setLabel) = UseState("UseState(2)");
                    return TextBlock(label);
                }
            }
            """;
        CollectionAssert.AreEqual(LabelOnly, Names(source, 4, 5, "state")?.ToArray());
    }

    [TestMethod]
    public void Names_IsUnknownOutsideAMethodBody()
    {
        Assert.IsNull(Names("class C : Component\n{\n    int x;\n}\n", 2, 1, "state"), "a type body is not a render method");
        Assert.IsNull(Names(Login, 99, 1, "state"), "a line outside the file");
    }

    // `UseIsDarkTheme()` is a hook DevTools can't place, so it can't tell which hook holds the password.
    private const string DarkLogin = """
        class Login2 : Component
        {
            public override Element Render()
            {
                var dark = UseIsDarkTheme();
                var (VARIABLE, setValue) = UseState("hunter2");
                var (count, setCount) = UseState(3);
                return TextBlock("Login2");
            }
        }
        """;

    private static (ReactorComponentState State, string Json)? ApplyTo(string variable)
    {
        var file = Path.Combine(Path.GetTempPath(), $"winapp-hooks-{Guid.NewGuid():N}.cs");
        File.WriteAllText(file, DarkLogin.Replace("VARIABLE", variable, StringComparison.Ordinal));
        try
        {
            var json = $$"""
                {"state":"ok","component":{"name":"Login2","kind":"class"},
                 "definition":{"file":{{System.Text.Json.JsonSerializer.Serialize(file)}},"line":4,"column":5},
                 "hooks":[{"index":0,"kind":"state","type":"string","value":"\"hunter2\"","canSet":true,"editable":true},
                          {"index":1,"kind":"state","type":"int","value":"3","canSet":true,"editable":true}]}
                """;
            var state = ReactorComponentState.Parse(json).State!;
            return ReactorHookSource.Apply(state, json);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public void Apply_HidesEveryTextHook_WhenAPasswordHookCannotBePlaced()
    {
        var (state, json) = ApplyTo("password") ?? throw new AssertFailedException("the hooks were left as they were");
        Assert.IsTrue(state.Hooks[0].Redacted && state.Hooks[0].Value == "<redacted>" && !state.Hooks[0].Editable,
            "the string hook may be the password: hidden and not editable");
        Assert.IsTrue(!state.Hooks[1].Redacted && state.Hooks[1].Value == "3" && state.Hooks[1].Editable, "a number is still shown");
        Assert.DoesNotContain("hunter2", json);
        Assert.IsNull(state.Hooks[0].Name, "no name is guessed for a hook that couldn't be placed");
    }

    [TestMethod]
    public void Apply_LeavesHooksAlone_WhenTheBodyUsesNoPassword() => Assert.IsNull(ApplyTo("token"));

    private static readonly string[] PwdOnly = ["pwd"];
    private static readonly string[] FormAndPin = ["form", "pin"];

    private const string BoxLogin = """
        class Login3 : Component
        {
            public override Element Render()
            {
                var (pwd, setPwd) = UseState("hunter2");
                var (user, setUser) = UseState("bob");
                return VStack(TextBox(user, setUser), PasswordBox(pwd, setPwd));
            }
        }
        """;

    [TestMethod]
    public void PasswordVariables_AreWhatTheBodyPassesWhereAPasswordGoes()
    {
        var lines = BoxLogin.Split('\n');
        CollectionAssert.AreEqual(PwdOnly, ReactorHookSource.PasswordVariables(lines, 4, 5).ToArray());
        var named = "class C : Component\n{\n    public override Element Render()\n    {\n" +
            "        var f = Field(label: user, adminPassword: form.Value.Secret);\n" +
            "        var d = new Login { UserPassword = pin!, Name = other };\n" +
            "        var ok = userPassword == other;\n        var e = PasswordBox(default, null);\n" +
            "        return PasswordBox(pwd + other);\n    }\n}\n";
        CollectionAssert.AreEqual(FormAndPin, ReactorHookSource.PasswordVariables(named.Split('\n'), 4, 5).ToArray());
        Assert.IsTrue(ReactorHookSource.NamesSecret(lines, 4, 5), "a body with a PasswordBox uses a password");
        Assert.IsTrue(ReactorHookSource.NamesSecret(
            "class C\n{\n    Element Render()\n    {\n        var (p, setP) = UseState(\"\");\n        return LoginForm(onPasswordChanged: setP);\n    }\n}\n".Split('\n'), 4, 5),
            "so does a body that hands a setter to a password-changed handler");
    }

    [TestMethod]
    public void PasswordVariables_IncludeAHookWhoseSetterAPasswordBoxCalls()
    {
        var uncontrolled = "class C : Component\n{\n    public override Element Render()\n    {\n" +
            "        var (a, setA) = UseState(\"\");\n        var (b, setB) = UseState(\"\");\n" +
            "        var (c, setC) = UseState(\"\");\n        var d = UseState(\"\");\n" +
            "        var (e, setE) = UseState(\"\");\n        var (shown, setShown) = UseState(\"\");\n" +
            "        var x1 = PasswordBox(default, setA);\n" +
            "        var x2 = PasswordBox(onPasswordChanged: v => setB(v));\n" +
            "        var x3 = PasswordBox().PasswordChanged(setC);\n" +
            "        var x4 = PasswordBox(null, d.Set);\n" +
            "        var x5 = PasswordBox(placeholderText: \"pin\", password: e, onPasswordChanged: setE);\n" +
            "        return TextBox(shown, setShown);\n    }\n}\n";
        CollectionAssert.AreEquivalent(UncontrolledPasswords, ReactorHookSource.PasswordVariables(uncontrolled.Split('\n'), 4, 5).ToArray());
    }

    private static readonly string[] UncontrolledPasswords = ["a", "b", "c", "d", "e"];

    [TestMethod]
    public void Apply_HidesAHookPassedToAPasswordBox_WhateverItsName()
    {
        var file = Path.Combine(Path.GetTempPath(), $"winapp-hooks-{Guid.NewGuid():N}.cs");
        File.WriteAllText(file, BoxLogin);
        try
        {
            var json = $$"""
                {"state":"ok","component":{"name":"Login3","kind":"class"},
                 "definition":{"file":{{System.Text.Json.JsonSerializer.Serialize(file)}},"line":4,"column":5},
                 "hooks":[{"index":0,"kind":"state","type":"string","value":"\"hunter2\"","canSet":true,"editable":true},
                          {"index":1,"kind":"state","type":"string","value":"\"bob\"","canSet":true,"editable":true}]}
                """;
            var (state, raw) = ReactorHookSource.Apply(ReactorComponentState.Parse(json).State!, json)
                ?? throw new AssertFailedException("the hooks were left as they were");
            Assert.IsTrue(state.Hooks[0] is { Name: "pwd", Redacted: true, Value: "<redacted>", Editable: false }, "pwd is a password");
            Assert.IsTrue(state.Hooks[1] is { Name: "user", Redacted: false, Value: "\"bob\"", Editable: true }, "user is shown");
            Assert.DoesNotContain("hunter2", raw);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
