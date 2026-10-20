// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    [DataRow("expanded", "Expanded")]
    [DataRow("collapsed", "Collapsed")]
    public async Task StatePrimitives_ExpandCollapse_ReturnsConfirmedStateAndRequestedRoot(string state, string expected)
    {
        var element = new UiElement { AutomationId = "Details", Selector = "Details", WindowHandle = 5678 };
        _fakeUia.FindSingleResult = element;
        _fakeTargetResolver.TargetResult.WindowHandle = 1234;
        var command = GetRequiredService<UiExpandCollapseCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["Details", "--state", state, "--window", "1234", "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = JsonSerializer.Deserialize<JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("Details", result.GetProperty("elementId").GetString());
        Assert.AreEqual(1234, result.GetProperty("hwnd").GetInt64());
        Assert.AreEqual(expected, result.GetProperty("state").GetString());
        Assert.AreEqual("ExpandCollapsePattern", result.GetProperty("pattern").GetString());
        Assert.AreSame(element, _fakeUia.ExpandCollapseCalls.Single().Element);
        Assert.AreEqual(expected, _fakeUia.ExpandCollapseCalls.Single().State.ToString());
        Assert.IsEmpty(_fakeUia.InvokeCalls);
        Assert.IsTrue(GetRequiredService<UiCommand>().Subcommands.Any(c => c.Name == "expand-collapse"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("leaf")]
    [DataRow("toggle")]
    [DataRow("0")]
    [DataRow("Expanded,Collapsed")]
    public async Task StatePrimitives_ExpandCollapse_RejectsInvalidStateBeforeResolving(string state)
    {
        _fakeTargetResolver.ResolveThrow = new InvalidOperationException("Must not resolve invalid input.");
        var command = GetRequiredService<UiExpandCollapseCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["Details", "--state", state, "--window", "1234", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiJsonError.CodeInvalidArguments);
        Assert.IsEmpty(_fakeUia.ExpandCollapseCalls);
        Assert.IsEmpty(TestAnsiConsole.Output);
    }

    [TestMethod]
    public async Task StatePrimitives_ExpandCollapse_RequiresState()
    {
        var command = GetRequiredService<UiExpandCollapseCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["Details", "--window", "1234", "--json"]);
        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiJsonError.CodeInvalidArguments);
        Assert.IsEmpty(_fakeUia.ExpandCollapseCalls);
    }

    [TestMethod]
    public async Task StatePrimitives_ExpandCollapse_UnsupportedTargetNeverInvokesAncestor()
    {
        var element = new UiElement
        {
            Selector = "Details",
            InvokableAncestor = new UiElement { Selector = "Ancestor" },
        };
        _fakeUia.FindSingleResult = element;
        _fakeUia.ExpandCollapseThrow = new NotSupportedException("The exact target has no ExpandCollapsePattern.");
        var command = GetRequiredService<UiExpandCollapseCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["Details", "--state", "collapsed", "--window", "1234", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiJsonError.CodePatternUnsupported);
        Assert.AreSame(element, _fakeUia.ExpandCollapseCalls.Single().Element);
        Assert.IsEmpty(_fakeUia.InvokeCalls);
        Assert.IsEmpty(TestAnsiConsole.Output);
    }

    [TestMethod]
    public async Task StatePrimitives_ExpandCollapse_UnconfirmedStateHasNoSuccessEnvelope()
    {
        _fakeUia.FindSingleResult = new UiElement { Selector = "Details" };
        _fakeUia.ExpandCollapseThrow = new TimeoutException("Provider did not confirm Collapsed.");
        var command = GetRequiredService<UiExpandCollapseCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["Details", "--state", "collapsed", "--window", "1234", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiJsonError.CodeInternalError);
        StringAssert.Contains(ConsoleStdErr.ToString(), "did not confirm Collapsed");
        Assert.IsEmpty(TestAnsiConsole.Output);
    }

    [TestMethod]
    public async Task StatePrimitives_ScrollPercent_UsesInvariantDecimalAndReportsActual()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var element = new UiElement { Selector = "PageScroll", WindowHandle = 5678 };
            _fakeUia.FindSingleResult = element;
            _fakeUia.VerticalScrollPercentResult = 37.124;
            _fakeTargetResolver.TargetResult.WindowHandle = 1234;
            var command = GetRequiredService<UiScrollCommand>();

            var exitCode = await ParseAndInvokeWithCaptureAsync(command,
                ["PageScroll", "--vertical-percent", "37.125", "--window", "1234", "--json"]);

            Assert.AreEqual(0, exitCode);
            var result = JsonSerializer.Deserialize<JsonElement>(TestAnsiConsole.Output);
            Assert.AreEqual(37.125, result.GetProperty("verticalPercent").GetDouble());
            Assert.AreEqual(37.124, result.GetProperty("actualVerticalPercent").GetDouble());
            Assert.AreEqual("PageScroll", result.GetProperty("elementId").GetString());
            Assert.AreEqual(1234, result.GetProperty("hwnd").GetInt64());
            Assert.AreEqual(37.125, _fakeUia.VerticalScrollPercentCalls.Single().Percent);
            Assert.AreSame(element, _fakeUia.VerticalScrollPercentCalls.Single().Element);
            Assert.IsEmpty(_fakeUia.ScrollContainerCalls);
            Assert.IsEmpty(_fakeMouse.ScrollWheelCalls);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [TestMethod]
    [DataRow("-0.01")]
    [DataRow("100.01")]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("-Infinity")]
    [DataRow("1e999")]
    [DataRow("37,125")]
    [DataRow("garbage")]
    [DataRow("")]
    public async Task StatePrimitives_ScrollPercent_RejectsInvalidInputBeforeResolving(string percent)
    {
        _fakeTargetResolver.ResolveThrow = new InvalidOperationException("Must not resolve invalid input.");
        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["PageScroll", "--vertical-percent", percent, "--window", "1234", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiJsonError.CodeInvalidArguments);
        Assert.IsEmpty(_fakeUia.VerticalScrollPercentCalls);
        Assert.IsEmpty(_fakeUia.ScrollContainerCalls);
        Assert.IsEmpty(_fakeMouse.ScrollWheelCalls);
        Assert.IsEmpty(TestAnsiConsole.Output);
    }

    [TestMethod]
    [DataRow("--direction", "down")]
    [DataRow("--to", "bottom")]
    [DataRow("--wheel", "-1")]
    public async Task StatePrimitives_ScrollPercent_IsMutuallyExclusive(string option, string value)
    {
        _fakeTargetResolver.ResolveThrow = new InvalidOperationException("Must not resolve conflicting modes.");
        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["PageScroll", "--vertical-percent", "0", option, value, "--window", "1234", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiJsonError.CodeInvalidArguments);
        Assert.IsEmpty(_fakeUia.VerticalScrollPercentCalls);
        Assert.IsEmpty(_fakeUia.ScrollContainerCalls);
        Assert.IsEmpty(_fakeMouse.ScrollWheelCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StatePrimitives_ScrollPercent_ReportsUnsupportedOrUnconfirmedWithoutFallback(bool timeout)
    {
        _fakeUia.FindSingleResult = new UiElement { Selector = "PageScroll" };
        _fakeUia.VerticalScrollPercentThrow = timeout
            ? new TimeoutException("ScrollPattern did not confirm 50; actual 0.")
            : new NotSupportedException("The exact target has no ScrollPattern.");
        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["PageScroll", "--vertical-percent", "50", "--window", "1234", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(timeout ? UiJsonError.CodeInternalError : UiJsonError.CodePatternUnsupported);
        Assert.IsEmpty(TestAnsiConsole.Output);
        Assert.IsEmpty(_fakeUia.ScrollContainerCalls);
        Assert.IsEmpty(_fakeMouse.ScrollWheelCalls);
    }

    [TestMethod]
    public async Task StatePrimitives_GetProperty_PreservesSelectionArrayAndInvariantScalarProperties()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            _fakeUia.FindSingleResult = new UiElement { Selector = "ModeSelector" };
            _fakeUia.PropertiesResult = new()
            {
                ["SelectionAutomationIds"] = new[] { "ModeBalanced" },
                ["ScrollVerticalViewSize"] = 37.125,
                ["ScrollHorizontalViewSize"] = 100.0,
                ["ScrollVerticalPercent"] = 12.5,
                ["IsEnabled"] = true,
            };
            var command = GetRequiredService<UiGetPropertyCommand>();
            var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["ModeSelector", "--window", "1234", "--json"]);

            Assert.AreEqual(0, exitCode);
            var result = JsonSerializer.Deserialize<JsonElement>(TestAnsiConsole.Output);
            var properties = result.GetProperty("properties");
            Assert.AreEqual(JsonValueKind.Array, properties.GetProperty("SelectionAutomationIds").ValueKind);
            Assert.AreEqual("ModeBalanced", properties.GetProperty("SelectionAutomationIds")[0].GetString());
            Assert.AreEqual("37.125", properties.GetProperty("ScrollVerticalViewSize").GetString());
            Assert.AreEqual("100", properties.GetProperty("ScrollHorizontalViewSize").GetString());
            Assert.AreEqual("12.5", properties.GetProperty("ScrollVerticalPercent").GetString());
            Assert.AreEqual("True", properties.GetProperty("IsEnabled").GetString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
