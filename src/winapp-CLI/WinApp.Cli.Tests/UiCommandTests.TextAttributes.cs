// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    [DataRow("700", "Courier New", "15.5", "3678732", "True", "1")]
    [DataRow("Mixed", "Mixed", "Mixed", "Mixed", "Mixed", "Mixed")]
    [DataRow("NotSupported", "NotSupported", "NotSupported", "NotSupported", "NotSupported", "NotSupported")]
    [DataRow("Unavailable", "Unavailable", "Unavailable", "Unavailable", "Unavailable", "Unavailable")]
    public async Task GetProperty_TextAttributes_FullJsonPreservesStringEnvelope(
        string weight, string name, string size, string color, string italic, string strike)
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "internal-id", Selector = "document", Type = "Document" };
        _fakeUia.PropertiesResult = new()
        {
            ["Name"] = "Document", ["BoundingRectangle"] = "10,20,300,200",
            ["FontWeight"] = weight, ["FontName"] = name, ["FontSize"] = size,
            ["ForegroundColor"] = color, ["IsItalic"] = italic, ["StrikethroughStyle"] = strike,
        };
        var command = GetRequiredService<UiGetPropertyCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["document", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = JsonSerializer.Deserialize<JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(2, result.EnumerateObject().Count());
        Assert.AreEqual("document", result.GetProperty("elementId").GetString());
        var properties = result.GetProperty("properties");
        Assert.AreEqual(_fakeUia.PropertiesResult.Count, properties.EnumerateObject().Count());
        foreach (var (key, expected) in _fakeUia.PropertiesResult)
        {
            Assert.AreEqual(JsonValueKind.String, properties.GetProperty(key).ValueKind, key);
            Assert.AreEqual(expected, properties.GetProperty(key).GetString(), key);
        }
    }

    [TestMethod]
    [DataRow("FontWeight")]
    [DataRow("FontName")]
    [DataRow("FontSize")]
    [DataRow("ForegroundColor")]
    [DataRow("IsItalic")]
    [DataRow("StrikethroughStyle")]
    public async Task GetProperty_TextAttribute_SingleNameAccepted(string name)
    {
        _fakeUia.FindSingleResult = new UiElement { Selector = "document", Type = "Document" };
        _fakeUia.PropertiesResult = new() { [name] = "Mixed" };
        var command = GetRequiredService<UiGetPropertyCommand>();
        Assert.AreEqual(0, await ParseAndInvokeWithCaptureAsync(command,
            ["document", "-a", "TestApp", "--property", name, "--json"]));
        var properties = JsonSerializer.Deserialize<JsonElement>(TestAnsiConsole.Output).GetProperty("properties");
        Assert.AreEqual(1, properties.EnumerateObject().Count());
        Assert.AreEqual("Mixed", properties.GetProperty(name).GetString());
    }

    [TestMethod]
    [DataRow("FontWieght")]
    [DataRow("fontweight")]
    [DataRow("")]
    public async Task GetProperty_UnknownName_InvalidArguments(string name)
    {
        _fakeUia.FindSingleThrow = new AssertFailedException("Invalid names must fail before element lookup.");
        var command = GetRequiredService<UiGetPropertyCommand>();
        Assert.AreEqual(1, await ParseAndInvokeWithCaptureAsync(command,
            ["document", "-a", "TestApp", "--property", name, "--json"]));
        AssertJsonErrorCode("invalid_arguments");
        Assert.AreEqual("", TestAnsiConsole.Output);
        StringAssert.Contains(ConsoleStdErr.ToString(), "Omit --property");
    }

    [TestMethod]
    public async Task GetProperty_TextAttribute_ComFailureIsScrubbed()
    {
        _fakeUia.FindSingleResult = new UiElement { Selector = "document" };
        _fakeUia.PropertiesThrow = new COMException("private-provider-detail", unchecked((int)0x80040201));
        var command = GetRequiredService<UiGetPropertyCommand>();
        Assert.AreEqual(1, await ParseAndInvokeWithCaptureAsync(command,
            ["document", "-a", "TestApp", "--property", "FontWeight", "--json"]));
        AssertJsonErrorCode("stale_element");
        Assert.AreEqual("", TestAnsiConsole.Output);
        Assert.IsFalse(ConsoleStdErr.ToString().Contains("private-provider-detail", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetProperty_TextAttribute_GenericFailureRetainsErrorEnvelope()
    {
        _fakeUia.FindSingleResult = new UiElement { Selector = "document" };
        _fakeUia.PropertiesThrow = new InvalidOperationException("Unexpected attribute type");
        var command = GetRequiredService<UiGetPropertyCommand>();
        Assert.AreEqual(1, await ParseAndInvokeWithCaptureAsync(command,
            ["document", "-a", "TestApp", "--property", "FontSize", "--json"]));
        AssertJsonErrorCode("internal_error");
        Assert.AreEqual("", TestAnsiConsole.Output);
    }
}
