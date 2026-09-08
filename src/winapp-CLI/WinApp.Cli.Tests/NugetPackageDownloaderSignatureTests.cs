// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using static WinApp.Cli.Tests.NugetFeedTestHelpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for how client signature/trust policy failures are reported. NuGet throws
/// <c>SignatureException</c> with an EMPTY <c>Message</c> and writes the actual diagnosis through the logger
/// it was given, so a package rejected by <c>signatureValidationMode=require</c> used to reach the user as a
/// completely blank error from <c>init</c> / <c>restore</c>.
/// </summary>
[TestClass]
public class NugetPackageDownloaderSignatureTests : BaseCommandTests
{
    [TestMethod]
    public async Task InstallPackageAsync_RequireSignatureRejectsUnsignedPackage_ReportsWhyInsteadOfEmptyError()
    {
        // NUGET_PACKAGES takes precedence over the globalPackagesFolder written below.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // Test packages are unsigned, so requiring signatures rejects this one.
            WriteNupkgToFeed(feed, "Sig.Pkg", "1.0.0");

            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="{packages.FullName}" />
                    <add key="signatureValidationMode" value="require" />
                  </config>
                  <packageSources>
                    <clear />
                    <add key="local" value="{feed.FullName}" />
                  </packageSources>
                  <disabledPackageSources>
                    <clear />
                  </disabledPackageSources>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => service.InstallPackageAsync("Sig.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken));

            // The whole point: this failure must never arrive blank.
            Assert.IsNotEmpty(ex.Message, "a signature rejection must explain itself");
            StringAssert.Contains(ex.Message, "Sig.Pkg", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "unsigned", StringComparison.OrdinalIgnoreCase);
            // Names the setting responsible, so the user knows where to look.
            StringAssert.Contains(ex.Message, "signatureValidationMode", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }
}
