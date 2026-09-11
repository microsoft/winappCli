// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class UiCommandAdviceTests
{
    private string? _previousTarget;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _previousTarget = Environment.GetEnvironmentVariable(UiCommandAdvice.TargetVariable);
        Environment.SetEnvironmentVariable(UiCommandAdvice.TargetVariable, null);
    }

    [TestCleanup]
    public void Cleanup() =>
        Environment.SetEnvironmentVariable(UiCommandAdvice.TargetVariable, _previousTarget);

    [TestMethod]
    public void WithTarget_IsProviderNeutral_AndKeepsOnlyTheDerivedWorkflowIdentity()
    {
        var target = new ExecutionTargetRef("test-provider", "Build Agent");
        const string RawOwner = "private-workflow-identity";
        var token = GuestOwnerContext.DeriveGuestToken(RawOwner, target.StateKey, "epoch");
        var owner = GuestOwnerContext.WithWorkflow(null, token);

        var forwarded = UiCommandAdvice.WithTarget(owner, target);

        Assert.AreEqual(target.Selector, forwarded[UiCommandAdvice.TargetVariable]);
        Assert.AreEqual(token, forwarded[GuestOwnerContext.WorkflowVariable]);
        Assert.IsFalse(string.Join(' ', forwarded.Values).Contains(RawOwner, StringComparison.Ordinal));
        Assert.IsFalse(owner.ContainsKey(UiCommandAdvice.TargetVariable), "Do not mutate the caller's context.");

        var local = UiCommandAdvice.WithTarget(forwarded, ExecutionTargetRef.Local);
        Assert.IsFalse(local.ContainsKey(UiCommandAdvice.TargetVariable));
        Assert.AreEqual(token, local[GuestOwnerContext.WorkflowVariable]);
    }

    [TestMethod]
    public void LocalAdvice_AndValueFailureEnvelope_AreUnchanged()
    {
        Assert.AreEqual("winapp ui inspect", UiCommandAdvice.Command("inspect"));
        var failure = new UiValueSetException(new UiElement { Id = "e1", Type = "Edit", Selector = "SearchBox" });
        var logger = new CapturingLogger<UiCommandAdviceTests>();
        using var errors = new StringWriter();

        UiErrors.GenericError(logger, failure, json: true, errorOut: errors);

        var error = ReadError(errors.ToString());
        Assert.AreEqual(failure.Message, error.GetProperty("message").GetString());
        Assert.AreEqual("internal_error", error.GetProperty("code").GetString());
        Assert.AreEqual("InvalidOperationException", error.GetProperty("details").GetString());
        Assert.IsFalse(errors.ToString().Contains("--on", StringComparison.Ordinal));
        Assert.IsTrue(logger.Has(LogLevel.Error, failure.Message));
    }

    [TestMethod]
    [DataRow("sandbox")]
    [DataRow("test-provider:BuildAgent")]
    public void ValueFailure_HumanAndJsonAdvice_PreserveTheTarget(string target)
    {
        Environment.SetEnvironmentVariable(UiCommandAdvice.TargetVariable, target);
        var logger = new CapturingLogger<UiCommandAdviceTests>();
        using var errors = new StringWriter();
        var failure = new UiValueSetException(new UiElement
        {
            Id = "e1",
            Type = "winapp ui inspect",
            Selector = "SearchBox",
        });

        UiErrors.GenericError(logger, failure, json: true, errorOut: errors);

        var message = ReadError(errors.ToString()).GetProperty("message").GetString()!;
        StringAssert.Contains(message,
            $"winapp ui send-keys --verbatim \"<value>\" --target \"SearchBox\" --via send-input -a <app> --on {target}");
        StringAssert.Contains(message, $"'winapp ui send-keys --on {target}'");
        StringAssert.Contains(message, "Element e1 (winapp ui inspect)", "Element data is not command advice.");
        Assert.IsTrue(logger.Has(LogLevel.Error, message));
        Assert.IsFalse(failure.Message.Contains("--on", StringComparison.Ordinal), "Library message stays local.");
    }

    [TestMethod]
    public void GenericAndAmbiguousErrors_PreserveArbitraryText()
    {
        Environment.SetEnvironmentVariable(UiCommandAdvice.TargetVariable, "test-provider:agent");
        const string Message = "child output: winapp ui inspect; C:\\winapp ui send-keys\\file.json; --on sandbox";
        var logger = new CapturingLogger<UiCommandAdviceTests>();
        using var errors = new StringWriter();

        UiErrors.GenericError(logger, new IOException(Message), json: true, errorOut: errors);
        UiErrors.AmbiguousSelector(logger, Message);

        Assert.AreEqual(Message, ReadError(errors.ToString()).GetProperty("message").GetString());
        Assert.AreEqual(2, logger.Entries.Count(entry => entry.Level == LogLevel.Error &&
            entry.Message.EndsWith(Message, StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("test-provider:agent")]
    public void SharedErrors_PreserveTheirMessageAndEnvelopeShapes(string? target)
    {
        Environment.SetEnvironmentVariable(UiCommandAdvice.TargetVariable, target);
        var suffix = target is null ? "" : $" --on {target}";
        var logger = new CapturingLogger<UiCommandAdviceTests>();
        var previousError = Console.Error;
        using var errors = new StringWriter();
        try
        {
            Console.SetError(errors);
            UiErrors.MissingApp(logger, json: true);
            Assert.AreEqual(
                $"Target app required. Use --app <name|title|PID> or --window <HWND>. Run 'winapp ui list-windows{suffix}' to find running apps.",
                ReadError(errors.ToString()).GetProperty("message").GetString());
            errors.GetStringBuilder().Clear();

            UiErrors.MissingSelector(logger, "click", json: true);
            var missing = ReadError(errors.ToString());
            Assert.AreEqual("missing_selector", missing.GetProperty("code").GetString());
            Assert.AreEqual(
                $"A selector is required. Usage: winapp ui click <selector> -a <app>{suffix}. Use 'winapp ui search <text> -a <app>{suffix}' to find elements.",
                missing.GetProperty("message").GetString());
            errors.GetStringBuilder().Clear();

            UiErrors.ElementNotFound(logger, "winapp ui inspect", json: true);
            Assert.AreEqual("No element found matching 'winapp ui inspect'",
                ReadError(errors.ToString()).GetProperty("message").GetString());
            Assert.IsTrue(logger.Has(LogLevel.Error, $"'winapp ui inspect{suffix}' or 'winapp ui search{suffix}'"));
            errors.GetStringBuilder().Clear();

            UiErrors.StaleElement(logger, json: true);
            Assert.AreEqual("Element is no longer accessible",
                ReadError(errors.ToString()).GetProperty("message").GetString());
            Assert.IsTrue(logger.Has(LogLevel.Error, $"'winapp ui inspect{suffix}'"));
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [TestMethod]
    [DataRow("test-provider:Build Agent")]
    [DataRow("test-provider:O'Brien \"Agent\"")]
    [DataRow("test-provider:agent' --on local; throw 'injected")]
    [DataRow("test-provider:$(throw 'injected'); $env:WINAPP_UI_WORKFLOW_ID & | %PATH% `")]
    [DataRow("test-provider:\u2018quoted\u2019\u201a\u201b")]
    [DataRow("test-provider:line1\nline2")]
    public async Task QuotedSelectors_RemainOneLiteralPowerShellArgument(string selector)
    {
        Environment.SetEnvironmentVariable(UiCommandAdvice.TargetVariable, selector);
        var command = UiCommandAdvice.Command("inspect");
        var script = "$ProgressPreference = 'SilentlyContinue'; " +
            "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); " +
            "function winapp { ConvertTo-Json -InputObject @($args) -Compress }; " + command;
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start)!;
        var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        var errorsTask = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken);
        var output = await outputTask;
        var errors = await errorsTask;

        Assert.AreEqual(0, process.ExitCode, errors);
        Assert.AreEqual("", errors);
        CollectionAssert.AreEqual(new[] { "ui", "inspect", "--on", selector },
            JsonSerializer.Deserialize<string[]>(output), command);
    }

    private static JsonElement ReadError(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("error").Clone();
    }
}
