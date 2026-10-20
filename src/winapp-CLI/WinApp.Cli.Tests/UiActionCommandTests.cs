// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class UiActionCommandTests : IDisposable
{
    private ServiceProvider _services = null!;
    private TestConsole _console = null!;
    private StringWriter _stderr = null!;
    private ActionServiceFake _actions = null!;
    private TargetResolverFake _resolver = null!;
    private string _directory = null!;
    private readonly List<string> _defaultOutputs = [];

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.GetFullPath(Path.Combine("TestResults", $"UiActionCommandTests-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(_directory);
        _console = new TestConsole();
        _stderr = new StringWriter();
        _actions = new ActionServiceFake();
        _resolver = new TargetResolverFake();
        _services = new ServiceCollection()
            .ConfigureCommands()
            .AddSingleton<IAnsiConsole>(_console)
            .AddSingleton<IUiActionService>(_actions)
            .AddSingleton<IUiTargetResolver>(_resolver)
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.None))
            .BuildServiceProvider();
        _services.GetRequiredService<UiRecordActionsCommand.Handler>().IsInputRedirected = () => false;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Dispose();
        foreach (var file in _defaultOutputs)
        {
            File.Delete(file);
        }
        Directory.Delete(_directory, recursive: true);
    }

    public void Dispose()
    {
        _services?.Dispose();
        _console?.Dispose();
        _stderr?.Dispose();
    }

    [TestMethod]
    public void Commands_AreWiredAndVideoRecordIsUnchanged()
    {
        var ui = _services.GetRequiredService<UiCommand>();
        var video = _services.GetRequiredService<UiRecordCommand>();
        Assert.IsTrue(ui.Subcommands.Contains(video));
        Assert.IsTrue(ui.Subcommands.Contains(_services.GetRequiredService<UiRecordActionsCommand>()));
        Assert.IsTrue(ui.Subcommands.Contains(_services.GetRequiredService<UiReplayCommand>()));
        Assert.IsTrue(video.Options.Contains(UiRecordCommand.FramesOption));
        Assert.IsTrue(video.Options.Contains(SharedUiOptions.FpsOption));
        Assert.IsFalse(video.Options.Contains(UiRecordActionsCommand.CaptureTextOption));
        StringAssert.Contains(video.ShortDescription, "MP4");
    }

    [TestMethod]
    public void Replay_HelpDescribesProcessIdentityGuard()
    {
        var command = _services.GetRequiredService<UiReplayCommand>();

        Assert.IsNotNull(command.Description);
        StringAssert.Contains(command.Description, "process name must match");
        StringAssert.Contains(command.Description, "case-insensitive");
        StringAssert.Contains(command.Description, "a new PID is allowed");
    }

    [TestMethod]
    [DataRow("--allow-all")]
    [DataRow("--allow-system-keys")]
    [DataRow("--via")]
    [DataRow("--capture-screen")]
    public void ActionCommands_DoNotExposeInputScopeBypasses(string option)
    {
        Assert.IsNotEmpty(_services.GetRequiredService<UiRecordActionsCommand>().Parse(["-a", "TestApp", option]).Errors);
        Assert.IsNotEmpty(_services.GetRequiredService<UiReplayCommand>().Parse(["flow.json", "-a", "TestApp", option]).Errors);
        Assert.AreEqual(0, _actions.RecordCalls);
        Assert.AreEqual(0, _actions.ReplayCalls);
    }

    [TestMethod]
    public async Task Record_RequiresExplicitTarget()
    {
        var result = await RecordAsync(["--output", OutputPath, "--json"]);

        AssertFailure(result, "missing_app");
        AssertNoUiCalls();
        Assert.IsFalse(File.Exists(OutputPath));
    }

    [TestMethod]
    [DataRow("--duration-sec=-1")]
    [DataRow("--duration-sec=3601")]
    [DataRow("--max-steps=0")]
    [DataRow("--max-steps=10001")]
    [DataRow("--window=0")]
    public async Task Record_ValidatesLimitsBeforeResolving(string option)
    {
        var result = await RecordAsync(["-a", "TestApp", "--output", OutputPath, option, "--json"]);

        AssertFailure(result, "invalid_arguments");
        AssertNoUiCalls();
        Assert.IsFalse(File.Exists(OutputPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Record_RejectsExistingFilesAndDirectoriesBeforeResolving(bool directory)
    {
        if (directory) { Directory.CreateDirectory(OutputPath); }
        else { await File.WriteAllTextAsync(OutputPath, "sentinel", TestContext.CancellationToken); }

        var result = await RecordAsync(["-a", "TestApp", "--output", OutputPath, "--json"]);

        AssertFailure(result, "output_exists");
        AssertNoUiCalls();
        if (directory) { Assert.IsTrue(Directory.Exists(OutputPath)); }
        else { Assert.AreEqual("sentinel", await File.ReadAllTextAsync(OutputPath, TestContext.CancellationToken)); }
    }

    [TestMethod]
    public async Task Record_InvalidParentPathDoesNotStartCapture()
    {
        await File.WriteAllTextAsync(OutputPath, "parent is a file", TestContext.CancellationToken);

        var result = await RecordAsync(["-a", "TestApp", "--output", Path.Combine(OutputPath, "flow.json"), "--json"]);

        AssertFailure(result, "invalid_arguments");
        AssertNoUiCalls();
    }

    [TestMethod]
    public async Task Record_OutputCreatedDuringCaptureIsNotOverwritten()
    {
        _actions.OnRecord = async (_, onReady) =>
        {
            onReady?.Invoke();
            await File.WriteAllTextAsync(OutputPath, "racing writer", TestContext.CancellationToken);
            return _actions.RecordResult;
        };

        var result = await RecordAsync(["-a", "TestApp", "--output", OutputPath, "--json"]);

        AssertFailure(result, "output_exists");
        Assert.AreEqual(1, _actions.RecordCalls);
        Assert.AreEqual("racing writer", await File.ReadAllTextAsync(OutputPath, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Record_DefaultsToParameterizedHardwareInputAndReportsNamesOnly()
    {
        var result = await RecordAsync(["-a", "TestApp", "--output", OutputPath, "--json"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsNotNull(_actions.RecordOptions);
        Assert.AreEqual(0, _actions.RecordOptions.DurationSec);
        Assert.AreEqual(1000, _actions.RecordOptions.MaxSteps);
        Assert.IsFalse(_actions.RecordOptions.CaptureText);
        Assert.IsFalse(_actions.RecordOptions.IncludeInjected);
        Assert.AreEqual("TestApp", _resolver.LastApp);
        var saved = UiActionFlowDocument.Read(OutputPath);
        Assert.AreEqual("text1", saved.Steps[1].Parameter);
        Assert.IsNull(saved.Steps[1].Text);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.AreEqual(OutputPath, json.RootElement.GetProperty("path").GetString());
        Assert.AreEqual("recorded", json.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(2, json.RootElement.GetProperty("stepCount").GetInt32());
        Assert.AreEqual("text1", json.RootElement.GetProperty("parameters")[0].GetString());
        Assert.IsFalse(json.RootElement.TryGetProperty("steps", out _));
        AssertStartedEvent(result.Stderr);
    }

    [TestMethod]
    public async Task Record_ExplicitCaptureAndInjectedOptionsArePassedWithoutLoggingText()
    {
        const string privateText = "non-password-but-private";
        _actions.RecordResult = new UiActionRecordResult(Flow() with
        {
            Steps = [ClickStep(), TextStep() with { Parameter = null, Text = privateText }],
        }, "duration_elapsed");

        var result = await RecordAsync([
            "-w", "42", "--output", OutputPath, "--duration-sec", "3600",
            "--max-steps", "10000", "--capture-text", "--include-injected", "--json",
        ]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsNotNull(_actions.RecordOptions);
        Assert.IsTrue(_actions.RecordOptions.CaptureText);
        Assert.IsTrue(_actions.RecordOptions.IncludeInjected);
        Assert.AreEqual(3600, _actions.RecordOptions.DurationSec);
        Assert.AreEqual(10000, _actions.RecordOptions.MaxSteps);
        Assert.AreEqual(42L, _resolver.LastWindow);
        Assert.IsNull(_resolver.LastApp);
        Assert.AreEqual(privateText, UiActionFlowDocument.Read(OutputPath).Steps[1].Text);
        Assert.IsFalse((result.Stdout + result.Stderr).Contains(privateText, StringComparison.Ordinal));
        using var started = JsonDocument.Parse(result.Stderr.Trim());
        Assert.IsTrue(started.RootElement.GetProperty("captureText").GetBoolean());
        Assert.IsTrue(started.RootElement.GetProperty("includeInjected").GetBoolean());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Record_TextModeExplainsPrivacyAndReview(bool captureText)
    {
        string[] flags = captureText ? ["--capture-text"] : [];
        var result = await RecordAsync(["-a", "TestApp", "--output", OutputPath, .. flags]);

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, captureText ? "stores full non-password field values" : "named parameter placeholders");
        StringAssert.Contains(result.Stderr, "Password fields stop recording as incomplete");
        StringAssert.Contains(result.Stderr, "outside the selected window");
        StringAssert.Contains(result.Stdout, "--accept-recording");
    }

    [TestMethod]
    public async Task Record_QuietOmitsPrivacyNoticeAndSummary()
    {
        var result = await RecordAsync(["-a", "TestApp", "--output", OutputPath, "--quiet"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("", result.Stdout);
        Assert.AreEqual("", result.Stderr);
        Assert.IsTrue(File.Exists(OutputPath));
    }

    [TestMethod]
    [DataRow("password_field")]
    [DataRow("unsupported_input")]
    [DataRow("max_steps")]
    public async Task Record_IncompleteDraftIsSavedWithFailingExitCode(string stopReason)
    {
        _actions.RecordResult = new UiActionRecordResult(Flow() with
        {
            Status = "incomplete",
            Warnings = ["Input capture stopped; review this incomplete draft."],
        }, stopReason);

        var result = await RecordAsync(["-a", "TestApp", "--output", OutputPath, "--json"]);

        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual("incomplete", UiActionFlowDocument.Read(OutputPath).Status);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.AreEqual("incomplete", json.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(stopReason, json.RootElement.GetProperty("stopReason").GetString());
        Assert.AreEqual(1, json.RootElement.GetProperty("warnings").GetArrayLength());
        AssertStartedEvent(result.Stderr);
    }

    [TestMethod]
    public async Task Record_EmptyDraftIsIncompleteAndCannotReplay()
    {
        _actions.RecordResult = new UiActionRecordResult(Flow() with { Steps = [] }, "stopped");

        var result = await RecordAsync(["-a", "TestApp", "--output", OutputPath, "--json"]);

        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual("incomplete", UiActionFlowDocument.Read(OutputPath).Status);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.AreEqual(0, json.RootElement.GetProperty("stepCount").GetInt32());
    }

    [TestMethod]
    public async Task Record_DefaultOutputNamesAreUniqueJsonFiles()
    {
        for (var index = 0; index < 2; index++)
        {
            var result = await RecordAsync(["-a", "TestApp", "--json"]);
            using var json = JsonDocument.Parse(result.Stdout);
            var path = json.RootElement.GetProperty("path").GetString()!;
            _defaultOutputs.Add(path);
            Assert.AreEqual(0, result.ExitCode);
            Assert.IsTrue(Path.GetFileName(path).StartsWith("ui-actions-", StringComparison.Ordinal));
            Assert.AreEqual(".json", Path.GetExtension(path));
            Assert.IsTrue(File.Exists(path));
        }
        Assert.AreNotEqual(_defaultOutputs[0], _defaultOutputs[1]);
    }

    [TestMethod]
    public async Task Record_ReadyEventIsEmittedOnlyAfterServiceReadinessAndOnlyOnce()
    {
        _actions.OnRecord = (_, onReady) =>
        {
            Assert.AreEqual("", _stderr.ToString());
            Assert.AreEqual("", _console.Output);
            onReady?.Invoke();
            onReady?.Invoke();
            AssertStartedEvent(_stderr.ToString());
            Assert.AreEqual("", _console.Output);
            return Task.FromResult(_actions.RecordResult);
        };

        var result = await RecordAsync(["-a", "TestApp", "--output", OutputPath, "--json"]);

        Assert.AreEqual(0, result.ExitCode);
        AssertStartedEvent(result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.IsFalse(json.RootElement.TryGetProperty("event", out _));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("\n")]
    public async Task Record_UnboundedStdinStopWaitsForReadinessAndSavesDraft(string input)
    {
        using var reader = new ObservedReader(input);
        var handler = _services.GetRequiredService<UiRecordActionsCommand.Handler>();
        handler.IsInputRedirected = () => true;
        handler.StdinOverride = reader;
        _actions.OnRecord = async (ct, onReady) =>
        {
            await reader.ReadObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
            Assert.IsFalse(ct.IsCancellationRequested);
            Assert.AreEqual("", _stderr.ToString());
            onReady?.Invoke();
            await WaitForStopAsync(ct);
            return _actions.RecordResult;
        };

        var result = await RecordAsync(["-a", "TestApp", "--output", OutputPath, "--json"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(File.Exists(OutputPath));
        AssertStartedEvent(result.Stderr);
    }

    [TestMethod]
    public async Task Record_TimedCaptureNeverReadsRedirectedStdin()
    {
        using var reader = new ObservedReader("");
        var handler = _services.GetRequiredService<UiRecordActionsCommand.Handler>();
        handler.IsInputRedirected = () => true;
        handler.StdinOverride = reader;

        var result = await RecordAsync(["-a", "TestApp", "--duration-sec", "1", "--output", OutputPath, "--json"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsFalse(reader.ReadObserved.Task.IsCompleted);
    }

    [TestMethod]
    public async Task Record_CallerCancellationDuringCaptureStillPersistsReturnedFlow()
    {
        using var stop = new CancellationTokenSource();
        _actions.OnRecord = async (ct, onReady) =>
        {
            onReady?.Invoke();
            await stop.CancelAsync();
            Assert.IsTrue(ct.IsCancellationRequested);
            return _actions.RecordResult;
        };

        var result = await InvokeAsync<UiRecordActionsCommand>(["-a", "TestApp", "--output", OutputPath, "--json"], stop.Token);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("recorded", UiActionFlowDocument.Read(OutputPath).Status);
    }

    [TestMethod]
    public async Task Record_CancellationBeforeReadinessReturnsStructuredFailure()
    {
        _actions.OnRecord = (_, _) => throw new OperationCanceledException("do-not-log-this");

        var result = await RecordAsync(["-a", "TestApp", "--output", OutputPath, "--json"]);

        AssertFailure(result, "cancelled");
        Assert.IsFalse(result.Stderr.Contains("do-not-log-this", StringComparison.Ordinal));
        Assert.IsFalse(result.Stderr.Contains("recording-actions-started", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(OutputPath));
    }

    [TestMethod]
    [DataRow("no_interactive_desktop")]
    [DataRow("recording_unavailable")]
    public async Task Record_TypedStartupFailureIsStructuredAndDoesNotCreateAFlow(string code)
    {
        _actions.OnRecord = (_, _) => throw new UiActionException(code, "Action capture could not start safely.");

        var result = await RecordAsync(["-a", "TestApp", "--output", OutputPath, "--json"]);

        AssertFailure(result, code);
        Assert.IsFalse(result.Stderr.Contains("recording-actions-started", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(OutputPath));
    }

    [TestMethod]
    public async Task Record_UnexpectedFailureDoesNotEchoExceptionPayload()
    {
        _actions.OnRecord = (_, _) => throw new InvalidOperationException("private-field-value");

        var result = await RecordAsync(["-a", "TestApp", "--output", OutputPath, "--json"]);

        AssertFailure(result, "internal_error");
        Assert.IsFalse(result.Stderr.Contains("private-field-value", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(OutputPath));
    }

    [TestMethod]
    public async Task Record_ReportsWarningsOnStderrInTextMode()
    {
        _actions.RecordResult = new UiActionRecordResult(Flow() with { Warnings = ["Capture paused outside the selected window."] }, "stopped");

        var result = await RecordAsync(["-a", "TestApp", "--output", OutputPath]);

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, "Warning: Capture paused");
        Assert.IsFalse(result.Stdout.Contains("Warning:", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Replay_RequiresReviewBeforeResolving()
    {
        WriteFlow();
        var result = await ReplayAsync([OutputPath, "-a", "TestApp", "--json"]);

        AssertFailure(result, "review_required");
        AssertNoUiCalls();
    }

    [TestMethod]
    public async Task Replay_RequiresExplicitTargetAndDoesNotUseRecordedProcess()
    {
        WriteFlow();
        var result = await ReplayAsync([OutputPath, "--accept-recording", "--json"]);

        AssertFailure(result, "missing_app");
        AssertNoUiCalls();
    }

    [TestMethod]
    public async Task Replay_RequiresAFlowArgument()
    {
        var result = await ReplayAsync(["-a", "TestApp", "--accept-recording", "--json"]);

        AssertFailure(result, "invalid_arguments");
        AssertNoUiCalls();
    }

    [TestMethod]
    [DataRow("--timeout=0")]
    [DataRow("--timeout=99")]
    [DataRow("--timeout=120001")]
    [DataRow("--window=0")]
    public async Task Replay_ValidatesLimitsBeforeResolving(string option)
    {
        WriteFlow();
        var result = await ReplayAsync([.. ReplayArguments, option]);

        AssertFailure(result, "invalid_arguments");
        AssertNoUiCalls();
    }

    [TestMethod]
    public void NegativeWindowHandlesAreRejectedByTheSharedParser()
    {
        Assert.IsNotEmpty(_services.GetRequiredService<UiRecordActionsCommand>().Parse(["--window=-1"]).Errors);
        Assert.IsNotEmpty(_services.GetRequiredService<UiReplayCommand>().Parse(["flow.json", "--window=-1"]).Errors);
        AssertNoUiCalls();
    }

    [TestMethod]
    [DataRow("private-no-equals", "invalid_arguments")]
    [DataRow("=private-value", "invalid_arguments")]
    [DataRow("1name=private-value", "invalid_flow")]
    [DataRow("text 1=private-value", "invalid_flow")]
    [DataRow("unused=private-value", "invalid_flow")]
    [DataRow("Text1=private-value", "invalid_flow")]
    [DataRow("text1=private-\u0001-value", "invalid_flow")]
    public async Task Replay_RejectsMalformedUnknownAndUnsupportedParametersWithoutEcho(string parameter, string code)
    {
        WriteFlow();
        var result = await ReplayAsync([OutputPath, "-a", "TestApp", "--accept-recording", "--parameter", parameter, "--json"]);

        AssertFailure(result, code);
        AssertNoUiCalls();
        Assert.IsFalse(result.Stderr.Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Replay_RejectsDuplicateParametersWithoutEcho()
    {
        WriteFlow();
        var result = await ReplayAsync([.. ReplayArguments, "--parameter", "text1=private-duplicate"]);

        AssertFailure(result, "invalid_arguments");
        AssertNoUiCalls();
        Assert.IsFalse(result.Stderr.Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Replay_RejectsValuelessParameterOccurrencesAsJson(bool afterValidParameter)
    {
        WriteFlow();
        string[] parameters = afterValidParameter ? ["--parameter", "text1=private"] : [];
        var result = await ReplayAsync([OutputPath, "-a", "TestApp", "--accept-recording", .. parameters, "--parameter", "--json"]);

        AssertFailure(result, "invalid_arguments");
        AssertNoUiCalls();
        Assert.IsFalse(result.Stderr.Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Replay_RequiresEveryDeclaredParameter()
    {
        WriteFlow();
        var result = await ReplayAsync([OutputPath, "-a", "TestApp", "--accept-recording", "--json"]);

        AssertFailure(result, "missing_parameter");
        AssertNoUiCalls();
    }

    [TestMethod]
    public async Task Replay_RejectsOverlongParameterBeforeResolving()
    {
        WriteFlow();
        var result = await ReplayAsync([
            OutputPath, "-a", "TestApp", "--accept-recording", "--json",
            "--parameter", $"text1={new string('x', UiActionFlowDocument.MaximumTextLength + 1)}",
        ]);

        AssertFailure(result, "invalid_flow");
        AssertNoUiCalls();
        Assert.IsTrue(result.Stderr.Length < 1000);
    }

    [TestMethod]
    public async Task Replay_EmptyParameterClearsFieldAndUsesTimeoutDefault()
    {
        WriteFlow();
        var result = await ReplayAsync([OutputPath, "-w", "42", "--accept-recording", "--parameter", "text1=", "--json"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsNotNull(_actions.ReplayOptions);
        Assert.IsTrue(_actions.ReplayOptions.AcceptRecording);
        Assert.AreEqual(5000, _actions.ReplayOptions.TimeoutMs);
        Assert.AreEqual("", _actions.ReplayOptions.Parameters["text1"]);
        Assert.AreEqual(42L, _resolver.LastWindow);
    }

    [TestMethod]
    public async Task Replay_RepeatedParametersRemainLiteralAndNeverChangeTheFlowFile()
    {
        const string first = "%USERPROFILE%=not-expanded";
        const string second = "$(not-executed); private value";
        WriteFlow(Flow() with
        {
            ProcessName = "DoNotLaunchOrSelectThisProcess",
            Steps = [ClickStep(), TextStep(), TextStep() with { Parameter = "second" }],
        });
        var original = await File.ReadAllBytesAsync(OutputPath, TestContext.CancellationToken);
        var result = await ReplayAsync([
            OutputPath, "-a", "DifferentRunningApp", "--accept-recording", "--timeout", "6000",
            "--parameter", $"text1={first}", "--parameter", $"second={second}", "--json",
        ]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsNotNull(_actions.ReplayOptions);
        Assert.AreEqual(first, _actions.ReplayOptions.Parameters["text1"]);
        Assert.AreEqual(second, _actions.ReplayOptions.Parameters["second"]);
        Assert.AreEqual(6000, _actions.ReplayOptions.TimeoutMs);
        Assert.AreEqual("DifferentRunningApp", _resolver.LastApp);
        Assert.AreSame(_resolver.Target, _actions.ReplayTarget);
        Assert.AreEqual("DoNotLaunchOrSelectThisProcess", _actions.ReplayFlow?.ProcessName);
        Assert.IsFalse((result.Stdout + result.Stderr).Contains("not-expanded", StringComparison.Ordinal));
        Assert.IsFalse((result.Stdout + result.Stderr).Contains("private value", StringComparison.Ordinal));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(OutputPath, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Replay_IncompleteFlowIsRejectedAndNotModified()
    {
        WriteFlow(Flow() with { Status = "incomplete" });
        var original = await File.ReadAllTextAsync(OutputPath, TestContext.CancellationToken);

        var result = await ReplayAsync(ReplayArguments);

        AssertFailure(result, "incomplete_recording");
        AssertNoUiCalls();
        Assert.AreEqual(original, await File.ReadAllTextAsync(OutputPath, TestContext.CancellationToken));
    }

    [TestMethod]
    [DataRow("""{"schemaVersion":2,"processName":"App","steps":[]}""")]
    [DataRow("""{"processName":"App","launch":"private-command","steps":[]}""")]
    [DataRow("""{"processName":"App","processName":"Other","steps":[]}""")]
    [DataRow("""{"processName":"App","steps":[]}""")]
    [DataRow("""{"processName":"App","steps":[{"kind":"shell","target":{"automationId":"Save","controlType":"Button"}}]}""")]
    [DataRow("""{"processName":"App","steps":[{"kind":"key","keys":"win+r","target":{"automationId":"Save","controlType":"Button"}}]}""")]
    [DataRow("""{"processName":"App","steps":[{"kind":"text","text":"private","parameter":"text1","target":{"automationId":"Edit","controlType":"Edit"}}]}""")]
    [DataRow("""{"processName":"App","steps":[{"kind":"text","parameter":"text1","target":{"name":"private","controlType":"Edit"}}]}""")]
    [DataRow("""{"processName":"App","steps":[{"kind":"text","parameter":"text1","target":{"automationId":"Edit","controlType":"Edit"}}]}""")]
    [DataRow("""{"processName":"App","steps":[{"kind":"click","button":"left","target":{"automationId":"Save","controlType":"Button"}},{"kind":"key","keys":"alt+tab","target":{"automationId":"Save","controlType":"Button"}}]}""")]
    [DataRow("""{"processName":"App","steps":[{"kind":"click","button":"left","command":"private","target":{"automationId":"Save","controlType":"Button"}}]}""")]
    [DataRow("not-json-private")]
    public async Task Replay_ValidatesTheEntireUntrustedDocumentBeforeResolving(string document)
    {
        await File.WriteAllTextAsync(OutputPath, document, TestContext.CancellationToken);

        var result = await ReplayAsync(ReplayArguments);

        AssertFailure(result, "invalid_flow");
        AssertNoUiCalls();
        Assert.IsFalse(result.Stderr.Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Replay_MissingFileIsStructuredFailureBeforeResolving()
    {
        var result = await ReplayAsync(ReplayArguments);

        AssertFailure(result, "invalid_flow");
        AssertNoUiCalls();
    }

    [TestMethod]
    public async Task Replay_ReportsCompletedInputsAndManuallyAuthoredAssertions()
    {
        WriteFlow(Flow() with
        {
            Steps = [
                ClickStep(),
                new UiActionStep
                {
                    Kind = "assert", Target = new UiActionTarget { AutomationId = "Status", ControlType = "Text" },
                    Property = "Name", Expected = "Saved",
                },
            ],
        });
        _actions.ReplayResult = new UiActionReplayResult(2, 1);

        var result = await ReplayAsync([OutputPath, "-a", "TestApp", "--accept-recording", "--json"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("", result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.AreEqual("completed", json.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(2, json.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.AreEqual(1, json.RootElement.GetProperty("assertionsChecked").GetInt32());
        Assert.IsFalse(result.Stdout.Contains("passed", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Replay_TextResultDoesNotClaimAnAutomaticTestVerdict()
    {
        WriteFlow();
        var result = await ReplayAsync([OutputPath, "-a", "TestApp", "--accept-recording", "--parameter", "text1=private"]);

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stdout, "Replay inputs completed");
        StringAssert.Contains(result.Stdout, "0 manually authored assertions");
        StringAssert.Contains(result.Stdout, "not an automatic correctness or accessibility verdict");
        Assert.IsFalse(result.Stdout.Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("no_interactive_desktop")]
    [DataRow("foreground_not_target")]
    [DataRow("ambiguous_selector")]
    [DataRow("element_not_found")]
    [DataRow("target_moved")]
    [DataRow("input_failed")]
    public async Task Replay_PreservesTypedRuntimeFailuresWithoutSuccessOutput(string code)
    {
        WriteFlow();
        _actions.ReplayException = new UiActionException(code, "Replay stopped safely.");

        var result = await ReplayAsync(ReplayArguments);

        AssertFailure(result, code);
        Assert.AreEqual(1, _actions.ReplayCalls);
    }

    [TestMethod]
    public async Task Replay_CancellationProducesNonzeroStructuredError()
    {
        WriteFlow();
        _actions.ReplayException = new OperationCanceledException("private-value");

        var result = await ReplayAsync(ReplayArguments);

        AssertFailure(result, "cancelled");
        Assert.IsFalse(result.Stderr.Contains("private-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Replay_UnexpectedFailureDoesNotEchoExceptionPayload()
    {
        WriteFlow();
        _actions.ReplayException = new System.Runtime.InteropServices.COMException("private-value");

        var result = await ReplayAsync(ReplayArguments);

        AssertFailure(result, "internal_error");
        Assert.IsFalse(result.Stderr.Contains("private-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ActionCommands_MissingRuntimeTargetProducesNoTargetError()
    {
        WriteFlow();
        _resolver.Exception = new AppNotFoundException("private-window-title");

        var replay = await ReplayAsync(ReplayArguments);
        AssertFailure(replay, "no_target");
        var record = await RecordAsync(["-a", "TestApp", "--output", Path.Combine(_directory, "new.json"), "--json"]);
        AssertFailure(record, "no_target");
        Assert.AreEqual(0, _actions.RecordCalls);
        Assert.AreEqual(0, _actions.ReplayCalls);
        Assert.IsFalse((replay.Stderr + record.Stderr).Contains("private-window-title", StringComparison.Ordinal));
    }

    private string OutputPath => Path.Combine(_directory, "actions.json");
    private string[] ReplayArguments => [OutputPath, "-a", "TestApp", "--accept-recording", "--parameter", "text1=value", "--json"];

    private void WriteFlow(UiActionFlow? flow = null) => UiActionFlowDocument.WriteNew(OutputPath, flow ?? Flow());

    private static UiActionFlow Flow() => new()
    {
        ProcessName = "TestApp",
        Steps = [ClickStep(), TextStep()],
    };

    private static UiActionStep ClickStep() => new()
    {
        Kind = "click", Button = "left",
        Target = new UiActionTarget { AutomationId = "Edit", ControlType = "Edit" },
    };

    private static UiActionStep TextStep() => new()
    {
        Kind = "text", Parameter = "text1",
        Target = new UiActionTarget { AutomationId = "Edit", ControlType = "Edit" },
    };

    private Task<CommandResult> RecordAsync(string[] arguments) => InvokeAsync<UiRecordActionsCommand>(arguments, TestContext.CancellationToken);
    private Task<CommandResult> ReplayAsync(string[] arguments) => InvokeAsync<UiReplayCommand>(arguments, TestContext.CancellationToken);

    private async Task<CommandResult> InvokeAsync<T>(string[] arguments, CancellationToken cancellationToken) where T : Command
    {
        var stdoutStart = _console.Output.Length;
        var stderrStart = _stderr.GetStringBuilder().Length;
        var parsed = _services.GetRequiredService<T>().Parse(arguments);
        parsed.InvocationConfiguration.Output = _console.Profile.Out.Writer;
        parsed.InvocationConfiguration.Error = _stderr;
        var exitCode = await parsed.InvokeAsync(parsed.InvocationConfiguration, cancellationToken: cancellationToken);
        return new CommandResult(exitCode, _console.Output[stdoutStart..], _stderr.ToString()[stderrStart..]);
    }

    private static void AssertFailure(CommandResult result, string code)
    {
        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual("", result.Stdout);
        var lines = result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var json = JsonDocument.Parse(lines[^1]);
        Assert.AreEqual(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static void AssertStartedEvent(string stderr)
    {
        using var json = JsonDocument.Parse(stderr.Trim());
        Assert.AreEqual("recording-actions-started", json.RootElement.GetProperty("event").GetString());
    }

    private void AssertNoUiCalls()
    {
        Assert.AreEqual(0, _resolver.Calls);
        Assert.AreEqual(0, _actions.RecordCalls);
        Assert.AreEqual(0, _actions.ReplayCalls);
    }

    private async Task WaitForStopAsync(CancellationToken captureToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, captureToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
            Assert.Fail("Capture did not receive a stop signal.");
        }
        catch (OperationCanceledException) when (captureToken.IsCancellationRequested) { }
    }

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);

    private sealed class TargetResolverFake : IUiTargetResolver
    {
        public Task<UiTarget> ResolveProcessAsync(string app, CancellationToken ct) => throw new NotSupportedException();

        public int Calls { get; private set; }
        public string? LastApp { get; private set; }
        public long? LastWindow { get; private set; }
        public Exception? Exception { get; set; }
        public UiTarget Target { get; } = new() { ProcessId = 1234, ProcessName = "TestApp", WindowHandle = 42 };

        public Task<UiTarget> ResolveAsync(string? app, long? hwnd, CancellationToken ct)
        {
            Calls++;
            LastApp = app;
            LastWindow = hwnd;
            if (Exception is not null) { throw Exception; }
            return Task.FromResult(Target);
        }
    }

    private sealed class ActionServiceFake : IUiActionService
    {
        public int RecordCalls { get; private set; }
        public int ReplayCalls { get; private set; }
        public UiActionRecordOptions? RecordOptions { get; private set; }
        public UiActionReplayOptions? ReplayOptions { get; private set; }
        public UiActionFlow? ReplayFlow { get; private set; }
        public UiTarget? ReplayTarget { get; private set; }
        public UiActionRecordResult RecordResult { get; set; } = new(Flow(), "stopped");
        public UiActionReplayResult ReplayResult { get; set; } = new(2, 0);
        public Exception? ReplayException { get; set; }
        public Func<CancellationToken, Action?, Task<UiActionRecordResult>>? OnRecord { get; set; }

        public Task<UiActionRecordResult> RecordAsync(UiTarget target, UiActionRecordOptions options, CancellationToken cancellationToken, Action? onReady = null)
        {
            RecordCalls++;
            RecordOptions = options;
            if (OnRecord is not null) { return OnRecord(cancellationToken, onReady); }
            onReady?.Invoke();
            return Task.FromResult(RecordResult);
        }

        public Task<UiActionReplayResult> ReplayAsync(UiTarget target, UiActionFlow flow, UiActionReplayOptions options, CancellationToken cancellationToken)
        {
            ReplayCalls++;
            ReplayOptions = options;
            ReplayFlow = flow;
            ReplayTarget = target;
            if (ReplayException is not null) { throw ReplayException; }
            return Task.FromResult(ReplayResult);
        }
    }

    private sealed class ObservedReader(string value) : StringReader(value)
    {
        public TaskCompletionSource ReadObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override string? ReadLine()
        {
            var line = base.ReadLine();
            ReadObserved.TrySetResult();
            return line;
        }
    }
}
