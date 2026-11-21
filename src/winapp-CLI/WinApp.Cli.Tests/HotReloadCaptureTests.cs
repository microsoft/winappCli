// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.HotReload;

namespace WinApp.Cli.Tests;

[TestClass]
public class HotReloadCaptureTests
{
    [TestMethod]
    [DataRow(false, false, "could not find its hot reload helper", DisplayName = "No helper next to winapp")]
    [DataRow(true, true, "has its own .NET framework files", DisplayName = "Loose framework files next to the helper (row 60)")]
    [DataRow(true, false, null, DisplayName = "The published layout")]
    public void HelperProblem_SaysWhyTheHelperCantStart(bool helper, bool looseFramework, string? expected)
    {
        var folder = Directory.CreateTempSubdirectory("winapp-cli-");
        try
        {
            if (helper) { File.WriteAllText(HotReloadCapture.HelperPath(folder.FullName), ""); }
            if (looseFramework) { File.WriteAllText(Path.Combine(folder.FullName, "System.Runtime.dll"), ""); }

            var problem = HotReloadCapture.HelperProblem(folder.FullName);

            if (expected is null) { Assert.IsNull(problem); }
            else { Assert.Contains(expected, problem!); }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
