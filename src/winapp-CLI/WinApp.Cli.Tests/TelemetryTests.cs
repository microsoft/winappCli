// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.CommandLine;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.Telemetry;
using Microsoft.Diagnostics.Telemetry.Internal;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Telemetry;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Tests;

[DoNotParallelize]
[TestClass]
public sealed class TelemetryTests
{
    private const string ProviderName = "Microsoft.Windows.WinAppDevCLI";
    private string? _originalOptOut;
    private string? _originalCaller;

    [TestInitialize]
    public void SaveEnvironment()
    {
        _originalOptOut = Environment.GetEnvironmentVariable("WINAPP_CLI_TELEMETRY_OPTOUT");
        _originalCaller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        Environment.SetEnvironmentVariable("WINAPP_CLI_TELEMETRY_OPTOUT", null);
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", null);
    }

    [TestCleanup]
    public void RestoreEnvironment()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_TELEMETRY_OPTOUT", _originalOptOut);
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", _originalCaller);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("0")]
    [DataRow("1")]
    public async Task GuestAgentStartup_OptsOutEvenForParseFailures(string? initialOptOut)
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_TELEMETRY_OPTOUT", initialOptOut);

        var result = await ProgramMainTestHarness.InvokeProgramAsync(
            ["--json", "guest-agent", "--port", "invalid"]);

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.AreEqual("1", Environment.GetEnvironmentVariable("WINAPP_CLI_TELEMETRY_OPTOUT"));
        Assert.IsFalse(new WinApp.Cli.Telemetry.Telemetry().IsTelemetryOn);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("0")]
    [DataRow("1")]
    public async Task HostInvocation_PreservesOptOutAndDoesNotTreatArgumentValuesAsGuestMode(string? initialOptOut)
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_TELEMETRY_OPTOUT", initialOptOut);

        var result = await ProgramMainTestHarness.InvokeProgramAsync(
            ["--json", "run", "--help", "--args", "guest-agent"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(initialOptOut, Environment.GetEnvironmentVariable("WINAPP_CLI_TELEMETRY_OPTOUT"));
        Assert.AreEqual(initialOptOut != "1", new WinApp.Cli.Telemetry.Telemetry().IsTelemetryOn);
    }

    [TestMethod]
    public void CommandEvents_EmitTargetKindInBothPayloads()
    {
        using var listener = new CapturingEventListener(ProviderName);
        var telemetry = new WinApp.Cli.Telemetry.Telemetry();
        var root = new RootCommand();
        root.Options.Add(ExecutionTargetSelection.OnOption);
        root.Subcommands.Add(new Command("run"));
        var command = root.Parse("run --on sandbox", WinAppParserConfiguration.Default).CommandResult;

        telemetry.Log("CommandInvoked_Event", LogLevel.Critical, new CommandInvokedEvent(command, DateTime.UtcNow));
        telemetry.Log("CommandCompleted_Event", LogLevel.Critical, new CommandCompletedEvent(command, DateTime.UtcNow, 70));

        var events = listener.WaitForEvents(2);
        AssertEvent(events, "CommandInvoked_Event", EventLevel.Verbose, TelemetryEventSource.CriticalDataKeyword,
            "ExecutionTargetKind", "sandbox");
        AssertEvent(events, "CommandCompleted_Event", EventLevel.Verbose, TelemetryEventSource.CriticalDataKeyword,
            "ExecutionTargetKind", "sandbox");
    }

    [TestMethod]
    public void AddSensitiveString_IgnoresUnsafeEntriesAndSanitizesEventBaseFields()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "caller-secret-tool");
        var telemetry = new WinApp.Cli.Telemetry.Telemetry();
        telemetry.AddSensitiveString("secret", "<redacted>");
        telemetry.AddSensitiveString("secret", "duplicate-is-ignored");
        telemetry.AddSensitiveString("abc", "too-short");
        telemetry.AddSensitiveString(" ", "blank");

        var data = new ProbeEvent
        {
            Detail = "SECRET data and abc remain",
            Caller = "caller-secret-tool",
            AgentName = "secret-agent",
        };

        telemetry.Log("SanitizeProbe", LogLevel.Local, data);

        Assert.AreEqual("<redacted> data and abc remain", data.Detail);
        Assert.AreEqual("caller-<redacted>-tool", data.Caller);
        Assert.AreEqual("<redacted>-agent", data.AgentName);
    }

    [TestMethod]
    public void Log_MapsLevelsAndDiagnosticFlagToExpectedEventOptions()
    {
        using var listener = new CapturingEventListener(ProviderName);
        var telemetry = new WinApp.Cli.Telemetry.Telemetry();
        var relatedActivityId = Guid.NewGuid();

        telemetry.Log("InfoWithoutDiagnostics", LogLevel.Info, new ProbeEvent { Detail = "info" }, relatedActivityId);
        telemetry.IsDiagnosticTelemetryOn = true;
        telemetry.Log("InfoWithDiagnostics", LogLevel.Info, new ProbeEvent { Detail = "diagnostic" }, relatedActivityId);
        telemetry.Log("MeasureWithDiagnostics", LogLevel.Measure, new ProbeEvent { Detail = "measure" }, relatedActivityId);
        telemetry.Log("CriticalWithDiagnostics", LogLevel.Critical, new ProbeEvent { Detail = "critical" }, relatedActivityId);
        telemetry.LogError("InfoError", LogLevel.Info, new ProbeEvent { Detail = "info-error" }, relatedActivityId);
        telemetry.LogError("MeasureError", LogLevel.Measure, new ProbeEvent { Detail = "measure-error" }, relatedActivityId);
        telemetry.LogError("CriticalError", LogLevel.Critical, new ProbeEvent { Detail = "critical-error" }, relatedActivityId);
        telemetry.LogError("LocalError", LogLevel.Local, new ProbeEvent { Detail = "local-error" }, relatedActivityId);

        var events = listener.WaitForEvents(8);

        AssertEvent(events, "InfoWithoutDiagnostics", EventLevel.Verbose, EventKeywords.None, "Detail", "info");
        AssertEvent(events, "InfoWithDiagnostics", EventLevel.Verbose, TelemetryEventSource.TelemetryKeyword, "Detail", "diagnostic");
        AssertEvent(events, "MeasureWithDiagnostics", EventLevel.Verbose, TelemetryEventSource.MeasuresKeyword, "Detail", "measure");
        AssertEvent(events, "CriticalWithDiagnostics", EventLevel.Verbose, TelemetryEventSource.CriticalDataKeyword, "Detail", "critical");
        AssertEvent(events, "InfoError", EventLevel.Error, TelemetryEventSource.TelemetryKeyword, "Detail", "info-error");
        AssertEvent(events, "MeasureError", EventLevel.Error, TelemetryEventSource.MeasuresKeyword, "Detail", "measure-error");
        AssertEvent(events, "CriticalError", EventLevel.Error, TelemetryEventSource.CriticalDataKeyword, "Detail", "critical-error");
        AssertEvent(events, "LocalError", EventLevel.Error, EventKeywords.None, "Detail", "local-error");
    }

    [TestMethod]
    public void TelemetryOptOut_DowngradesCriticalToLocalEventOptions()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_TELEMETRY_OPTOUT", "1");
        using var listener = new CapturingEventListener(ProviderName);
        var telemetry = new WinApp.Cli.Telemetry.Telemetry();

        telemetry.Log("OptedOutCritical", LogLevel.Critical, new ProbeEvent { Detail = "local-only" });
        telemetry.LogError("OptedOutCriticalError", LogLevel.Critical, new ProbeEvent { Detail = "local-error" });

        var events = listener.WaitForEvents(2);

        Assert.IsFalse(telemetry.IsTelemetryOn);
        AssertEvent(events, "OptedOutCritical", EventLevel.Verbose, EventKeywords.None, "Detail", "local-only");
        AssertEvent(events, "OptedOutCriticalError", EventLevel.Error, EventKeywords.None, "Detail", "local-error");
    }

    [TestMethod]
    public void ConvenienceMethods_EmitExpectedTelemetryEvents()
    {
        using var listener = new CapturingEventListener(ProviderName);
        var telemetry = new WinApp.Cli.Telemetry.Telemetry();
        telemetry.AddSensitiveString("sensitive", "<s>");

        telemetry.LogTimeTaken("build-sensitive", 42);
        telemetry.LogException("pack-sensitive", CreateExceptionWithInner());
        telemetry.LogCritical("critical-usage");
        telemetry.LogCritical("critical-error", isError: true);

        var events = listener.WaitForEvents(4);

        AssertEvent(events, "TimeTaken", EventLevel.Verbose, TelemetryEventSource.CriticalDataKeyword, "EventName", "build-<s>");
        AssertEvent(events, "TimeTaken", EventLevel.Verbose, TelemetryEventSource.CriticalDataKeyword, "TimeTakenMilliseconds", 42u);
        AssertEvent(events, "ExceptionThrown", EventLevel.Error, TelemetryEventSource.CriticalDataKeyword, "Action", "pack-<s>");
        AssertEvent(events, "ExceptionThrown", EventLevel.Error, TelemetryEventSource.CriticalDataKeyword, "Message", "outer <s>");
        AssertEvent(events, "ExceptionThrown", EventLevel.Error, TelemetryEventSource.CriticalDataKeyword, "InnerMessage", "inner <s>");
        AssertEvent(events, "critical-usage", EventLevel.Verbose, TelemetryEventSource.CriticalDataKeyword, "PartA_PrivTags", PartA_PrivTags.ProductAndServiceUsage);
        AssertEvent(events, "critical-error", EventLevel.Error, TelemetryEventSource.CriticalDataKeyword, "PartA_PrivTags", PartA_PrivTags.ProductAndServiceUsage);
    }

    [TestMethod]
    public void AddWellKnownSensitiveStrings_ScrubsUserProfilePathFromLoggedEvents()
    {
        using var listener = new CapturingEventListener(ProviderName);
        var telemetry = new WinApp.Cli.Telemetry.Telemetry();

        // Probe with the user-profile path rather than the machine name: it is always a real, absolute
        // path well over the 3-char minimum that AddSensitiveString requires before it registers a value
        // (Telemetry.cs guards name.Length > 3). A machine name can be 1-3 chars on some hosts and would
        // be silently skipped by that guard, making a machine-name assertion host-dependent/flaky.
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        telemetry.AddWellKnownSensitiveStrings();
        telemetry.Log("AfterWellKnownSensitiveStrings", LogLevel.Local, new ProbeEvent { Detail = userProfile });

        var events = listener.WaitForEvents(1);
        var match = events.FirstOrDefault(e => e.Name == "AfterWellKnownSensitiveStrings");
        Assert.IsNotNull(match, "Expected the probe event to be emitted.");
        var detail = match!.Payload.TryGetValue("Detail", out var value) ? value as string : null;
        Assert.IsFalse(string.IsNullOrEmpty(detail), "Probe event should carry a Detail payload.");
        Assert.IsFalse(detail!.Contains(userProfile, StringComparison.Ordinal),
            "AddWellKnownSensitiveStrings must scrub the raw user-profile path from telemetry payloads; a no-op would leak it.");
        StringAssert.Contains(detail!, "<UserDirectory>",
            "The scrubbed user-profile path should be replaced with the <UserDirectory> placeholder.");
    }

    private static InvalidOperationException CreateExceptionWithInner()
    {
        try
        {
            ThrowOuter();
            throw new AssertFailedException("unreachable");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }

        static void ThrowOuter()
        {
            try
            {
                throw new ArgumentException("inner sensitive");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("outer sensitive", ex);
            }
        }
    }

    private static void AssertEvent(IReadOnlyCollection<CapturedEvent> events, string eventName, EventLevel level, EventKeywords keywords, string payloadName, object? expectedValue)
    {
        var match = events.FirstOrDefault(e => e.Name == eventName && e.Payload.TryGetValue(payloadName, out var actual) && Equals(actual, expectedValue));
        Assert.IsNotNull(match, $"Expected {eventName} to contain {payloadName}={expectedValue}. Actual: {string.Join("; ", events.Select(e => e.ToString()))}");
        Assert.AreEqual(level, match.Level, $"Unexpected level for {eventName}.");
        Assert.AreEqual(keywords, match.Keywords, $"Unexpected keywords for {eventName}.");
    }

    [EventData]
    private sealed class ProbeEvent : EventBase
    {
        public string? Detail { get; set; }

        public override PartA_PrivTags PartA_PrivTags => PartA_PrivTags.ProductAndServiceUsage;

        public override void ReplaceSensitiveStrings(Func<string, string> replaceSensitiveStrings)
        {
            if (Detail != null)
            {
                Detail = replaceSensitiveStrings(Detail);
            }
        }
    }

    private sealed class CapturingEventListener : EventListener
    {
        private readonly string _providerName;
        private readonly ConcurrentQueue<CapturedEvent> _events = new();

        public CapturingEventListener(string providerName)
        {
            _providerName = providerName;
            foreach (var source in EventSource.GetSources().Where(s => s.Name == providerName))
            {
                EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == _providerName)
            {
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            var payload = new Dictionary<string, object?>();
            if (eventData.PayloadNames != null && eventData.Payload != null)
            {
                for (var i = 0; i < eventData.PayloadNames.Count; i++)
                {
                    payload[eventData.PayloadNames[i]] = eventData.Payload[i];
                }
            }

            _events.Enqueue(new CapturedEvent(eventData.EventName ?? string.Empty, eventData.Level, eventData.Keywords, payload));
        }

        public CapturedEvent[] WaitForEvents(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (_events.Count < count && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }

            var result = _events.ToArray();
            Assert.IsTrue(result.Length >= count, $"Timed out waiting for {count} telemetry events. Captured {result.Length}: {string.Join("; ", result.Select(e => e.ToString()))}");
            return result;
        }
    }

    private sealed record CapturedEvent(string Name, EventLevel Level, EventKeywords Keywords, IReadOnlyDictionary<string, object?> Payload)
    {
        public override string ToString() => $"{Name} Level={Level} Keywords={Keywords} Payload=[{string.Join(", ", Payload.Select(p => $"{p.Key}={p.Value}"))}]";
    }
}
