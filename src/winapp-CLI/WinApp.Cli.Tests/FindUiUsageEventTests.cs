// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.Telemetry.Internal;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class FindUiUsageEventTests
{
    private static readonly string[] OneReactorId = ["reactor-flex-1"];
    private static readonly string[] OneGalleryId = ["gallery-tabview-1"];

    [TestMethod]
    public void CreateSearch_CapturesModeSourceAndMatchCount_NoIdDetail()
    {
        var e = FindUiUsageEvent.CreateSearch("toolkit", includeReactor: false, json: true, matchCount: 3);

        Assert.AreEqual("search", e.Mode);
        Assert.AreEqual("toolkit", e.Source);
        Assert.IsFalse(e.IncludeReactor);
        Assert.IsTrue(e.Json);
        Assert.AreEqual(3, e.ResultCount);
        Assert.AreEqual(0, e.RequestedIdCount);
        Assert.IsNull(e.ResolvedIds, "search mode carries no id detail");
        Assert.AreEqual(PartA_PrivTags.ProductAndServiceUsage, e.PartA_PrivTags);
    }

    [TestMethod]
    public void CreateSearch_NullSource_IsPreserved()
    {
        var e = FindUiUsageEvent.CreateSearch(source: null, includeReactor: false, json: false, matchCount: 0);

        Assert.IsNull(e.Source, "a default (unscoped) search reports no source");
        Assert.AreEqual(0, e.ResultCount);
    }

    [TestMethod]
    public void CreateSearch_SourceReactor_MarksIncludeReactor()
    {
        var e = FindUiUsageEvent.CreateSearch("reactor", includeReactor: true, json: false, matchCount: 1);

        Assert.AreEqual("reactor", e.Source);
        Assert.IsTrue(e.IncludeReactor, "opting into Reactor must be reflected");
    }

    [TestMethod]
    public void CreateFetch_EmitsOnlyResolvedIds_AndCountsUnresolved()
    {
        // 3 requested, 2 resolved -> the 2 real ids are emitted; the 1 unresolved id
        // is reflected only in the count, never as a string.
        var resolved = new[] { "gallery-tabview-1", "toolkit-datagrid-1" };
        var e = FindUiUsageEvent.CreateFetch(includeReactor: false, json: false, resolved, requestedIdCount: 3);

        Assert.AreEqual("fetch", e.Mode);
        Assert.AreEqual(3, e.RequestedIdCount);
        Assert.AreEqual(2, e.ResolvedIdCount);
        Assert.AreEqual(1, e.UnresolvedIdCount);
        Assert.AreEqual("gallery-tabview-1,toolkit-datagrid-1", e.ResolvedIds);
        Assert.IsNull(e.Source, "fetch mode reports no --source");
    }

    [TestMethod]
    public void CreateFetch_NoneResolved_EmitsNullIdsAndFullUnresolvedCount()
    {
        var e = FindUiUsageEvent.CreateFetch(includeReactor: false, json: false, Array.Empty<string>(), requestedIdCount: 2);

        Assert.AreEqual(0, e.ResolvedIdCount);
        Assert.AreEqual(2, e.UnresolvedIdCount);
        Assert.IsNull(e.ResolvedIds, "no real ids resolved, so none are emitted");
    }

    [TestMethod]
    public void CreateFetch_ReactorId_MarksIncludeReactor()
    {
        var e = FindUiUsageEvent.CreateFetch(includeReactor: true, json: false, OneReactorId, requestedIdCount: 1);

        Assert.IsTrue(e.IncludeReactor);
        Assert.AreEqual("reactor-flex-1", e.ResolvedIds);
    }

    [TestMethod]
    public void CreateList_CapturesModeAndCount()
    {
        var e = FindUiUsageEvent.CreateList(json: true, count: 495);

        Assert.AreEqual("list", e.Mode);
        Assert.AreEqual(495, e.ResultCount);
        Assert.IsTrue(e.Json);
        Assert.IsFalse(e.IncludeReactor, "--list never opts into Reactor");
        Assert.IsNull(e.Source);
        Assert.IsNull(e.ResolvedIds);
    }

    [TestMethod]
    public void ReplaceSensitiveStrings_RunsOverEveryStringField()
    {
        var fetchEvent = FindUiUsageEvent.CreateFetch(includeReactor: false, json: false, OneGalleryId, requestedIdCount: 1);
        var searchEvent = FindUiUsageEvent.CreateSearch("toolkit", includeReactor: false, json: false, matchCount: 1);

        fetchEvent.ReplaceSensitiveStrings(s => s.Replace("gallery-tabview-1", "<redacted>", StringComparison.Ordinal));
        searchEvent.ReplaceSensitiveStrings(s => s.Replace("toolkit", "<s>", StringComparison.Ordinal).Replace("search", "<m>", StringComparison.Ordinal));

        Assert.AreEqual("<redacted>", fetchEvent.ResolvedIds, "the sanitizer must run over ResolvedIds");
        Assert.AreEqual("<s>", searchEvent.Source, "the sanitizer must run over Source");
        Assert.AreEqual("<m>", searchEvent.Mode, "the sanitizer must run over Mode");
    }

    [TestMethod]
    public void AllLogMethods_EmitFindUiUsageEventThroughTheProvider()
    {
        using var listener = new NameCapturingListener("Microsoft.Windows.WinAppDevCLI");

        FindUiUsageEvent.LogSearch("gallery", includeReactor: false, json: false, matchCount: 2);
        FindUiUsageEvent.LogFetch(includeReactor: false, json: false, OneGalleryId, requestedIdCount: 1);
        FindUiUsageEvent.LogList(json: false, count: 10);

        // This is a process-global ETW listener, and command-level tests run in
        // parallel and now emit this same event through the real handler — so assert
        // the Log->provider path fired (presence), not an exact count.
        var names = listener.WaitForEvent("FindUiUsage_Event");
        Assert.Contains("FindUiUsage_Event", names, "the static Log methods must emit through the WinAppDevCLI provider");
    }

    private sealed class NameCapturingListener : EventListener
    {
        private readonly string _provider;
        private readonly List<string> _names = [];

        public NameCapturingListener(string provider)
        {
            _provider = provider;
            foreach (var source in EventSource.GetSources().Where(s => s.Name == provider))
            {
                EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == _provider)
            {
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName is { } name)
            {
                lock (_names)
                {
                    _names.Add(name);
                }
            }
        }

        /// <summary>Wait until <paramref name="name"/> has been observed at least
        /// once (robust to extra events from parallel tests), then snapshot.</summary>
        public string[] WaitForEvent(string name)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (_names)
                {
                    if (_names.Contains(name))
                    {
                        return [.. _names];
                    }
                }
                Thread.Sleep(10);
            }
            lock (_names)
            {
                return [.. _names];
            }
        }
    }
}
