// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NativeTestFixtureTests
{
    [TestMethod]
    public void ExplicitMissingFixtureNeverFallsBackToLocallyBuiltFixture()
    {
        var prior = Environment.GetEnvironmentVariable("WINAPP_NATIVE_TEST_FIXTURE");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "native-runtime-tests.exe");
        try
        {
            Environment.SetEnvironmentVariable("WINAPP_NATIVE_TEST_FIXTURE", path);
            var error = Assert.ThrowsExactly<AssertFailedException>(() => NativeTestFixture.Resolve());
            StringAssert.Contains(error.Message, path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINAPP_NATIVE_TEST_FIXTURE", prior);
        }
    }

    [TestMethod]
    public void ExplicitFixturePathSupportsIsolatedValidationOutput()
    {
        var prior = Environment.GetEnvironmentVariable("WINAPP_NATIVE_TEST_FIXTURE");
        var path = Path.GetTempFileName();
        try
        {
            Environment.SetEnvironmentVariable("WINAPP_NATIVE_TEST_FIXTURE", path);
            Assert.AreEqual(path, NativeTestFixture.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINAPP_NATIVE_TEST_FIXTURE", prior);
            File.Delete(path);
        }
    }
}
