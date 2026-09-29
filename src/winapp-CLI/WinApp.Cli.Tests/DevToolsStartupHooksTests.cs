// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public class DevToolsStartupHooksTests
{
    private const string Agent = @"C:\ProgramData\winapp\engine\abc\WinApp.DevTools.Managed.dll";
    private const string Source = @"C:\winapp\WinApp.DevTools.Managed.dll";
    private const string Developer = @"C:\tools\MyDiagnostics.dll";

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void Compose_WithoutHooks_AddsAgent(string? existing) =>
        Assert.AreEqual(Agent, DevToolsStartupHooks.Compose(existing, Agent));

    [TestMethod]
    public void Compose_ReplacesOnlyKnownOwnedPaths()
    {
        var existing = string.Join(Path.PathSeparator, Developer, Source.ToUpperInvariant(), Agent, Source);
        Assert.AreEqual(string.Join(Path.PathSeparator, Developer, Agent),
            DevToolsStartupHooks.Compose(existing, Agent, Source));
    }

    [TestMethod]
    public void Compose_PreservesSameFilenameAndUnprovenOldPaths()
    {
        const string unrelated = @"C:\developer\WinApp.DevTools.Managed.dll";
        const string old = @"C:\ProgramData\winapp\engine\unknown\WinApp.DevTools.Managed.dll";
        var existing = string.Join(Path.PathSeparator, Developer, unrelated, old, "AssemblyNamedHook");
        Assert.AreEqual(string.Join(Path.PathSeparator, existing, Agent), DevToolsStartupHooks.Compose(existing, Agent, Source));
    }

    [TestMethod]
    public void Compose_CanonicalizesOwnedPaths_AndIsRepeatable()
    {
        var existing = string.Join(Path.PathSeparator, Developer, @"C:\winapp\child\..\WinApp.DevTools.Managed.dll");
        var once = DevToolsStartupHooks.Compose(existing, Agent, Source);
        Assert.AreEqual(string.Join(Path.PathSeparator, Developer, Agent), once);
        Assert.AreEqual(once, DevToolsStartupHooks.Compose(once, Agent, Source));
    }
}
