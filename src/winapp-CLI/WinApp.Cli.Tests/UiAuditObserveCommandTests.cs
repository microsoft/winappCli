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
public sealed class UiAuditObserveCommandTests : IDisposable
{
    private readonly TestConsole _console = new();
    private readonly StringWriter _error = new();
    private readonly ResolverFake _resolver = new();
    private readonly ObservationFake _observation = new();
    private ServiceProvider _services = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _services = new ServiceCollection()
            .UseCommandHandler<UiAuditObserveCommand, UiAuditObserveCommand.Handler>()
            .AddSingleton<IAnsiConsole>(_console)
            .AddSingleton<IUiTargetResolver>(_resolver)
            .AddSingleton<IUiAuditObservationService>(_observation)
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.None))
            .BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _services?.Dispose();
        _console.Dispose();
        _error.Dispose();
    }

    [TestMethod]
    public async Task InputConsentIsRequiredBeforeTargetResolution()
    {
        var result = await Run(["-a", "123", "--tab-steps", "1", "--json"]);

        AssertFailure(result, "input_opt_in_required");
        AssertNoEffects();
    }

    [TestMethod]
    public async Task InputConsentPrecedesMissingTargetValidation()
    {
        var result = await Run(["--tab-steps", "2", "--json"]);

        AssertFailure(result, "input_opt_in_required");
        AssertNoEffects();
    }

    [TestMethod]
    public async Task ReadOnlyDefaultsAreFiveSecondsAndFiveHundredEvents()
    {
        var result = await Run(["-a", "123", "--json"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("", result.Stderr);
        Assert.AreEqual("123", _resolver.App);
        Assert.IsNull(_resolver.Window);
        Assert.IsNotNull(_observation.Options);
        Assert.AreEqual(5, _observation.Options.DurationSec);
        Assert.AreEqual(0, _observation.Options.TabSteps);
        Assert.AreEqual(500, _observation.Options.MaxEvents);
        Assert.IsFalse(_observation.Options.AcceptInput);
        Assert.AreSame(_resolver.Target, _observation.Target);
    }

    [TestMethod]
    public async Task AcceptedProbePassesLimitsWithoutChangingTheirPerDirectionMeaning()
    {
        var result = await Run([
            "-w", "456", "--duration-sec", "60", "--tab-steps", "30", "--max-events", "5000", "--accept-input", "--json",
        ]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(456L, _resolver.Window);
        Assert.IsNull(_resolver.App);
        Assert.IsNotNull(_observation.Options);
        Assert.AreEqual(60, _observation.Options.DurationSec);
        Assert.AreEqual(30, _observation.Options.TabSteps);
        Assert.AreEqual(5000, _observation.Options.MaxEvents);
        Assert.IsTrue(_observation.Options.AcceptInput);
    }

    [TestMethod]
    [DataRow("--duration-sec=0")]
    [DataRow("--duration-sec=61")]
    [DataRow("--tab-steps=-1")]
    [DataRow("--tab-steps=31")]
    [DataRow("--max-events=0")]
    [DataRow("--max-events=5001")]
    [DataRow("--duration-sec=private-malformed")]
    [DataRow("--tab-steps=private-malformed")]
    [DataRow("--max-events=private-malformed")]
    [DataRow("--duration-sec")]
    [DataRow("--tab-steps")]
    [DataRow("--max-events")]
    public async Task InvalidLimitsAreStructuredErrorsBeforeResolution(string option)
    {
        var result = await Run(["-a", "123", "--accept-input", option, "--json"]);

        AssertFailure(result, "invalid_arguments");
        AssertNoEffects();
        Assert.IsFalse(result.Stderr.Contains("private-malformed", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("TestApp")]
    [DataRow("2147483648")]
    public async Task ProcessTargetMustBeAPositiveNumericPid(string app)
    {
        var result = await Run(["-a", app, "--json"]);

        AssertFailure(result, "invalid_arguments");
        AssertNoEffects();
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("private-malformed")]
    [DataRow("9223372036854775808")]
    public async Task InvalidWindowHandlesAreStructuredErrorsBeforeResolution(string window)
    {
        var result = await Run(["-w", window, "--json"]);

        AssertFailure(result, "invalid_arguments");
        AssertNoEffects();
        Assert.IsFalse(result.Stderr.Contains("private-malformed", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MissingTargetAndConflictingTargetsHaveNoEffects()
    {
        AssertFailure(await Run(["--json"]), "missing_app");
        AssertFailure(await Run(["-a", "123", "-w", "456", "--json"]), "invalid_arguments");
        AssertNoEffects();
    }

    [TestMethod]
    public async Task JsonHasTheAgreedCamelCaseContractAndSeparateNativeEvents()
    {
        _observation.Result = _observation.Result with
        {
            FocusTrace =
            [
                new UiAuditFocusPoint
                {
                    Sequence = 0, Direction = "initial", NodeId = "o1", RuntimeId = "[2A,7705FE,4,6]",
                    NameWithheld = true, IsInTarget = true, ObservedAtUtc = DateTimeOffset.UnixEpoch,
                },
            ],
            Events =
            [
                new UiAuditEvent
                {
                    Sequence = 1, Kind = "notification", NodeId = "o1", RuntimeId = "[2A,7705FE,4,6]",
                    ReceivedAtUtc = DateTimeOffset.UnixEpoch, NotificationKind = 2, NotificationProcessing = 1,
                    NameWithheld = true, DisplayStringWithheld = true, ActivityIdWithheld = true,
                },
            ],
        };
        var result = await Run(["-a", "123", "--json"]);

        Assert.AreEqual(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(123, root.GetProperty("processId").GetInt32());
        Assert.AreEqual(456L, root.GetProperty("windowHandle").GetInt64());
        Assert.AreEqual("completed", root.GetProperty("status").GetString());
        Assert.AreEqual(5, root.GetProperty("durationSec").GetInt32());
        Assert.AreEqual(0, root.GetProperty("requestedTabSteps").GetInt32());
        Assert.AreEqual(0, root.GetProperty("completedTabSteps").GetInt32());
        Assert.AreEqual(500, root.GetProperty("maxEvents").GetInt32());
        Assert.IsFalse(root.GetProperty("eventLimitReached").GetBoolean());
        Assert.AreEqual("duration_elapsed", root.GetProperty("stopReason").GetString());
        Assert.AreEqual(DateTimeOffset.UnixEpoch, root.GetProperty("startedAtUtc").GetDateTimeOffset());
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddSeconds(5), root.GetProperty("endedAtUtc").GetDateTimeOffset());
        var focus = root.GetProperty("focusTrace")[0];
        Assert.AreEqual("initial", focus.GetProperty("direction").GetString());
        Assert.AreEqual("[2A,7705FE,4,6]", focus.GetProperty("runtimeId").GetString());
        Assert.AreEqual(JsonValueKind.Null, focus.GetProperty("automationId").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, focus.GetProperty("name").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, focus.GetProperty("isPassword").ValueKind);
        Assert.IsTrue(focus.GetProperty("nameWithheld").GetBoolean());
        var nativeEvent = root.GetProperty("events")[0];
        Assert.AreEqual("notification", nativeEvent.GetProperty("kind").GetString());
        Assert.AreEqual(2, nativeEvent.GetProperty("notificationKind").GetInt32());
        Assert.AreEqual(1, nativeEvent.GetProperty("notificationProcessing").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, nativeEvent.GetProperty("displayString").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, nativeEvent.GetProperty("activityId").ValueKind);
        Assert.IsTrue(nativeEvent.GetProperty("displayStringWithheld").GetBoolean());
        Assert.AreEqual(0, root.GetProperty("issues").GetArrayLength());
        Assert.IsFalse(root.TryGetProperty("ProcessId", out _));
    }

    [TestMethod]
    [DataRow("cancelled")]
    [DataRow("duration_elapsed")]
    [DataRow("event_limit")]
    [DataRow("no_interactive_desktop")]
    [DataRow("focus_left_target")]
    [DataRow("coverage_limited")]
    public async Task PartialResultsStillReturnJsonWithNonzeroExit(string reason)
    {
        _observation.Result = _observation.Result with
        {
            Status = "partial",
            StopReason = reason,
            CompletedTabSteps = 1,
            RequestedTabSteps = 3,
            Issues = [new UiAuditObservationIssue(reason, "Collection ended before all observations completed.")],
        };
        var result = await Run(["-a", "123", "--json"]);

        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual("", result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.AreEqual("partial", document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(reason, document.RootElement.GetProperty("stopReason").GetString());
        Assert.AreEqual(1, document.RootElement.GetProperty("completedTabSteps").GetInt32());
        Assert.AreEqual(reason, document.RootElement.GetProperty("issues")[0].GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task AcceptedTextModeExplainsStateChangesAndNoRestoration()
    {
        var result = await Run(["-a", "123", "--tab-steps", "1", "--accept-input"]);

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, "can change business state");
        StringAssert.Contains(result.Stderr, "No state will be restored");
        StringAssert.Contains(result.Stdout, "do not establish an accessibility verdict");
    }

    [TestMethod]
    public async Task QuietModeSuppressesSuccessfulTextOutput()
    {
        var result = await Run(["-a", "123", "--quiet"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("", result.Stdout);
        Assert.AreEqual("", result.Stderr);
    }

    [TestMethod]
    public async Task ResolverFailuresDoNotEchoProviderDetails()
    {
        _resolver.Failure = new InvalidOperationException("private-provider-title");
        var result = await Run(["-a", "123", "--json"]);

        AssertFailure(result, "no_target");
        Assert.AreEqual(0, _observation.Calls);
        Assert.IsFalse(result.Stderr.Contains("private-provider-title", StringComparison.Ordinal));
    }

    [TestMethod]
    public void HelpExplainsReadOnlyPrivacyAndPerDirectionLimits()
    {
        var command = _services.GetRequiredService<UiAuditObserveCommand>();
        Assert.IsNotNull(command.Description);
        StringAssert.Contains(command.Description, "read-only for 5 seconds");
        StringAssert.Contains(command.Description, "N Tab then N Shift+Tab");
        StringAssert.Contains(command.Description, "Keyboard probing requires an unlocked interactive desktop");
        StringAssert.Contains(command.Description, "read-only event observation does not require input focus");
        StringAssert.Contains(command.Description, "notification text");
        StringAssert.Contains(command.Description, "partial JSON");
    }

    private async Task<CommandResult> Run(string[] arguments)
    {
        var stdoutStart = _console.Output.Length;
        var stderrStart = _error.GetStringBuilder().Length;
        var parsed = _services.GetRequiredService<UiAuditObserveCommand>().Parse(arguments);
        parsed.InvocationConfiguration.Output = _console.Profile.Out.Writer;
        parsed.InvocationConfiguration.Error = _error;
        var exit = await parsed.InvokeAsync(parsed.InvocationConfiguration, cancellationToken: TestContext.CancellationToken);
        return new CommandResult(exit, _console.Output[stdoutStart..], _error.ToString()[stderrStart..]);
    }

    private void AssertNoEffects()
    {
        Assert.AreEqual(0, _resolver.Calls);
        Assert.AreEqual(0, _observation.Calls);
    }

    private static void AssertFailure(CommandResult result, string expectedCode)
    {
        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual("", result.Stdout);
        using var error = JsonDocument.Parse(result.Stderr);
        Assert.AreEqual(expectedCode, error.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);

    private sealed class ResolverFake : IUiTargetResolver
    {
        internal UiTarget Target { get; } = new() { ProcessId = 123, WindowHandle = 456 };
        internal string? App { get; private set; }
        internal long? Window { get; private set; }
        internal int Calls { get; private set; }
        internal Exception? Failure { get; set; }

        public Task<UiTarget> ResolveAsync(string? app, long? hwnd, CancellationToken ct)
        {
            Calls++;
            App = app;
            Window = hwnd;
            if (Failure is not null)
            {
                throw Failure;
            }
            return Task.FromResult(Target);
        }
    }

    private sealed class ObservationFake : IUiAuditObservationService
    {
        internal int Calls { get; private set; }
        internal UiTarget? Target { get; private set; }
        internal UiAuditObservationOptions? Options { get; private set; }
        internal UiAuditObservationResult Result { get; set; } = new()
        {
            ProcessId = 123, WindowHandle = 456, DurationSec = 5, MaxEvents = 500,
            StartedAtUtc = DateTimeOffset.UnixEpoch, EndedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(5),
            Status = "completed", StopReason = "duration_elapsed",
        };

        public Task<UiAuditObservationResult> ObserveAsync(UiTarget target, UiAuditObservationOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Target = target;
            Options = options;
            return Task.FromResult(Result);
        }
    }
}
