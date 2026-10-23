// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Coverage for the <see cref="DetectedProject"/> record's display helpers, including
/// the human-readable label for every <see cref="DetectedProjectType"/> and the
/// defensive default branch.
/// </summary>
[TestClass]
public class DetectedProjectTests
{
    private static DetectedProject Make(DetectedProjectType type, string displayPath, string projectFile)
        => new(type, new DirectoryInfo(Path.GetTempPath()), displayPath, projectFile);

    [TestMethod]
    [DataRow((int)DetectedProjectType.Tauri, "Tauri")]
    [DataRow((int)DetectedProjectType.Electron, "Electron")]
    [DataRow((int)DetectedProjectType.Flutter, "Flutter")]
    [DataRow((int)DetectedProjectType.Dotnet, ".NET")]
    [DataRow((int)DetectedProjectType.Rust, "Rust")]
    [DataRow((int)DetectedProjectType.CPP, "C++")]
    public void TypeLabel_KnownTypes_MapToFriendlyNames(int typeValue, string expected)
    {
        Assert.AreEqual(expected, Make((DetectedProjectType)typeValue, ".", "proj").TypeLabel);
    }

    [TestMethod]
    public void TypeLabel_UnknownType_FallsBackToEnumName()
    {
        // Defensive default arm: an out-of-range enum value falls back to ToString().
        var project = Make((DetectedProjectType)999, ".", "proj");
        Assert.AreEqual("999", project.TypeLabel);
    }

    [TestMethod]
    public void DisplayFilePath_RootProject_UsesDotPrefixWithoutDirectory()
    {
        var project = Make(DetectedProjectType.Dotnet, ".", "MyApp.csproj");
        Assert.AreEqual("./MyApp.csproj", project.DisplayFilePath);
    }

    [TestMethod]
    public void DisplayFilePath_NestedProject_IncludesRelativeDirectory()
    {
        var project = Make(DetectedProjectType.Dotnet, "src/MyApp", "MyApp.csproj");
        Assert.AreEqual("./src/MyApp/MyApp.csproj", project.DisplayFilePath);
    }

    [TestMethod]
    public void ToDisplayString_CombinesLabelAndPath()
    {
        var project = Make(DetectedProjectType.CPP, "app", "CMakeLists.txt");
        Assert.AreEqual("C++ project (./app/CMakeLists.txt)", project.ToDisplayString());
    }
}
