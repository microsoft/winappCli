// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using NuGet.Versioning;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="NugetService.RangeSatisfiesWithFloat"/>, the float-aware point test winapp's
/// dependency resolver uses to decide whether a concrete version falls inside a declared range (honoring a
/// float's ceiling, prerelease eligibility and prefix via <c>FloatRange.Satisfies</c>). It is exercised
/// directly because a floating range (<c>1.*</c>) is lost when a package is authored through
/// <c>PackageBuilder</c> (its nuspec is written as <c>[1.0.0, )</c>), so a feed-authored end-to-end test
/// could not reproduce the floating case it guards against.
/// </summary>
[TestClass]
public class NugetServiceVersionRangeTests
{
    // VersionRange.Satisfies ignores a float's ceiling and prerelease eligibility (1.* reports satisfying both
    // 2.0.0 and 1.5.0-preview), so a diamond where the 2.* branch fixes the shared dependency first would be
    // silently accepted when the 1.* branch is later checked. RangeSatisfiesWithFloat defers to
    // FloatRange.Satisfies, which rejects a version above the band or an ineligible prerelease.
    [TestMethod]
    [DataRow("1.*", "2.0.0", false, DisplayName = "1.* does NOT satisfy 2.0.0 (above the floated ceiling)")]
    [DataRow("1.*", "1.5.0", true, DisplayName = "1.* satisfies an in-band 1.5.0")]
    [DataRow("1.*", "1.0.0", true, DisplayName = "1.* satisfies its floor 1.0.0")]
    [DataRow("1.*", "1.5.0-preview", false, DisplayName = "Stable 1.* does NOT satisfy an in-band prerelease")]
    [DataRow("2.*", "1.0.0", false, DisplayName = "2.* does NOT satisfy a below-floor 1.0.0")]
    [DataRow("1.2.*", "1.3.0", false, DisplayName = "1.2.* does NOT satisfy 1.3.0 (above the patch band)")]
    [DataRow("1.2.*", "1.2.9", true, DisplayName = "1.2.* satisfies an in-band 1.2.9")]
    [DataRow("1.2.3-beta.*", "1.2.3-rc.1", false, DisplayName = "beta-prefix float does NOT satisfy an rc prerelease")]
    [DataRow("[1.0.0, )", "2.0.0", true, DisplayName = "A non-floating open range is unaffected")]
    [DataRow("*", "999.0.0", true, DisplayName = "Unbounded major float (*) has no ceiling")]
    public void RangeSatisfiesWithFloat_EnforcesFloatedCeiling(string range, string version, bool expected)
    {
        Assert.AreEqual(
            expected,
            NugetService.RangeSatisfiesWithFloat(VersionRange.Parse(range), NuGetVersion.Parse(version)));
    }
}
