// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

/// <summary>
/// Global test initialization and cleanup for the WinApp.Cli test suite
/// </summary>
[TestClass]
public static class GlobalTestSetup
{
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
}
