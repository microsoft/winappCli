// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.Tracing;
using System.Reflection;
using System.Text;
using System.Text.Json;
using WinApp.DevTools.Managed;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ReactorEventsTests
{
    [TestInitialize]
    public void Reset() => ReactorEvents.ResetForTests();

    [TestMethod]
    public void Translate_ReportsErrorsWithoutMessages()
    {
        var record = ReactorEvents.Translate("RenderError", EventLevel.Error,
            ["componentName", "exceptionType", "message"], ["Counter", "System.InvalidOperationException", ""])!;
        Assert.AreEqual("renderError", record.Kind);
        Assert.AreEqual("error", record.Level);
        Assert.IsTrue(record.Log);
        var json = JsonDocument.Parse(ReactorEvents.ToJson(record, 7, DateTime.UtcNow)).RootElement;
        Assert.AreEqual("Counter", json.GetProperty("component").GetString());
        Assert.AreEqual("System.InvalidOperationException", json.GetProperty("exception").GetString());
        Assert.IsFalse(json.TryGetProperty("message", out _), "an exception message is never reported");
        Assert.AreEqual(7, json.GetProperty("seq").GetInt64());
    }

    [TestMethod]
    [DataRow("StateChange", "state", false)]
    [DataRow("ComponentUnmount", "unmount", false)]
    [DataRow("SwallowedError", "swallowedError", true)]
    [DataRow("HResultFailed", "hresult", true)]
    [DataRow("Warning", "warning", true)]
    [DataRow("HotReloadStateMigrated", "hotReload", true)]
    [DataRow("WindowOpened", "windowOpened", true)]
    [DataRow("WindowClosed", "windowClosed", true)]
    [DataRow("ComponentRendered", "rendered", false)]
    public void Translate_KnownEvents(string name, string kind, bool log)
    {
        var record = ReactorEvents.Translate(name, EventLevel.Informational, ["hr"], [unchecked((int)0x80004005)])!;
        Assert.AreEqual(kind, record.Kind);
        Assert.AreEqual(log, record.Log);
        if (name == "HResultFailed")
        {
            Assert.AreEqual("0x80004005", record.Fields.Single(field => field.Name == "hresult").Value);
        }
    }

    [TestMethod]
    public void Translate_IgnoresQuietEvents_AndKeepsUnknownWarnings()
    {
        Assert.IsNull(ReactorEvents.Translate("ReconcileStart", EventLevel.Informational, null, null));
        var other = ReactorEvents.Translate("ThemeApplyFailed", EventLevel.Warning, ["targetType"], ["Button"])!;
        Assert.AreEqual("other", other.Kind);
        Assert.IsTrue(other.Log);
    }

    [TestMethod]
    public void ToJson_PassesEventTextOnAsReactorWroteIt()
    {
        var warning = new ReactorEventRecord("warning", "warning", "Warning", true,
            [("message", "could not connect: authToken=abc123"), ("category", "Network")]);
        var json = JsonDocument.Parse(ReactorEvents.ToJson(warning, 1, DateTime.UtcNow)).RootElement;
        Assert.AreEqual("could not connect: authToken=abc123", json.GetProperty("message").GetString());
        Assert.AreEqual("Network", json.GetProperty("category").GetString());

        var other = ReactorEvents.Translate("LoginFailed", EventLevel.Warning, ["user", "authToken"], ["ada", "abc123"])!;
        var detail = JsonDocument.Parse(ReactorEvents.ToJson(other, 2, DateTime.UtcNow)).RootElement.GetProperty("detail").GetString();
        Assert.AreEqual("user=ada; authToken=abc123", detail);
    }

    [TestMethod]
    public void ToJson_BoundsLongText()
    {
        var record = new ReactorEventRecord("warning", "warning", "Warning", true, [("message", new string('x', 5000))]);
        var json = JsonDocument.Parse(ReactorEvents.ToJson(record, 1, DateTime.UtcNow)).RootElement;
        Assert.AreEqual(ReactorEvents.MaximumFieldLength, json.GetProperty("message").GetString()!.Length);
    }

    [TestMethod]
    public void Publish_KeepsOnlyLogEntries_UpToCapacity()
    {
        ReactorEvents.Publish(new ReactorEventRecord("state", "verbose", "StateChange", false, []));
        for (var i = 0; i < ReactorEvents.Capacity + 3; i++)
        {
            ReactorEvents.Publish(new ReactorEventRecord("warning", "warning", "Warning", true, [("message", "w" + i)]));
        }
        var (entries, dropped) = ReactorEvents.Snapshot();
        Assert.AreEqual(ReactorEvents.Capacity, entries.Count);
        Assert.AreEqual(3, dropped);
        StringAssert.Contains(entries[^1], "w" + (ReactorEvents.Capacity + 2));
    }

    [TestMethod]
    public void Listener_ObservesTheReactorEventSource()
    {
        ReactorEvents.Start();
        Assert.IsNull(TestReactorEventSource.Log.ConstructionException);
        Assert.IsTrue(TestReactorEventSource.Log.IsEnabled(EventLevel.Warning, (EventKeywords)0x20), "the listener enables Reactor's error keyword");
        TestReactorEventSource.Log.Warning("Theme", "Resolve", "Brush key missing");
        var (entries, _) = ReactorEvents.Snapshot();
        Assert.IsTrue(entries.Any(entry => entry.Contains("Brush key missing", StringComparison.Ordinal)), string.Join("\n", entries));
    }

    [TestMethod]
    public void HotReload_IsReportedAsALogEntry()
    {
        ReactorEvents.UpdateApplication([typeof(string)]);
        var (entries, _) = ReactorEvents.Snapshot();
        StringAssert.Contains(entries.Single(), "\"metadataUpdate\"");
    }

    [TestMethod]
    public async Task Serve_ReplaysTheLog_ThenStreams()
    {
        ReactorEvents.Publish(new ReactorEventRecord("renderError", "error", "RenderError", true, [("component", "Counter")]));
        var stream = new MemoryStream();
        var step = 0;
        await ReactorEvents.ServeAsync(stream, 0, _ =>
        {
            if (step++ == 0)
            {
                ReactorEvents.Publish(new ReactorEventRecord("windowOpened", "info", "WindowOpened", true, [("windowType", "ReactorWindow")]));
                Thread.Sleep(100);
                return "MASK 1";
            }
            return null;
        });
        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(3, lines.Length, string.Join("\n", lines));
        StringAssert.StartsWith(lines[0], "EVENT {");
        StringAssert.Contains(lines[0], "\"replay\":true");
        StringAssert.Contains(lines[1], "\"kind\":\"ready\"");
        StringAssert.Contains(lines[2], "ReactorWindow");
    }

    private sealed record FakeKeyedListDiagnostic(DateTime TimestampUtc, string? ControlContext, FakeKind Kind,
        IReadOnlyList<string> SampleKeys, int Count);

    private enum FakeKind { NullKey, DuplicateKey }

    private sealed record FakeSourceLocation(string FilePath, int LineNumber, int ColumnNumber);

    // A newer Reactor's shape: the warning also names its component and the list's C# call site.
    private sealed record FakeKeyedListDiagnosticWithSite(DateTime TimestampUtc, string? ControlContext, FakeKind Kind,
        IReadOnlyList<string> SampleKeys, int Count, string? ComponentName, FakeSourceLocation? CallSite);

    [TestMethod]
    [DoNotParallelize]
    public void KeyedLists_PollReportsWhatChangedInReactorsList_FirstTimesMarkedForTheBadge()
    {
        var at = new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc);
        var source = new List<object>
        {
            new FakeKeyedListDiagnosticWithSite(at, "ItemsRepeater", FakeKind.NullKey, ["k"], 1, "TaskList",
                new(@"C:\src\App\TaskList.cs", 42, 13)),
        };
        var published = new List<string>();
        var (originalSource, originalSink) = (ReactorEvents.KeyedLists.Source, ReactorEvents.KeyedLists.Sink);
        ReactorEvents.KeyedLists.Source = () => source;
        ReactorEvents.KeyedLists.Sink = record => published.Add(ReactorEvents.ToJson(record, published.Count + 1, at));
        try
        {
            ReactorEvents.KeyedLists.Poll();
            Assert.HasCount(1, published);
            foreach (var expected in new[] { "\"kind\":\"keyedList\"", "\"problem\":\"nullKey\"", "\"component\":\"TaskList\"",
                "\"file\":\"C:\\\\src\\\\App\\\\TaskList.cs\"", "\"line\":42", "\"column\":13", "\"first\":true" })
            {
                StringAssert.Contains(published[0], expected);
            }

            ReactorEvents.KeyedLists.Poll();
            Assert.HasCount(1, published, "an unchanged list reports nothing new");

            // The list changes: the same bailout fires twice more, and another list goes wrong (an older Reactor shape).
            source[0] = ((FakeKeyedListDiagnosticWithSite)source[0]) with { Count = 3 };
            source.Add(new FakeKeyedListDiagnostic(at.AddSeconds(1), "GridView", FakeKind.DuplicateKey, ["a", "a"], 1));
            ReactorEvents.KeyedLists.Poll();
            Assert.HasCount(3, published);
            StringAssert.Contains(published[1], "\"count\":3");
            Assert.IsFalse(published[1].Contains("\"first\"", StringComparison.Ordinal), "a repeat is logged, not counted again");
            StringAssert.Contains(published[2], "\"first\":true");
            Assert.IsFalse(published[2].Contains("\"component\"", StringComparison.Ordinal) || published[2].Contains("\"file\"", StringComparison.Ordinal),
                "nothing is made up when Reactor doesn't say");

            // What Reactor does when a list fires again: the same entry with a later time and a higher count. Still not first.
            source[1] = ((FakeKeyedListDiagnostic)source[1]) with { TimestampUtc = at.AddSeconds(30), Count = 2 };
            ReactorEvents.KeyedLists.Poll();
            Assert.HasCount(4, published);
            StringAssert.Contains(published[3], "\"count\":2");
            Assert.IsFalse(published[3].Contains("\"first\"", StringComparison.Ordinal), "a list is first only once, whatever its time");

            // CONTROL: a count that did not grow, at a time already seen, is not a new report.
            source[0] = ((FakeKeyedListDiagnosticWithSite)source[0]) with { Count = 2 };
            ReactorEvents.KeyedLists.Poll();
            Assert.HasCount(4, published);
        }
        finally
        {
            (ReactorEvents.KeyedLists.Source, ReactorEvents.KeyedLists.Sink) = (originalSource, originalSink);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void KeyedLists_AReactorWithoutTheListOrWithAChangingOneReportsNothingAndDoesNotThrow()
    {
        var published = new List<ReactorEventRecord>();
        var (originalSource, originalSink) = (ReactorEvents.KeyedLists.Source, ReactorEvents.KeyedLists.Sink);
        ReactorEvents.KeyedLists.Sink = published.Add;
        try
        {
            ReactorEvents.KeyedLists.Source = () => null;
            ReactorEvents.KeyedLists.Poll();
            ReactorEvents.KeyedLists.Source = () => throw new TargetInvocationException(new InvalidOperationException());
            ReactorEvents.KeyedLists.Poll();
            Assert.IsEmpty(published);
        }
        finally
        {
            (ReactorEvents.KeyedLists.Source, ReactorEvents.KeyedLists.Sink) = (originalSource, originalSink);
        }
    }

    [TestMethod]
    public void KeyedLists_ReportEachBailoutOnceAndAgainWhenItFiresMore_WithoutTheKeys()
    {
        var at = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        var first = ReactorEvents.KeyedLists.Translate(new FakeKeyedListDiagnostic(at, "ListView", FakeKind.DuplicateKey, ["alice@example.com", "alice@example.com"], 1))!;
        Assert.AreEqual("keyedList", first.Kind);
        Assert.IsTrue(first.Log);
        var json = ReactorEvents.ToJson(first, 1, at);
        StringAssert.Contains(json, "\"problem\":\"duplicateKey\"");
        StringAssert.Contains(json, "\"keys\":2");
        Assert.IsFalse(json.Contains("alice", StringComparison.Ordinal), "keys are app data and are not reported");
        Assert.IsNull(ReactorEvents.KeyedLists.Translate(new FakeKeyedListDiagnostic(at, "ListView", FakeKind.DuplicateKey, ["x"], 1)),
            "the same bailout is reported once");
        Assert.IsNotNull(ReactorEvents.KeyedLists.Translate(new FakeKeyedListDiagnostic(at, "ListView", FakeKind.DuplicateKey, ["x"], 5)),
            "and again when it fired more often");
    }

    [EventSource(Name = "Microsoft-UI-Reactor")]
    private sealed class TestReactorEventSource : EventSource
    {
        public static readonly TestReactorEventSource Log = new();

        public static class Keywords
        {
            public const EventKeywords Errors = (EventKeywords)0x20;
        }

        [Event(39, Level = EventLevel.Warning, Keywords = Keywords.Errors)]
        public void Warning(string category, string operation, string message) => WriteEvent(39, category, operation, message);
    }
}
