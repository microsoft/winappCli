// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

[TestClass]
public class ReactorHookSourceTests
{
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
        CollectionAssert.AreEqual(new[] { "userPassword", "theme" }, Names(source, 4, 9, "state", "context")?.ToArray(),
            "a function component's lambda body");
        CollectionAssert.AreEqual(new[] { "count" }, Names(source, 14, 5, "state")?.ToArray(), "this.UseState");
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
        CollectionAssert.AreEqual(new[] { "sort", "rows" }, Names(source, 6, 5, "state", "memo")?.ToArray());
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
        CollectionAssert.AreEqual(new[] { "a" },
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
        CollectionAssert.AreEqual(new[] { "label" }, Names(source, 4, 5, "state")?.ToArray());
    }

    [TestMethod]
    public void Names_IsUnknownOutsideAMethodBody()
    {
        Assert.IsNull(Names("class C : Component\n{\n    int x;\n}\n", 2, 1, "state"), "a type body is not a render method");
        Assert.IsNull(Names(Login, 99, 1, "state"), "a line outside the file");
    }
}
