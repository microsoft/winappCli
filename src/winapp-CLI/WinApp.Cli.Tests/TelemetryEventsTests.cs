// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics.Tracing;
using System.Text.Json;
using Microsoft.Diagnostics.Telemetry;
using Microsoft.Diagnostics.Telemetry.Internal;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class TelemetryEventsTests
{
    [TestMethod]
    public void CommandInvokedEvent_SerializesArgumentsOptionsAndSanitizesContext()
    {
        var commandResult = ParseCommand("secret-value --count 5 --path C:\\secret\\file.txt --switch").CommandResult;
        var started = new DateTime(2026, 7, 15, 1, 2, 3, DateTimeKind.Utc);
        var telemetryEvent = new CommandInvokedEvent(commandResult, started)
        {
            Caller = "caller-secret",
            AgentName = "agent-secret",
        };

        telemetryEvent.ReplaceSensitiveStrings(s => s.Replace("secret", "<s>", StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(commandResult.Command.GetType().FullName, telemetryEvent.CommandName);
        Assert.AreEqual(started, telemetryEvent.StartedTime);
        Assert.AreEqual(PartA_PrivTags.ProductAndServiceUsage, telemetryEvent.PartA_PrivTags);
        Assert.AreEqual("caller-secret", telemetryEvent.Caller, "Event-specific sanitization is separate from Telemetry's cross-cutting EventBase fields.");
        Assert.AreEqual("agent-secret", telemetryEvent.AgentName);

        using var json = JsonDocument.Parse(telemetryEvent.Context);
        var root = json.RootElement;
        Assert.AreEqual("[string]", GetPropertyValue(root.GetProperty("Arguments"), "target")?.GetString());
        Assert.AreEqual("5", GetPropertyValue(root.GetProperty("Options"), "count")?.GetString());
        Assert.AreEqual("True", GetPropertyValue(root.GetProperty("Options"), "switch")?.GetString());
        Assert.AreEqual("[string]", GetPropertyValue(root.GetProperty("Options"), "path")?.GetString());
        Assert.IsFalse(telemetryEvent.Context.Contains("secret-value", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CommandInvokedEvent_RepresentsImplicitMissingAndErrorValues()
    {
        var missingRequired = ParseCommand(string.Empty).CommandResult;
        var missingEvent = new CommandInvokedEvent(missingRequired, DateTime.UnixEpoch);
        using (var json = JsonDocument.Parse(missingEvent.Context))
        {
            Assert.IsTrue(GetPropertyValue(json.RootElement.GetProperty("Arguments"), "target")?.ValueKind is JsonValueKind.Null);
            var countValue = GetPropertyValue(json.RootElement.GetProperty("Options"), "count");
            Assert.IsTrue(countValue is null || countValue.Value.ValueKind is JsonValueKind.Null);
        }

        var invalid = ParseCommand("provided --count not-an-int").CommandResult;
        var invalidEvent = new CommandInvokedEvent(invalid, DateTime.UnixEpoch);
        using var invalidJson = JsonDocument.Parse(invalidEvent.Context);
        Assert.AreEqual("[error]", GetPropertyValue(invalidJson.RootElement.GetProperty("Options"), "count")?.GetString());
    }

    [TestMethod]
    public void CommandInvokedEvent_RepresentsArgumentConversionErrors()
    {
        var root = new RootCommand("root")
        {
            new Argument<int>("number"),
        };

        var telemetryEvent = new CommandInvokedEvent(root.Parse("not-an-int", WinAppParserConfiguration.Default).CommandResult, DateTime.UnixEpoch);

        using var json = JsonDocument.Parse(telemetryEvent.Context);
        Assert.AreEqual("[error]", GetPropertyValue(json.RootElement.GetProperty("Arguments"), "number")?.GetString());
    }

    [TestMethod]
    public void CommandInvokedEvent_WhenContextCannotBeSerialized_StoresErrorContext()
    {
        var context = CommandInvokedEvent.CreateContext(ThrowingChildren());

        StringAssert.StartsWith(context, "[error parsing context]:");

        static IEnumerable<SymbolResult> ThrowingChildren()
        {
            throw new InvalidOperationException("synthetic parse failure");
#pragma warning disable CS0162 // Unreachable code: required to make this an iterator block.
            yield break;
#pragma warning restore CS0162
        }
    }

    [TestMethod]
    public void CommandCompletedEvent_StoresExitCodeTimeAndSanitizesCommandName()
    {
        var result = ParseCommand("target").CommandResult;
        var finished = new DateTime(2026, 7, 15, 4, 5, 6, DateTimeKind.Utc);
        var telemetryEvent = new CommandCompletedEvent(result, finished, 123);

        telemetryEvent.ReplaceSensitiveStrings(s => s.Replace("RootCommand", "Root", StringComparison.Ordinal));

        Assert.AreEqual(result.Command.GetType().FullName!.Replace("RootCommand", "Root", StringComparison.Ordinal), telemetryEvent.CommandName);
        Assert.AreEqual(finished, telemetryEvent.FinishedTime);
        Assert.AreEqual(123, telemetryEvent.ExitCode);
        Assert.AreEqual(PartA_PrivTags.ProductAndServiceUsage, telemetryEvent.PartA_PrivTags);
    }

    [TestMethod]
    public void StaticLogMethods_EmitCommandTelemetryThroughFactory()
    {
        using var listener = new NameCapturingEventListener("Microsoft.Windows.WinAppDevCLI");
        var commandResult = ParseCommand("target --count 1").CommandResult;

        CommandInvokedEvent.Log(commandResult);
        CommandCompletedEvent.Log(commandResult, 12);

        var names = listener.WaitForEventNames(2);
        CollectionAssert.Contains(names, "CommandInvoked_Event");
        CollectionAssert.Contains(names, "CommandCompleted_Event");
    }

    [TestMethod]
    public void ProjectContextEvent_StoresOnlyNormalizedCategories()
    {
        var telemetryEvent = new ProjectContextEvent(
            "run",
            new ProjectContext(
                ProjectFamily.Dotnet,
                ProjectAppFramework.WinUI,
                ProjectTargetKind.SourceProject,
                ProjectContextSource.NuGetMsBuild,
                ProjectContextConfidence.High,
                ProjectContextPackaging.Packaged,
                ProjectExecutionMode.Folder));

        telemetryEvent.ReplaceSensitiveStrings(value => value.Replace("run", "execute", StringComparison.Ordinal));

        Assert.AreEqual("execute", telemetryEvent.Command);
        Assert.AreEqual("dotnet", telemetryEvent.ProjectFamily);
        Assert.AreEqual("winui", telemetryEvent.AppFramework);
        Assert.AreEqual("source-project", telemetryEvent.TargetKind);
        Assert.AreEqual("nuget-msbuild", telemetryEvent.DetectionSource);
        Assert.AreEqual("high", telemetryEvent.Confidence);
        Assert.AreEqual("packaged", telemetryEvent.Packaging);
        Assert.AreEqual("folder", telemetryEvent.ExecutionMode);
        Assert.AreEqual(PartA_PrivTags.ProductAndServiceUsage, telemetryEvent.PartA_PrivTags);
    }

    [TestMethod]
    public void TimeTakenEvent_StoresDurationAndSanitizesName()
    {
        var telemetryEvent = new TimeTakenEvent("deploy-secret", 987);

        telemetryEvent.ReplaceSensitiveStrings(s => s.Replace("secret", "<s>", StringComparison.Ordinal));

        Assert.AreEqual("deploy-<s>", telemetryEvent.EventName);
        Assert.AreEqual(987u, telemetryEvent.TimeTakenMilliseconds);
        Assert.AreEqual(PartA_PrivTags.ProductAndServicePerformance, telemetryEvent.PartA_PrivTags);
    }

    [TestMethod]
    public void ExceptionThrownEvent_CapturesOuterInnerExceptionDataAndSanitizesMessages()
    {
        var exception = CaptureException();
        var telemetryEvent = new ExceptionThrownEvent("action-secret", exception, s => s.Replace("secret", "<s>", StringComparison.Ordinal));

        telemetryEvent.ReplaceSensitiveStrings(s => s.Replace("action", "act", StringComparison.Ordinal));

        Assert.AreEqual("act-secret", telemetryEvent.Action);
        Assert.AreEqual(nameof(InvalidOperationException), telemetryEvent.Name);
        Assert.AreEqual(nameof(ArgumentException), telemetryEvent.InnerName);
        Assert.AreEqual("outer <s>", telemetryEvent.Message);
        Assert.AreEqual("inner <s>", telemetryEvent.InnerMessage);
        StringAssert.Contains(telemetryEvent.StackTrace!, nameof(CaptureException));
        StringAssert.Contains(telemetryEvent.InnerStackTrace, nameof(ThrowInner));
        Assert.AreEqual(PartA_PrivTags.ProductAndServicePerformance, telemetryEvent.PartA_PrivTags);
    }

    [TestMethod]
    public void ExceptionThrownEvent_HandlesMissingInnerException()
    {
        var telemetryEvent = new ExceptionThrownEvent("plain", new ApplicationException("message"), s => s);

        Assert.IsNull(telemetryEvent.InnerName);
        Assert.IsNull(telemetryEvent.InnerMessage);
        Assert.AreEqual(string.Empty, telemetryEvent.InnerStackTrace);
    }

    [TestMethod]
    public void EmptyEvent_ExposesTagsAndHasNoSensitiveFieldsToMutate()
    {
        var telemetryEvent = new EmptyEvent(PartA_PrivTags.SoftwareSetupAndInventory);

        telemetryEvent.ReplaceSensitiveStrings(_ => throw new AssertFailedException("EmptyEvent must not call the sanitizer."));

        Assert.AreEqual(PartA_PrivTags.SoftwareSetupAndInventory, telemetryEvent.PartA_PrivTags);
        Assert.AreEqual(PrivacyProduct.WIN_APP_DEV_CLI, telemetryEvent.PartA_PrivacyProduct);
        Assert.AreNotEqual("Unknown", telemetryEvent.AppVersion);
        Assert.IsFalse(string.IsNullOrWhiteSpace(telemetryEvent.SenderOrigin));
    }

    [TestMethod]
    public void TelemetryEventSource_ConstructorsOptionsAndConstantsMatchExpectedEtwContract()
    {
        using var named = new TelemetryEventSource("WinApp.Cli.Tests.TelemetryEventSource.Named");
        using var grouped = new TelemetryEventSource("WinApp.Cli.Tests.TelemetryEventSource.Grouped", TelemetryGroup.WindowsCoreTelemetry);
        using var derived = new DerivedTelemetryEventSource();

        Assert.AreEqual("WinApp.Cli.Tests.TelemetryEventSource.Named", named.Name);
        Assert.AreEqual("WinApp.Cli.Tests.TelemetryEventSource.Grouped", grouped.Name);
        Assert.IsTrue(derived.Settings.HasFlag(EventSourceSettings.EtwSelfDescribingEventFormat));
        Assert.AreEqual(TelemetryEventSource.TelemetryKeyword, TelemetryEventSource.TelemetryOptions().Keywords);
        Assert.AreEqual(TelemetryEventSource.MeasuresKeyword, TelemetryEventSource.MeasuresOptions().Keywords);
    }

    private static JsonElement? GetPropertyValue(JsonElement container, string suffix)
    {
        foreach (var property in container.EnumerateObject())
        {
            if (property.Name.Equals(suffix, StringComparison.Ordinal) ||
                property.Name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static ParseResult ParseCommand(string commandLine)
    {
        var root = new RootCommand("root")
        {
            new Argument<string>("target"),
            new Option<int>("--count"),
            new Option<FileInfo>("--path"),
            new Option<bool>("--switch"),
        };
        return root.Parse(commandLine, WinAppParserConfiguration.Default);
    }

    private static InvalidOperationException CaptureException()
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
    }

    private static void ThrowOuter()
    {
        try
        {
            ThrowInner();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("outer secret", ex);
        }
    }

    private static void ThrowInner() => throw new ArgumentException("inner secret");

    private sealed class DerivedTelemetryEventSource : TelemetryEventSource
    {
    }

    private sealed class NameCapturingEventListener : EventListener
    {
        private readonly string _providerName;
        private readonly List<string> _names = [];

        public NameCapturingEventListener(string providerName)
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
            if (eventData.EventName != null)
            {
                lock (_names)
                {
                    _names.Add(eventData.EventName);
                }
            }
        }

        public string[] WaitForEventNames(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (_names)
                {
                    if (_names.Count >= count)
                    {
                        return [.. _names];
                    }
                }

                Thread.Sleep(10);
            }

            lock (_names)
            {
                Assert.Fail($"Timed out waiting for {count} events. Captured: {string.Join(", ", _names)}");
                return [.. _names];
            }
        }
    }
}
