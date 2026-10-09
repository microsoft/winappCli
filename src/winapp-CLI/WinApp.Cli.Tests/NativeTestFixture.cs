// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

internal static class NativeTestFixture
{
    internal static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("WINAPP_NATIVE_TEST_FIXTURE");
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(@"..\..\..\..\..\winapp-devtools\test\bin\native-runtime-tests.exe", AppContext.BaseDirectory)
            : Path.GetFullPath(configured);
        Assert.IsTrue(File.Exists(path),
            $"Native dispatcher fixture missing: {path}. Build src\\winapp-devtools\\build-devtools.ps1 -Arch x64 " +
            "and set WINAPP_NATIVE_TEST_FIXTURE when using a separate test output directory.");
        return path;
    }
}
