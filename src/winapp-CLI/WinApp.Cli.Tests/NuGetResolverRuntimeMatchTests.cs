// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

[TestClass]
public class NuGetResolverRuntimeMatchTests
{
    private const string Stable25 = @"C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime.2_2.5.1.0_arm64__8wekyb3d8bbwe";
    private const string Stable264 = @"C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime.2_2.6.4.0_arm64__8wekyb3d8bbwe";
    private const string Stable265 = @"C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime.2_2.6.5.0_arm64__8wekyb3d8bbwe";
    private const string Stable27 = @"C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime.2_2.7.0.0_arm64__8wekyb3d8bbwe";
    private const string Experimental30 = @"C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime.3-experimental5_3.0.0.0_arm64__8wekyb3d8bbwe";
    private const string Stable18 = @"C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime.1.8_8000.994.2142.0_arm64__8wekyb3d8bbwe";

    private static readonly string[] InstalledRuntimes = [Experimental30, Stable25, Stable264, Stable265, Stable27, Stable18];

    [TestMethod]
    public void SelectWinAppSdkRuntime_ProjectRelease_PicksTheMatchingRuntimeOverNewerOnes()
    {
        // A 2.6 project on a machine that also has 2.7 and 3.0-experimental must be answered
        // from 2.6, not reported as a mismatch against the newest runtime.
        Assert.AreEqual(Stable265, NuGetResolver.SelectWinAppSdkRuntime(InstalledRuntimes, "2.6.5"));
        Assert.AreEqual(Stable27, NuGetResolver.SelectWinAppSdkRuntime(InstalledRuntimes, "2.7.0"));
        Assert.AreEqual(Stable18, NuGetResolver.SelectWinAppSdkRuntime(InstalledRuntimes, "1.8.260222000"));
    }

    [TestMethod]
    public void SelectWinAppSdkRuntime_NoProject_PrefersNewestStableOverNewerExperimental()
    {
        Assert.AreEqual(Stable27, NuGetResolver.SelectWinAppSdkRuntime(InstalledRuntimes, null));
    }

    [TestMethod]
    public void SelectWinAppSdkRuntime_NoMatchingRuntime_FallsBackToNewestStable()
    {
        // The caller compares this choice to the project and reports the skip.
        Assert.AreEqual(Stable27, NuGetResolver.SelectWinAppSdkRuntime(InstalledRuntimes, "2.4.0"));
    }

    [TestMethod]
    public void SelectWinAppSdkRuntime_OnlyExperimentalInstalled_UsesIt()
    {
        Assert.AreEqual(Experimental30, NuGetResolver.SelectWinAppSdkRuntime([Experimental30], null));
        Assert.IsNull(NuGetResolver.SelectWinAppSdkRuntime([], null));
    }

    [TestMethod]
    public void RuntimeMatchesRelease_SameRelease_Matches()
    {
        // The installed runtime is labelled by release ("1.8"); the project references a
        // full package version ("1.8.260222000"). They are the same release.
        Assert.IsTrue(NuGetResolver.RuntimeMatchesRelease("1.8", "1.8.260222000"));
    }

    [TestMethod]
    public void RuntimeMatchesRelease_DifferentRelease_DoesNotMatch()
    {
        // Runtime detection picks the newest runtime on the machine, but a project
        // compiles against the release it references. Indexing a 2.4 runtime for a 1.8
        // project makes 'find-api' confirm types that release does not have, and an agent
        // then writes code that does not build.
        Assert.IsFalse(NuGetResolver.RuntimeMatchesRelease("2.4", "1.8.260222000"));
    }

    [TestMethod]
    public void RuntimeMatchesRelease_NoProjectRequirement_TakesTheRuntimeAsIs()
    {
        // The machine-wide SDK scope is not tied to any project.
        Assert.IsTrue(NuGetResolver.RuntimeMatchesRelease("2.4", null));
    }

    [TestMethod]
    public void RuntimeMatchesRelease_UnparseableVersion_TakesTheRuntimeAsIs()
    {
        // Dropping metadata a project may need is worse than including it, so an
        // unrecognizable label on either side is not treated as a mismatch.
        Assert.IsTrue(NuGetResolver.RuntimeMatchesRelease("experimental", "1.8.260222000"));
        Assert.IsTrue(NuGetResolver.RuntimeMatchesRelease("1.8", "not-a-version"));
    }

    [TestMethod]
    public void RuntimeMatchesRelease_PrereleaseRuntimeLabel_ComparesTheReleaseBeforeTheSuffix()
    {
        // An experimental runtime's folder is labelled "2.0-experimental3". Parsing the
        // minor straight out of that fails, and a failed parse is treated as
        // "unrecognizable, take it as-is" — which waves the mismatch through and reopens
        // the very bug the release check exists to prevent.
        Assert.IsFalse(NuGetResolver.RuntimeMatchesRelease("2.0-experimental3", "1.8.260222000"));
        Assert.IsTrue(NuGetResolver.RuntimeMatchesRelease("1.8-experimental3", "1.8.260222000"));
        Assert.IsTrue(NuGetResolver.RuntimeMatchesRelease("1.8", "1.8.250401001-preview1"));
    }

    [TestMethod]
    public void ReferencedWinAppSdkVersion_SubPackageVersionsDrift_TakesTheNewestRelease()
    {
        // WinAppSDK 2.x ships as sub-packages whose versions drift apart inside one
        // release: 2.2.0 resolves Foundation 2.1.0, InteractiveExperiences 2.0.15 and
        // WinUI 2.2.1. The umbrella package carries no .winmd, so it never reaches this
        // list. Taking whichever sub-package comes first would compare an installed 2.2
        // runtime against "2.0" and exclude the runtime the project actually builds
        // against — silently dropping thousands of types from the answer.
        var packages = new List<PackageWithWinMd>
        {
            new("Microsoft.WindowsAppSDK.InteractiveExperiences", "2.0.15", [], []),
            new("Microsoft.WindowsAppSDK.Foundation", "2.1.0", [], []),
            new("Microsoft.WindowsAppSDK.WinUI", "2.2.1", [], []),
        };

        string? referenced = NuGetResolver.ReferencedWinAppSdkVersion(packages);

        Assert.AreEqual("2.2.1", referenced);
        Assert.IsTrue(NuGetResolver.RuntimeMatchesRelease("2.2", referenced));
        Assert.IsFalse(NuGetResolver.RuntimeMatchesRelease("2.4", referenced));
    }

    [TestMethod]
    public void ReferencedWinAppSdkVersion_NoWinAppSdkPackages_IsNull()
    {
        // A plain .NET or Win32 project must not be answered from WinAppSDK metadata it
        // does not build against, so a null here is what keeps the runtime out entirely.
        var packages = new List<PackageWithWinMd>
        {
            new("Microsoft.Web.WebView2", "1.0.3179.45", [], []),
        };

        Assert.IsNull(NuGetResolver.ReferencedWinAppSdkVersion(packages));
    }
}
