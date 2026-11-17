// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Regression tests for <see cref="WinAppParserConfiguration.Default"/>.
/// </summary>
/// <remarks>
/// Guards issue #619: argument values that begin with <c>@</c> (chat/social mentions such as
/// <c>@assistant</c>, npm scopes such as <c>@scope/pkg</c>, positional emails, etc.) must be passed
/// through to the command handler as literal text and must NOT be interpreted as
/// System.CommandLine <c>@responsefile</c> indirection. Before the fix, response-file token expansion
/// was left enabled, so <c>winapp ui search "@assistant"</c> failed at parse time with
/// "Response file not found 'assistant'." and the handler never ran.
/// </remarks>
[TestClass]
public class WinAppParserConfigurationTests : BaseCommandTests
{
    private static string ErrorSummary(System.CommandLine.ParseResult parseResult) =>
        "Leading '@' value must not trigger response-file expansion. Parse errors: " +
        string.Join("; ", parseResult.Errors.Select(e => e.Message));

    [TestMethod]
    public void PassesLeadingAtValueThroughAsLiteral_ForSearchSelector()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "ui", "search", "@assistant", "-a", "fakeapp" };

        var parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

        Assert.AreEqual(0, parseResult.Errors.Count, ErrorSummary(parseResult));
        Assert.AreEqual("@assistant", parseResult.GetValue(SharedUiOptions.SelectorArgument));
    }

    [TestMethod]
    public void PassesLeadingAtValueThroughAsLiteral_ForSetValueArgument()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "ui", "set-value", "txt-name", "@somebody", "-a", "fakeapp" };

        var parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

        Assert.AreEqual(0, parseResult.Errors.Count, ErrorSummary(parseResult));
        Assert.AreEqual("@somebody", parseResult.GetValue(SharedUiOptions.ValueArgument));
    }

    [TestMethod]
    public void PassesLeadingAtValueThroughAsLiteral_ForAppOption()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "ui", "search", "selector", "-a", "@fakeapp" };

        var parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

        Assert.AreEqual(0, parseResult.Errors.Count, ErrorSummary(parseResult));
        Assert.AreEqual("@fakeapp", parseResult.GetValue(SharedUiOptions.AppOption));
    }

    [TestMethod]
    public void PassesNpmScopeStyleValueThroughAsLiteral()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "ui", "search", "@scope/pkg", "-a", "fakeapp" };

        var parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

        Assert.AreEqual(0, parseResult.Errors.Count, ErrorSummary(parseResult));
        Assert.AreEqual("@scope/pkg", parseResult.GetValue(SharedUiOptions.SelectorArgument));
    }

    [TestMethod]
    public void PassesPlainValueThroughUnchanged()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "ui", "search", "assistant", "-a", "fakeapp" };

        var parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

        Assert.AreEqual(0, parseResult.Errors.Count, ErrorSummary(parseResult));
        Assert.AreEqual("assistant", parseResult.GetValue(SharedUiOptions.SelectorArgument));
    }

    [TestMethod]
    [DataRow("1772174")]
    [DataRow("0x1B0A8E")]
    [DataRow("0X1b0a8e")]
    public void WindowHandle_AcceptsDecimalAndHex(string value)
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var parseResult = rootCommand.Parse(["ui", "inspect", "-w", value], WinAppParserConfiguration.Default);

        Assert.AreEqual(0, parseResult.Errors.Count, string.Join("; ", parseResult.Errors.Select(e => e.Message)));
        Assert.AreEqual(1772174L, parseResult.GetValue(SharedUiOptions.WindowOption));
    }

    [TestMethod]
    public void WindowHandle_BadValueExplainsWhatAHandleLooksLike()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var parseResult = rootCommand.Parse(["ui", "inspect", "-w", "notepad"], WinAppParserConfiguration.Default);

        var message = parseResult.Errors.Select(e => e.Message).Single(m => m.Contains("is not a window handle"));
        StringAssert.Contains(message, "'notepad'");
        StringAssert.Contains(message, "0x1B0A8E");
    }
}
