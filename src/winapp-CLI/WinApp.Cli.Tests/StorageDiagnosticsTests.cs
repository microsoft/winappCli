// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class StorageDiagnosticsTests
{
    [TestMethod]
    public void Warning_DeduplicatesConcurrentIdenticalReports()
    {
        using var error = new StringWriter();
        var diagnostics = new StorageDiagnostics(error);

        Parallel.For(0, 20, _ => diagnostics.Warning("cache_fallback", "Using the local cache."));

        Assert.AreEqual($"Warning: Using the local cache.{Environment.NewLine}", error.ToString());
    }

    [TestMethod]
    public void Warning_Json_IsOneStructuredDocumentPerDiagnostic()
    {
        using var error = new StringWriter();
        var diagnostics = new StorageDiagnostics(error, json: true);

        diagnostics.Warning("cache_fallback", "Using C:\\work\\\"cache\".");

        using var doc = JsonDocument.Parse(error.ToString());
        Assert.AreEqual("cache_fallback", doc.RootElement.GetProperty("warning").GetProperty("code").GetString());
        Assert.AreEqual("Using C:\\work\\\"cache\".", doc.RootElement.GetProperty("warning").GetProperty("message").GetString());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void DeferredWarnings_AreOnlyPublishedAfterSuccess(bool succeeded)
    {
        using var error = new StringWriter();
        var diagnostics = new StorageDiagnostics(error, json: true, deferWarnings: true);
        diagnostics.Warning("cache_fallback", "Using the local cache.");
        Assert.AreEqual(string.Empty, error.ToString());

        diagnostics.Complete(succeeded);
        var output = error.ToString();
        diagnostics.Complete(succeeded);
        diagnostics.Warning("late_warning", "Must not change a completed invocation.");

        Assert.AreEqual(output, error.ToString());
        Assert.AreEqual(succeeded, output.Length > 0);
        if (succeeded)
        {
            using var document = JsonDocument.Parse(output);
            Assert.AreEqual("cache_fallback", document.RootElement.GetProperty("warning").GetProperty("code").GetString());
        }
    }

    [TestMethod]
    public void Warning_Quiet_DoesNotWrite()
    {
        using var error = new StringWriter();
        var diagnostics = new StorageDiagnostics(error, quiet: true);

        diagnostics.Warning("cache_fallback", "Using local cache.");

        Assert.AreEqual(string.Empty, error.ToString());
    }
}
