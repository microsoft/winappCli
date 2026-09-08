// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Global test initialization and cleanup for the WinApp.Cli test suite
/// </summary>
[TestClass]
public static class GlobalTestSetup
{
    /// <summary>
    /// The production Authenticode gates, captured before <see cref="AssemblyInitialize"/> opens them
    /// for the suite. Tests asserting on the real verifier must use this: a value the test assigns
    /// itself would still pass if production were rewired to an always-true stub.
    /// </summary>
    internal static Func<string, ILogger, bool> ProductionSignatureVerifier { get; private set; } = null!;

    /// <summary>
    /// The production gate for <see cref="CppWinrtService"/>, captured for the same reason.
    /// </summary>
    internal static Func<string, ILogger, bool> ProductionCppWinrtSignatureVerifier { get; private set; } = null!;

    /// <summary>
    /// Global test initialization - runs once before all tests
    /// </summary>
    /// <param name="context">Test context</param>
    [AssemblyInitialize]
    public static void AssemblyInitialize(TestContext context)
    {
        // Set up any global test resources here
        Console.WriteLine("Initializing WinApp.Cli test suite...");

        // Ensure we have a predictable environment for testing
        Environment.SetEnvironmentVariable("WINAPP_TEST_MODE", "true");

        // Suppress emoji output during tests for consistent output
        Environment.SetEnvironmentVariable("TERM_PROGRAM", "");
        Environment.SetEnvironmentVariable("VSCODE_PID", "");
        Environment.SetEnvironmentVariable("WT_SESSION", "");

        // Fixtures stand in dummy unsigned files for the real SDK binaries, so the Authenticode
        // gate on build tools is opened once here. Setting it per test would race: the seam is
        // process-wide and tests run in parallel at method level.
        // BuildToolsSignatureVerificationTests drives the gate directly and is [DoNotParallelize].
        ProductionSignatureVerifier = BuildToolsService.SignatureVerifier;
        BuildToolsService.SignatureVerifier = static (_, _) => true;

        // Same for cppwinrt.exe, which fixtures also stand in as a dummy unsigned script.
        // CppWinrtSignatureVerificationTests drives that gate directly and is [DoNotParallelize].
        ProductionCppWinrtSignatureVerifier = CppWinrtService.SignatureVerifier;
        CppWinrtService.SignatureVerifier = static (_, _) => true;
    }

    /// <summary>
    /// Global test cleanup - runs once after all tests
    /// </summary>
    [AssemblyCleanup]
    public static void AssemblyCleanup()
    {
        Console.WriteLine("Cleaning up WinApp.Cli test suite...");

        // Clean up any global test resources here
        Environment.SetEnvironmentVariable("WINAPP_TEST_MODE", null);
        Environment.SetEnvironmentVariable("TERM_PROGRAM", null);
        Environment.SetEnvironmentVariable("VSCODE_PID", null);
        Environment.SetEnvironmentVariable("WT_SESSION", null);

        // Clean up any temporary files that might have been left behind
        CleanupTempDirectories();
        CleanupCoverageScratch();
    }

    /// <summary>
    /// Cleans up any temporary test directories that might have been left behind
    /// </summary>
    private static void CleanupTempDirectories()
    {
        try
        {
            var tempPath = Path.GetTempPath();
            var testDirectories = Directory.GetDirectories(tempPath, "WinAppSignTest_*");

            foreach (var dir in testDirectories)
            {
                try
                {
                    // Check if directory is older than 1 hour to avoid interfering with running tests
                    var dirInfo = new DirectoryInfo(dir);
                    if (DateTime.Now - dirInfo.CreationTime > TimeSpan.FromHours(1))
                    {
                        Directory.Delete(dir, true);
                    }
                }
                catch
                {
                    // Ignore individual directory cleanup failures
                }
            }
        }
        catch
        {
            // Ignore global cleanup failures
        }
    }

    private static void CleanupCoverageScratch()
    {
        var scratchPath = Path.Join(AppContext.BaseDirectory, "coverage-scratch");
        try
        {
            if (Directory.Exists(scratchPath))
            {
                Directory.Delete(scratchPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Could not clean coverage scratch: {ex.Message}");
        }
    }
}
