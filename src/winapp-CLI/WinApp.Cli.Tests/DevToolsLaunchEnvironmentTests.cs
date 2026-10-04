// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public class DevToolsLaunchEnvironmentTests
{
    private const string Source = @"C:\winapp\WinApp.DevTools.Managed.dll";
    private const string Staged = @"C:\ProgramData\winapp\engine\owned\WinApp.DevTools.Managed.dll";

    [TestMethod]
    public void ManagedEnvironment_PreservesDeveloperConfiguration_AndRemovesOwnWatchPid()
    {
        const string developerHook = @"C:\developer\WinApp.DevTools.Managed.dll";
        var inherited = new Dictionary<string, string?>
        {
            ["DOTNET_STARTUP_HOOKS"] = string.Join(Path.PathSeparator, developerHook, Source),
            ["DOTNET_MODIFIABLE_ASSEMBLIES"] = "developer-value",
            ["WINAPP_WATCH_PID"] = "123",
            ["UNRELATED"] = "keep",
        };
        var environment = DevToolsArtifacts.ComposeLaunchEnvironment(@"C:\source", Staged, Source, inherited["DOTNET_STARTUP_HOOKS"]);
        Assert.AreEqual("1", environment["ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO"]);
        Assert.AreEqual(@"C:\source", environment["WINAPP_DEVTOOLS_SOURCE_ROOT"]);
        Assert.IsNull(environment["WINAPP_WATCH_PID"]);
        Assert.IsFalse(environment.ContainsKey("DOTNET_MODIFIABLE_ASSEMBLIES"));
        foreach (var pair in environment)
        {
            if (pair.Value is null)
            {
                inherited.Remove(pair.Key);
            }
            else
            {
                inherited[pair.Key] = pair.Value;
            }
        }
        Assert.AreEqual("developer-value", inherited["DOTNET_MODIFIABLE_ASSEMBLIES"]);
        Assert.AreEqual("keep", inherited["UNRELATED"]);
        Assert.AreEqual(string.Join(Path.PathSeparator, developerHook, Staged), inherited["DOTNET_STARTUP_HOOKS"]);
        Assert.IsFalse(inherited.ContainsKey("WINAPP_WATCH_PID"));
    }

    [TestMethod]
    public void NativeEnvironment_DoesNotRequireHook_OrInventSourceRoot()
    {
        var environment = DevToolsArtifacts.ComposeLaunchEnvironment(null, null, Source, "developer");
        Assert.IsFalse(environment.ContainsKey("DOTNET_STARTUP_HOOKS"));
        Assert.IsFalse(environment.ContainsKey("WINAPP_DEVTOOLS_SOURCE_ROOT"));
        Assert.AreEqual("1", environment["ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO"]);
        Assert.IsNull(environment["WINAPP_WATCH_PID"]);
    }

    [TestMethod]
    public void WithoutAProject_CommentsLiveWhereWinappRan_NotInTheBuildOutput()
    {
        var unlinked = DevToolsArtifacts.ComposeLaunchEnvironment(null, null, Source, null, @"C:\src\Orders");
        Assert.AreEqual(@"C:\src\Orders", unlinked["WINAPP_DEVTOOLS_COMMENT_ROOT"]);
        Assert.IsFalse(unlinked.ContainsKey("WINAPP_DEVTOOLS_SOURCE_ROOT"), "the launch folder is not claimed as source");

        var linked = DevToolsArtifacts.ComposeLaunchEnvironment(@"C:\src\Orders\App", null, Source, null, @"C:\src\Orders");
        Assert.IsNull(linked["WINAPP_DEVTOOLS_COMMENT_ROOT"], "a project keeps its own store, and an inherited value is cleared");
    }
}
