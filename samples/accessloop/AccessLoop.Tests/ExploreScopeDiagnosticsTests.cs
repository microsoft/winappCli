using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class ExploreScopeDiagnosticsTests
{
    [TestMethod]
    public void NativeStartupStopCollectsReadOnlyObservationsWithoutStartingHooks()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Native desktop diagnostics require Windows.");
        var source = new ExploreNativeWindowSource();
        try
        {
            source.EnableDiagnostics("unused-diagnostic-path");
            _ = source.Read(new(Environment.ProcessId, 0, "test", DateTimeOffset.UtcNow, null, null));
            source.RecordDiagnosticStop(new(DateTimeOffset.UtcNow, "monitor-unavailable", "startup stopped", 0));
        }
        finally { source.Dispose(); }

        var observations = source.DiagnosticSnapshot()
            .Where(entry => entry.Source == "post-stop-poll-before-app-cleanup").ToArray();
        Assert.IsNotEmpty(observations);
        Assert.IsTrue(observations.All(entry => entry.DesktopError is not null
            || (entry.InputDesktop is not null && entry.InputDesktopReceivesInput.HasValue)));
    }

    [TestMethod]
    public void TimelineKeepsOneSecondBeforeStopAndRetainsReadOnlyEvidenceAfterStop()
    {
        var root = Directory.CreateTempSubdirectory("accessloop-scope-diagnostics-");
        try
        {
            var path = Path.Combine(root.FullName, "scope-diagnostics.json");
            using var source = new ExploreNativeWindowSource();
            source.EnableDiagnostics(path);
            source.SetDiagnosticStep("guides-on");
            var now = Stopwatch.GetTimestamp();
            source.RecordDiagnostic(new(DateTimeOffset.UtcNow, now - 2 * Stopwatch.Frequency, "expired"));
            source.RecordDiagnostic(new(DateTimeOffset.UtcNow, now, "boundary-read", ForegroundWindow: 0));
            source.RecordDiagnosticStop(new(DateTimeOffset.UtcNow, "foreground-unavailable", "unknown", 0));
            source.RecordDiagnostic(new(DateTimeOffset.UtcNow, now + 1, "post-stop-poll-before-app-cleanup", ForegroundWindow: 42));
            source.WriteDiagnostics();

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var entries = document.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            Assert.AreEqual(3, entries.Length);
            Assert.IsFalse(entries.Any(entry => entry.GetProperty("source").GetString() == "expired"));
            Assert.IsTrue(entries.All(entry => entry.GetProperty("stepId").GetString() == "guides-on"));
            Assert.IsTrue(entries.Any(entry => entry.GetProperty("source").GetString() == "guard-stop"));
            Assert.IsTrue(entries.Any(entry => entry.GetProperty("foregroundWindow").ValueKind == JsonValueKind.Number
                && entry.GetProperty("foregroundWindow").GetInt64() == 42));
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void DiagnosticBuffersAreBoundedAndDiscloseDroppedEntries()
    {
        var root = Directory.CreateTempSubdirectory("accessloop-scope-bounds-");
        try
        {
            var path = Path.Combine(root.FullName, "scope-diagnostics.json");
            using var source = new ExploreNativeWindowSource();
            source.EnableDiagnostics(path);
            var now = Stopwatch.GetTimestamp();
            for (var index = 0; index < 300; index++)
                source.RecordDiagnostic(new(DateTimeOffset.UtcNow, now, "boundary-read"));
            source.RecordDiagnosticStop(new(DateTimeOffset.UtcNow, "foreground-unavailable", "unknown", 0));
            for (var index = 0; index < 300; index++)
                source.RecordDiagnostic(new(DateTimeOffset.UtcNow, now, "post-stop-poll-before-app-cleanup"));
            source.WriteDiagnostics();

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual(512, document.RootElement.GetProperty("entries").GetArrayLength());
            Assert.AreEqual(44, document.RootElement.GetProperty("beforeCapacityDropped").GetInt32());
            Assert.AreEqual(45, document.RootElement.GetProperty("afterCapacityDropped").GetInt32());
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void NativeFailuresKeepOperationErrorCodeAndCallStack()
    {
        using var source = new ExploreNativeWindowSource();
        source.EnableDiagnostics("unused-diagnostic-path");
        source.RecordDiagnosticFailure("GetMessage", new Win32Exception(5));
        var entry = source.DiagnosticSnapshot().Single();
        Assert.AreEqual("GetMessage", entry.Source);
        Assert.AreEqual(5, entry.ErrorCode);
        Assert.AreEqual(typeof(Win32Exception).FullName, entry.ExceptionType);
        Assert.IsFalse(string.IsNullOrWhiteSpace(entry.StackTrace));
    }
}
