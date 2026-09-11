// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.Telemetry.Internal;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class FindApiUsageEventTests
{
    [TestMethod]
    public void Create_CapturesVerbJsonCountAndFound()
    {
        var e = FindApiUsageEvent.Create("search", json: true, resultCount: 4, found: true);

        Assert.AreEqual("search", e.Verb);
        Assert.IsTrue(e.Json);
        Assert.AreEqual(4, e.ResultCount);
        Assert.IsTrue(e.Found);
        Assert.AreEqual(PartA_PrivTags.ProductAndServiceUsage, e.PartA_PrivTags);
    }

    [TestMethod]
    public void Create_DefaultsCountZeroAndFoundTrue()
    {
        var e = FindApiUsageEvent.Create("namespaces", json: false);

        Assert.AreEqual(0, e.ResultCount);
        Assert.IsTrue(e.Found, "verbs where 'found' has no meaning report found=true");
    }

    [TestMethod]
    public void Create_NotFound_IsPreserved()
    {
        var e = FindApiUsageEvent.Create("check-property", json: false, resultCount: 0, found: false);

        Assert.IsFalse(e.Found);
    }

    [TestMethod]
    public void ReplaceSensitiveStrings_RunsOverVerb()
    {
        var e = FindApiUsageEvent.Create("search", json: false);

        e.ReplaceSensitiveStrings(s => s.Replace("search", "<v>", StringComparison.Ordinal));

        Assert.AreEqual("<v>", e.Verb, "the sanitizer must run over Verb");
    }

    [TestMethod]
    public void Log_EmitsFindApiUsageEventThroughTheProvider()
    {
        using var listener = new NameCapturingListener("Microsoft.Windows.WinAppDevCLI");

        FindApiUsageEvent.Log("search", json: false, resultCount: 2, found: true);

        var names = listener.WaitForEvent("FindApiUsage_Event");
        CollectionAssert.Contains(names, "FindApiUsage_Event", "the static Log method must emit through the WinAppDevCLI provider");
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
