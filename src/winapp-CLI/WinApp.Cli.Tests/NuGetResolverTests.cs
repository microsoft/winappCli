// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers the metadata-selection rules that decide *which* .winmd files answer a
/// query. These are correctness rules rather than formatting: picking the wrong
/// SDK or runtime produces a confident answer about an API the project cannot
/// actually compile against.
/// </summary>
[TestClass]
public sealed class NuGetResolverTests
{
    private static readonly string[] SelectedWinmdOnly = ["Contoso.winmd"];
    private static readonly string[] ScannedRuntimeWinmd = ["Contoso.Runtime.winmd"];
    private static readonly string[] AlphaAndBetaOutputs = ["Alpha.dll", "Beta.dll"];
    private static readonly string[] MiddleAndLeafOutputs = ["Middle.dll", "Leaf.dll"];
    private static readonly string[] SystemContosoAndTransitiveIds = ["System.Contoso", "System.Transitive"];

    private string _dir = null!;

    #region Compile-surface resolution

    [TestMethod]
    public void ReadTargetPlatformVersionFromProjectFile_CppProject_ReadsWindowsTargetPlatformVersion()
    {
        // A C++ project has no project.assets.json, so this is the only place its target
        // is written down. Reading it as "no target declared" falls back to the newest
        // installed Windows Kit, which confirms APIs the project cannot compile against.
        string projectFile = Path.Combine(_dir, "App.vcxproj");
        File.WriteAllText(projectFile, """
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup Label="Globals">
                <WindowsTargetPlatformVersion>10.0.19041.0</WindowsTargetPlatformVersion>
              </PropertyGroup>
            </Project>
            """);

        Assert.AreEqual("10.0.19041.0", NuGetResolver.ReadTargetPlatformVersionFromProjectFile(projectFile));
    }

    [TestMethod]
    public void ReadTargetPlatformVersionFromProjectFile_MultiTargetedNetProject_TakesTheHighest()
    {
        // Matches the target whose compile assets are read, so SDK metadata and package
        // assets describe the same framework.
        string projectFile = Path.Combine(_dir, "App.csproj");
        File.WriteAllText(projectFile, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0-windows10.0.19041.0;net8.0-windows10.0.26100.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        Assert.AreEqual("10.0.26100.0", NuGetResolver.ReadTargetPlatformVersionFromProjectFile(projectFile));
    }

    [TestMethod]
    public void ReadTargetPlatformVersionFromProjectFile_NoWindowsTarget_ReturnsNull()
    {
        string projectFile = Path.Combine(_dir, "Plain.csproj");
        File.WriteAllText(projectFile, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        Assert.IsNull(NuGetResolver.ReadTargetPlatformVersionFromProjectFile(projectFile));
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_ManagedLibrary_IndexesItsDll()
    {
        // A referenced C# class library builds to a .dll. A winmd-only scan indexes
        // nothing for it, so every query about a type in the caller's own solution
        // answers "does not exist".
        string libDir = Path.Combine(_dir, "ContosoLib");
        string libBin = Path.Combine(libDir, "bin", "Debug", "net8.0");
        Directory.CreateDirectory(libBin);
        File.WriteAllText(Path.Combine(libDir, "ContosoLib.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(libBin, "ContosoLib.dll"), "x");
        // A dependency copied next to it must not be indexed: the project does not
        // reference it directly, and its types are not on the compile surface.
        File.WriteAllText(Path.Combine(libBin, "Newtonsoft.Json.dll"), "x");

        string appDir = Path.Combine(_dir, "App");
        Directory.CreateDirectory(appDir);
        string appProject = Path.Combine(appDir, "App.csproj");
        File.WriteAllText(appProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\ContosoLib\ContosoLib.csproj" />
              </ItemGroup>
            </Project>
            """);

        List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

        Assert.AreEqual(1, packages.Count, "the referenced library must be indexed");
        CollectionAssert.AreEquivalent(
            ReferencedLibraryOutputOnly,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_CustomAssemblyName_IndexesTheBuiltOutput()
    {
        // MSBuild names the output from <AssemblyName>, not the project file. Matching on
        // the project's own name finds nothing, so `find-api members Company.Controls.Widget`
        // answers "Type not found" for a type the app compiles against today.
        string libDir = Path.Combine(_dir, "RenamedLib");
        string libBin = Path.Combine(libDir, "bin", "Debug", "net8.0");
        Directory.CreateDirectory(libBin);
        File.WriteAllText(Path.Combine(libDir, "RenamedLib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <AssemblyName>Company.Controls</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(libBin, "Company.Controls.dll"), "x");

        string appDir = Path.Combine(_dir, "RenamedApp");
        Directory.CreateDirectory(appDir);
        string appProject = Path.Combine(appDir, "App.csproj");
        File.WriteAllText(appProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\RenamedLib\RenamedLib.csproj" />
              </ItemGroup>
            </Project>
            """);

        List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

        Assert.AreEqual(1, packages.Count, "the renamed output is still the referenced library");
        CollectionAssert.AreEquivalent(
            RenamedLibraryOutputOnly,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_SameOutputInSeveralConfigurations_TakesTheNewest()
    {
        // bin\ holds one subtree per configuration, all with the same file name. Taking
        // whichever the directory walk yields first indexes a stale Debug build after the
        // developer switches to Release, and a type added since then reads as missing.
        string libDir = Path.Combine(_dir, "MultiCfgLib");
        string debugBin = Path.Combine(libDir, "bin", "Debug", "net8.0");
        string releaseBin = Path.Combine(libDir, "bin", "Release", "net8.0");
        Directory.CreateDirectory(debugBin);
        Directory.CreateDirectory(releaseBin);
        File.WriteAllText(Path.Combine(libDir, "MultiCfgLib.csproj"), "<Project />");

        string stale = Path.Combine(debugBin, "MultiCfgLib.dll");
        string fresh = Path.Combine(releaseBin, "MultiCfgLib.dll");
        File.WriteAllText(stale, "old");
        File.WriteAllText(fresh, "new");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);

        string appDir = Path.Combine(_dir, "MultiCfgApp");
        Directory.CreateDirectory(appDir);
        string appProject = Path.Combine(appDir, "App.csproj");
        File.WriteAllText(appProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\MultiCfgLib\MultiCfgLib.csproj" />
              </ItemGroup>
            </Project>
            """);

        List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

        Assert.AreEqual(1, packages.Count);
        Assert.AreEqual(1, packages[0].WinMdFiles.Count, "one output per assembly name, not one per configuration");
        Assert.AreEqual(fresh, packages[0].WinMdFiles[0], "the most recently built output wins");
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_AnalyzerReference_IsNotIndexed()
    {
        // A source generator is referenced with OutputItemType="Analyzer", which tells
        // MSBuild to load it into the compiler rather than reference it. Its .dll still
        // lands in bin like any other, so indexing it makes `find-api` report the
        // generator's own types as callable API and an agent writes code that cannot
        // compile.
        WriteReferencedLibrary("GenLib", "GenLib.dll");
        string appProject = WriteApp("""
                <ProjectReference Include="..\GenLib\GenLib.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
            """);

        List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

        Assert.AreEqual(0, packages.Count, "a build-only reference is not on the compile surface");
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_ReferenceOutputAssemblyAsChildElement_IsNotIndexed()
    {
        // MSBuild item metadata is equally valid as a child element, and a project that
        // spells it that way must get the same answer as the attribute form.
        WriteReferencedLibrary("ToolLib", "ToolLib.dll");
        string appProject = WriteApp("""
                <ProjectReference Include="..\ToolLib\ToolLib.csproj">
                  <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
                </ProjectReference>
            """);

        List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

        Assert.AreEqual(0, packages.Count, "metadata spelled as a child element means the same thing");
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_JunctionInsideBin_IsNotFollowed()
    {
        // The scan refuses to *start* at a junctioned bin, but a junction found part-way
        // down is followed all the same. A repo that commits one at bin\Debug\shared
        // aimed at \\attacker\share turns `winapp find-api refresh` into an outbound
        // authenticated SMB connection, with no user action beyond cloning.
        string libDir = Path.Combine(_dir, "LinkLib");
        string libBin = Path.Combine(libDir, "bin", "Debug", "net8.0");
        Directory.CreateDirectory(libBin);
        File.WriteAllText(Path.Combine(libDir, "LinkLib.csproj"), "<Project />");

        string outside = Path.Combine(_dir, "Elsewhere", "planted");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "LinkLib.dll"), "x");

        string link = Path.Combine(libBin, "shared");
        if (!TryCreateJunction(link, outside))
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        try
        {
            string appProject = WriteApp("""
                    <ProjectReference Include="..\LinkLib\LinkLib.csproj" />
                """);

            List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

            Assert.AreEqual(0, packages.Count, "the walk must not descend through the junction");
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>Writes a referenced library with a single built output in its bin tree.</summary>
    private void WriteReferencedLibrary(string name, string outputFileName)
    {
        string libDir = Path.Combine(_dir, name);
        string libBin = Path.Combine(libDir, "bin", "Debug", "net8.0");
        Directory.CreateDirectory(libBin);
        File.WriteAllText(Path.Combine(libDir, name + ".csproj"), "<Project />");
        File.WriteAllText(Path.Combine(libBin, outputFileName), "x");
    }

    /// <summary>Writes an App.csproj whose ItemGroup holds <paramref name="itemGroupBody"/>.</summary>
    private string WriteApp(string itemGroupBody)
    {
        string appDir = Path.Combine(_dir, "App");
        Directory.CreateDirectory(appDir);
        string appProject = Path.Combine(appDir, "App.csproj");
        File.WriteAllText(appProject, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
            {itemGroupBody}
              </ItemGroup>
            </Project>
            """);
        return appProject;
    }

    /// <summary>Writes an App.csproj that targets <paramref name="targetFramework"/>.</summary>
    private string WriteApp(string itemGroupBody, string targetFramework)
    {
        string appDir = Path.Combine(_dir, "App");
        Directory.CreateDirectory(appDir);
        string appProject = Path.Combine(appDir, "App.csproj");
        File.WriteAllText(appProject, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{targetFramework}</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
            {itemGroupBody}
              </ItemGroup>
            </Project>
            """);
        return appProject;
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_SemicolonSeparatedInclude_IndexesEveryProjectNamed()
    {
        // MSBuild treats an Include as an item list. Reading the whole value as one path
        // finds no file, so both libraries go unindexed and every type in them answers
        // "does not exist" for code sitting in the caller's own solution.
        WriteReferencedLibrary("Alpha", "Alpha.dll");
        WriteReferencedLibrary("Beta", "Beta.dll");
        string appProject = WriteApp("""
                <ProjectReference Include="..\Alpha\Alpha.csproj;..\Beta\Beta.csproj" />
            """);

        List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

        CollectionAssert.AreEquivalent(
            AlphaAndBetaOutputs,
            packages.SelectMany(p => p.WinMdFiles).Select(Path.GetFileName).ToList(),
            "both projects named by one Include are on the compile surface");
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_MultiTargetedLibrary_PicksTheReferencingProjectsFramework()
    {
        // A multi-targeted library builds every TFM in one command, so the newest write
        // time picks an arbitrary one. A net8.0 app handed the net8.0-windows build is
        // told that Windows-only types on it exist, and the code it writes will not
        // compile.
        string libDir = Path.Combine(_dir, "Multi");
        Directory.CreateDirectory(libDir);
        File.WriteAllText(Path.Combine(libDir, "Multi.csproj"), "<Project />");

        string neutral = Path.Combine(libDir, "bin", "Debug", "net8.0");
        string windows = Path.Combine(libDir, "bin", "Debug", "net8.0-windows10.0.19041.0");
        Directory.CreateDirectory(neutral);
        Directory.CreateDirectory(windows);
        File.WriteAllText(Path.Combine(neutral, "Multi.dll"), "neutral");
        File.WriteAllText(Path.Combine(windows, "Multi.dll"), "windows");
        // The wrong one is the newest, which is what the timestamp rule would pick.
        File.SetLastWriteTimeUtc(Path.Combine(neutral, "Multi.dll"), DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(Path.Combine(windows, "Multi.dll"), DateTime.UtcNow);

        string appProject = WriteApp("""
                <ProjectReference Include="..\Multi\Multi.csproj" />
            """, "net8.0");

        List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

        string selected = packages.SelectMany(p => p.WinMdFiles).Single();
        Assert.AreEqual(
            "neutral",
            File.ReadAllText(selected),
            "a net8.0 project cannot compile against a net8.0-windows build");
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_TransitivelyReferencedProject_IsIndexed()
    {
        // App -> Middle -> Leaf. C# exposes Leaf's public types to App, so answering
        // "does not exist" for them is wrong about the caller's own solution. The project
        // file names only Middle; restore output is what records the full closure.
        WriteReferencedLibrary("Middle", "Middle.dll");
        WriteReferencedLibrary("Leaf", "Leaf.dll");
        string appProject = WriteApp("""
                <ProjectReference Include="..\Middle\Middle.csproj" />
            """);
        WriteAssetsWithProjectLibraries(appProject, "../Middle/Middle.csproj", "../Leaf/Leaf.csproj");

        List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

        CollectionAssert.AreEquivalent(
            MiddleAndLeafOutputs,
            packages.SelectMany(p => p.WinMdFiles).Select(Path.GetFileName).ToList(),
            "a transitively referenced project is on the compile surface");
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_TransitiveClosureNamingABuildOnlyReference_StaysExcluded()
    {
        // Restore records an analyzer project like any other, so reading the closure must
        // not undo the build-only filter and start confirming types the app cannot call.
        WriteReferencedLibrary("GenOnly", "GenOnly.dll");
        string appProject = WriteApp("""
                <ProjectReference Include="..\GenOnly\GenOnly.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
            """);
        WriteAssetsWithProjectLibraries(appProject, "../GenOnly/GenOnly.csproj");

        List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

        Assert.AreEqual(0, packages.Count, "restore output must not reinstate a build-only reference");
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_OnlyIncompatibleFrameworkOutput_IsNotIndexed()
    {
        // The library built only a net8.0-windows output; a net8.0 app cannot compile
        // against it. With no compatible output to fall back on, selecting the
        // "least incompatible" one would still report Windows-only types as callable.
        string libDir = Path.Combine(_dir, "WinOnly");
        string windows = Path.Combine(libDir, "bin", "Debug", "net8.0-windows10.0.19041.0");
        Directory.CreateDirectory(windows);
        File.WriteAllText(Path.Combine(libDir, "WinOnly.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(windows, "WinOnly.dll"), "windows");

        string appProject = WriteApp("""
                <ProjectReference Include="..\WinOnly\WinOnly.csproj" />
            """, "net8.0");

        List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

        Assert.AreEqual(0, packages.Count, "a net8.0 app cannot compile against a net8.0-windows-only library");
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_ConditionalReference_ExcludedWhenRestoreOutputPresent()
    {
        // A ProjectReference gated by a Condition is active only for some configurations,
        // which cannot be judged without the MSBuild engine. With restore output present
        // its reference set is authoritative, so the conditional raw reference must not be
        // indexed for a configuration it may not belong to.
        WriteReferencedLibrary("WinLib", "WinLib.dll");
        string appProject = WriteApp("""
                <ProjectReference Include="..\WinLib\WinLib.csproj" Condition="'$(TargetFramework)' == 'net8.0-windows'" />
            """, "net8.0");
        // Restore for the net8.0 configuration recorded no project references.
        WriteAssetsWithProjectLibraries(appProject);

        List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

        Assert.AreEqual(0, packages.Count, "a conditional reference absent from restore output is not on the compile surface");
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_ConditionalReference_KeptWhenNoRestoreOutput()
    {
        // Without restore output there is nothing authoritative to defer to, so a
        // conditional reference is kept on a best-effort basis rather than dropped —
        // otherwise a project with no restore would silently lose references.
        WriteReferencedLibrary("WinLib", "WinLib.dll");
        string appProject = WriteApp("""
                <ProjectReference Include="..\WinLib\WinLib.csproj" Condition="'$(Configuration)' == 'Debug'" />
            """);

        List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

        Assert.AreEqual(1, packages.Count, "with no restore output the raw reference is the only signal available");
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_ConditionalBuildOnlyReference_StaysExcludedViaRestoreClosure()
    {
        // A reference that is BOTH conditional and build-only (an analyzer). Deferring the
        // conditional reference to restore output must still record its build-only status, or
        // the transitive restore closure reinstates it and reports the generator's own types
        // as callable API.
        WriteReferencedLibrary("GenCond", "GenCond.dll");
        string appProject = WriteApp("""
                <ProjectReference Include="..\GenCond\GenCond.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" Condition="'$(TargetFramework)' == 'net8.0'" />
            """, "net8.0");
        WriteAssetsWithProjectLibraries(appProject, "../GenCond/GenCond.csproj");

        List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

        Assert.AreEqual(0, packages.Count, "a conditional build-only reference must not be reinstated by the restore closure");
    }

    [TestMethod]
    public void FindWinMdFromProjectReferences_ConditionInChooseWhen_IsHonoredWhenRestoreOutputPresent()
    {
        // The gating Condition sits on an enclosing <When>, not the reference or its
        // <ItemGroup>. It must still be recognized so the inactive reference is deferred to
        // restore output rather than indexed for a configuration it does not belong to.
        WriteReferencedLibrary("WhenLib", "WhenLib.dll");
        string appDir = Path.Combine(_dir, "App");
        Directory.CreateDirectory(appDir);
        string appProject = Path.Combine(appDir, "App.csproj");
        File.WriteAllText(appProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <Choose>
                <When Condition="'$(TargetFramework)' == 'net8.0-windows'">
                  <ItemGroup>
                    <ProjectReference Include="..\WhenLib\WhenLib.csproj" />
                  </ItemGroup>
                </When>
              </Choose>
            </Project>
            """);
        WriteAssetsWithProjectLibraries(appProject);

        List<PackageWithWinMd> packages = NuGetResolver.FindWinMdFromProjectReferences(appProject);

        Assert.AreEqual(0, packages.Count, "a reference gated by Choose/When is deferred to restore output");
    }

    [TestMethod]
    public void FindProjectAssetsJson_LoneUnownedAssetsFile_IsNotReturned()
    {
        // A single obj\project.assets.json can belong to a colocated sibling. Returning it
        // for this project answers from the sibling's package graph, so the ownership
        // filter must apply even when only one assets file exists.
        string projectDir = Path.Combine(_dir, "Solution");
        string mine = Path.Combine(projectDir, "App.csproj");
        string sibling = Path.Combine(projectDir, "Other.csproj");
        Directory.CreateDirectory(Path.Combine(projectDir, "obj"));
        File.WriteAllText(mine, "<Project />");
        File.WriteAllText(sibling, "<Project />");

        string assets = Path.Combine(projectDir, "obj", "project.assets.json");
        File.WriteAllText(assets, $$"""
            { "libraries": {}, "project": { "restore": { "projectPath": "{{sibling.Replace("\\", "\\\\")}}" } } }
            """);

        Assert.IsNull(
            NuGetResolver.FindProjectAssetsJson(projectDir, mine),
            "a lone assets file owned by a sibling is not this project's restore output");
        Assert.AreEqual(
            assets,
            NuGetResolver.FindProjectAssetsJson(projectDir, sibling),
            "the sibling still resolves its own assets file");
    }

    /// <summary>
    /// Writes a project.assets.json next to <paramref name="appProject"/> listing the given
    /// project-relative paths as <c>"type": "project"</c> libraries, the way restore records
    /// a project's full project-reference closure.
    /// </summary>
    private static void WriteAssetsWithProjectLibraries(string appProject, params string[] relativePaths)
    {
        string objDir = Path.Combine(Path.GetDirectoryName(appProject)!, "obj");
        Directory.CreateDirectory(objDir);
        string libraries = string.Join(",\n", relativePaths.Select((path, index) => $$"""
                "Lib{{index}}/1.0.0": { "type": "project", "path": "{{path}}", "msbuildProject": "{{path}}" }
            """));
        File.WriteAllText(Path.Combine(objDir, "project.assets.json"), $$"""
            {
              "version": 3,
              "libraries": {
            {{libraries}}
              },
              "project": { "restore": { "projectPath": "{{appProject.Replace("\\", "\\\\")}}" } }
            }
            """);
    }

    [TestMethod]
    public void FindProjectAssetsJson_SeveralUnderOneObjTree_PicksTheOneRestoredForThisProject()
    {
        // Colocated projects, or a nested BaseIntermediateOutputPath, put more than one
        // assets file under a single obj tree. Picking by write time makes the whole
        // index depend on which project was built last, so a query about this project
        // answers from its neighbour's package set.
        string projectDir = Path.Combine(_dir, "Solution");
        string mine = Path.Combine(projectDir, "App.csproj");
        string theirs = Path.Combine(projectDir, "Other.csproj");
        Directory.CreateDirectory(projectDir);

        string myAssets = WriteNestedAssets(projectDir, "app-intermediate", mine);
        string theirAssets = WriteNestedAssets(projectDir, "other-intermediate", theirs);
        // The neighbour was restored more recently, so write time alone would pick it.
        File.SetLastWriteTimeUtc(myAssets, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(theirAssets, DateTime.UtcNow);

        Assert.AreEqual(myAssets, NuGetResolver.FindProjectAssetsJson(projectDir, mine));
        Assert.AreEqual(theirAssets, NuGetResolver.FindProjectAssetsJson(projectDir, theirs));
    }

    [TestMethod]
    public void FindProjectAssetsJson_ObjIsAJunctionOutOfTheProject_IsNotFollowed()
    {
        // `find-api` indexes whatever a cloned repo points it at, without the user opening
        // a file. A checked-in `obj` junction (or symlink) aimed at \\attacker\share turns
        // `winapp find-api refresh` into an outbound authenticated SMB connection that
        // leaks the caller's NTLM credentials — the reason every repo-controlled probe in
        // this resolver is gated before it touches the disk.
        string projectDir = Path.Combine(_dir, "Junctioned");
        string outside = Path.Combine(_dir, "Elsewhere", "obj");
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(outside);

        string projectFile = Path.Combine(projectDir, "App.csproj");
        File.WriteAllText(projectFile, "<Project />");
        File.WriteAllText(Path.Combine(outside, "project.assets.json"), "{}");

        string link = Path.Combine(projectDir, "obj");
        if (!TryCreateJunction(link, outside))
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        try
        {
            Assert.IsNull(
                NuGetResolver.FindProjectAssetsJson(projectDir, projectFile),
                "a redirected obj directory must not be probed or read");
        }
        finally
        {
            // Removing the reparse point itself; recursive deletion of the fixture would
            // otherwise fail on it.
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public void FindProjectAssetsJson_AssetsFileItselfIsALink_IsNotFollowed()
    {
        // Guarding `obj` leaves the last segment unguarded: the directory is ordinary and
        // only `project.assets.json` inside it is a link. Reading it is the same outbound
        // reach a redirected `obj` would be, chosen the same way — by cloning a repo.
        string projectDir = Path.Combine(_dir, "LinkedAssetsFile");
        string outsideDir = Path.Combine(_dir, "ElsewhereAssetsFile");
        Directory.CreateDirectory(Path.Combine(projectDir, "obj"));
        Directory.CreateDirectory(outsideDir);

        string outsideAssets = Path.Combine(outsideDir, "project.assets.json");
        File.WriteAllText(outsideAssets, "{}");

        string link = Path.Combine(projectDir, "obj", "project.assets.json");
        try
        {
            File.CreateSymbolicLink(link, outsideAssets);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Could not create a file symbolic link on this machine.");
        }

        // No project file, so the ownership check cannot be what rejects it.
        Assert.IsNull(
            NuGetResolver.FindProjectAssetsJson(projectDir),
            "a redirected assets file must not be read");
    }

    [TestMethod]
    public void ReadAssemblyName_PlainName_IsUsed()
    {
        Assert.AreEqual("MyLib", NuGetResolver.ReadAssemblyName(WriteProject("MyLib")));
    }

    [TestMethod]
    public void ReadAssemblyName_UnexpandedProperty_IsIgnored()
    {
        Assert.IsNull(NuGetResolver.ReadAssemblyName(WriteProject("$(MSBuildProjectName).Core")));
    }

    [TestMethod]
    [DataRow(@"..\redirect\Poison", DisplayName = "climbs out of the output directory")]
    [DataRow(@"sub\Poison", DisplayName = "names a subdirectory")]
    [DataRow(@"C:\absolute\Poison", DisplayName = "is rooted")]
    [DataRow("*", DisplayName = "wildcard matches every dependency")]
    [DataRow("Poison?", DisplayName = "single-character wildcard")]
    [DataRow("..", DisplayName = "the parent directory itself")]
    public void ReadAssemblyName_ValueIsNotAPlainFileName_IsIgnored(string assemblyName)
    {
        // The value becomes a search pattern, and a search pattern is not confined to the
        // directory it is rooted at: Directory.GetFiles(bin, @"..\redirect\Poison.dll")
        // returns that file. Falling back to the project file name keeps the scan inside
        // the referenced project's own output.
        Assert.IsNull(NuGetResolver.ReadAssemblyName(WriteProject(assemblyName)));
    }

    private string WriteProject(string assemblyName)
    {
        string dir = Path.Combine(_dir, "AsmName", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string projectFile = Path.Combine(dir, "Lib.csproj");
        File.WriteAllText(
            projectFile,
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>{assemblyName}</AssemblyName></PropertyGroup></Project>");
        return projectFile;
    }

    /// <summary>Creates a directory junction (<c>mklink /J</c>), which needs no elevation.</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return false;
            }
            p.WaitForExit(5000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [TestMethod]
    public void FindPackagesFromAssets_SeveralWindowsTargets_SaysWhichOneAnsweredAndThatOthersMayDiffer()
    {
        // The answer is only true of the target chosen. Multi-targeting is legitimate, so
        // this reports the ambiguity rather than refusing to index the project.
        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object>(),
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.19041.0"] = new Dictionary<string, object>(),
                ["net8.0-windows10.0.26100.0"] = new Dictionary<string, object>(),
            },
            libraries = new Dictionary<string, object>(),
        }));
        var warnings = new List<string>();

        NuGetResolver.FindPackagesFromAssets(path, warnings.Add);

        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "net8.0-windows10.0.26100.0");
        StringAssert.Contains(warnings[0], "may not exist for the others");
    }

    [TestMethod]
    public void FindPackagesFromAssets_SingleWindowsTarget_DoesNotWarn()
    {
        // The warning must mark real ambiguity, not fire on every ordinary project.
        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object>(),
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.26100.0"] = new Dictionary<string, object>(),
            },
            libraries = new Dictionary<string, object>(),
        }));
        var warnings = new List<string>();

        NuGetResolver.FindPackagesFromAssets(path, warnings.Add);

        Assert.AreEqual(0, warnings.Count);
    }

    private static readonly string[] ReferencedLibraryOutputOnly = ["ContosoLib.dll"];
    private static readonly string[] RenamedLibraryOutputOnly = ["Company.Controls.dll"];

    /// <summary>
    /// Writes an assets file under <c>obj/&lt;intermediate&gt;/</c> — never directly at
    /// <c>obj/project.assets.json</c> — so the recursive fallback is the path under test.
    /// </summary>
    private static string WriteNestedAssets(string projectDir, string intermediate, string projectPath)
    {
        string dir = Path.Combine(projectDir, "obj", intermediate);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "project.assets.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            libraries = new Dictionary<string, object>(),
            project = new { restore = new { projectPath } },
        }));
        return path;
    }

    #endregion

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"NuGetResolverTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [TestMethod]
    public void FindPackagesFromAssets_MalformedAssetsFile_WarnsInsteadOfSilentlyIndexingNothing()
    {
        // The caller only reaches this method when project.assets.json exists, so a parse
        // failure means restore output is present but unreadable. Staying silent leaves the
        // caller with a thin index and no way to tell an unavailable API from an unread one.
        string path = WriteAssets(@"{ ""libraries"": { ""Contoso/1.0.0"": { ""type"": ""pack");
        var warnings = new List<string>();

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path, warnings.Add);

        Assert.AreEqual(0, packages.Count);
        Assert.AreEqual(1, warnings.Count, "a truncated assets file must be reported");
        StringAssert.Contains(warnings[0], "project.assets.json");
        StringAssert.Contains(warnings[0], "dotnet restore");
    }

    [TestMethod]
    public void FindPackagesFromAssets_WellFormedAssetsFile_DoesNotWarn()
    {
        // The warning must mark a real failure, not fire on every project.
        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object>(),
            libraries = new Dictionary<string, object>(),
        }));
        var warnings = new List<string>();

        NuGetResolver.FindPackagesFromAssets(path, warnings.Add);

        Assert.AreEqual(0, warnings.Count);
    }
    private string WriteAssets(string json)
    {
        string path = Path.Combine(_dir, "project.assets.json");
        File.WriteAllText(path, json);
        return path;
    }

    [TestMethod]
    public void ReadTargetPlatformVersion_ReadsWindowsVersionFromTargetMoniker()
    {
        string path = WriteAssets("""
        {
          "targets": { "net8.0-windows10.0.26100.0": {} },
          "libraries": {}
        }
        """);

        Assert.AreEqual("10.0.26100.0", NuGetResolver.ReadTargetPlatformVersion(path));
    }

    [TestMethod]
    public void ReadTargetPlatformVersion_FallsBackToProjectFrameworks()
    {
        string path = WriteAssets("""
        {
          "targets": { "net8.0": {} },
          "project": { "frameworks": { "net8.0-windows10.0.22621.0": {} } }
        }
        """);

        Assert.AreEqual("10.0.22621.0", NuGetResolver.ReadTargetPlatformVersion(path));
    }

    [TestMethod]
    public void ReadTargetPlatformVersion_ReturnsNullWhenNotWindowsTargeted()
    {
        string path = WriteAssets("""
        {
          "targets": { "net8.0": {} },
          "project": { "frameworks": { "net8.0": {} } }
        }
        """);

        Assert.IsNull(NuGetResolver.ReadTargetPlatformVersion(path));
    }

    [TestMethod]
    public void ReadTargetPlatformVersion_ReturnsNullForUnreadableAssets()
    {
        string path = WriteAssets("{ this is not json");

        Assert.IsNull(NuGetResolver.ReadTargetPlatformVersion(path));
    }

    [TestMethod]
    public void FindPackagesFromAssets_PrefersSelectedCompileAssetsOverEveryWinmdOnDisk()
    {
        // The package ships metadata for two targets; restore selected only one.
        // Indexing both lets a query confirm an API the project cannot compile against.
        string packageRoot = Path.Combine(_dir, "packages");
        string packageDir = Path.Combine(packageRoot, "contoso.metadata", "1.0.0");
        string selected = Path.Combine(packageDir, "lib", "net8.0-windows10.0.26100.0");
        string notSelected = Path.Combine(packageDir, "lib", "uap10.0");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(notSelected);
        File.WriteAllText(Path.Combine(selected, "Contoso.winmd"), "x");
        File.WriteAllText(Path.Combine(notSelected, "Legacy.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.26100.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object>
                        {
                            ["lib/net8.0-windows10.0.26100.0/Contoso.winmd"] = new { },
                        },
                    },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Metadata/1.0.0"] = new { type = "package", path = "contoso.metadata/1.0.0" },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        Assert.AreEqual(1, packages.Count);
        CollectionAssert.AreEquivalent(
            SelectedWinmdOnly,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void FindPackagesFromAssets_FallsBackToScanWhenRestoreNamedNoCompileAssets()
    {
        // Some WinRT metadata packages carry .winmd outside any compile group. Those
        // must still index, or the fix for over-broad scanning would lose real APIs.
        string packageRoot = Path.Combine(_dir, "packages");
        string packageDir = Path.Combine(packageRoot, "contoso.runtime", "2.0.0");
        string metadata = Path.Combine(packageDir, "metadata");
        Directory.CreateDirectory(metadata);
        File.WriteAllText(Path.Combine(metadata, "Contoso.Runtime.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.26100.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Runtime/2.0.0"] = new { },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Runtime/2.0.0"] = new { type = "package", path = "contoso.runtime/2.0.0" },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        Assert.AreEqual(1, packages.Count);
        CollectionAssert.AreEquivalent(
            ScannedRuntimeWinmd,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    private static readonly (string Dir, Version? Version)[] InstalledSdks =
    [
        (@"C:\Kits\10.0.28000.0", new Version("10.0.28000.0")),
        (@"C:\Kits\10.0.26100.0", new Version("10.0.26100.0")),
        (@"C:\Kits\10.0.19041.0", new Version("10.0.19041.0")),
    ];

    [TestMethod]
    public void SelectWindowsSdkDir_PrefersTheExactTargetedVersion()
    {
        string? picked = NuGetResolver.SelectWindowsSdkDir(InstalledSdks, "10.0.26100.0", _ => true);

        Assert.AreEqual(@"C:\Kits\10.0.26100.0", picked);
    }

    [TestMethod]
    public void SelectWindowsSdkDir_NeverPicksAnSdkNewerThanTheTarget()
    {
        // The targeted 10.0.22621.0 is not installed. UnionMetadata is cumulative, so
        // answering from the newer 10.0.26100.0 would confirm APIs that do not exist at
        // the target and fail the build with CS0234. Cap at the highest install below it.
        var warnings = new List<string>();
        string? picked = NuGetResolver.SelectWindowsSdkDir(
            InstalledSdks, "10.0.22621.0", _ => true, warnings.Add);

        Assert.AreEqual(@"C:\Kits\10.0.19041.0", picked);
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "10.0.22621.0 is not installed");
    }

    [TestMethod]
    public void SelectWindowsSdkDir_FallsBackToClosestNewerWhenNothingAtOrBelowTarget()
    {
        // Only newer SDKs are installed, so some overshoot is unavoidable. Take the
        // smallest one and say the results may include APIs the target does not have.
        var warnings = new List<string>();
        string? picked = NuGetResolver.SelectWindowsSdkDir(
            InstalledSdks, "10.0.17763.0", _ => true, warnings.Add);

        Assert.AreEqual(@"C:\Kits\10.0.19041.0", picked);
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "at or below");
    }

    [TestMethod]
    public void SelectWindowsSdkDir_MatchesOnBuildNumberWhenRevisionsDisagree()
    {
        var warnings = new List<string>();
        string? picked = NuGetResolver.SelectWindowsSdkDir(
            InstalledSdks, "10.0.26100.1742", _ => true, warnings.Add);

        Assert.AreEqual(@"C:\Kits\10.0.26100.0", picked);
        Assert.AreEqual(0, warnings.Count, "a build-number match is the targeted SDK, so it must not warn");
    }

    [TestMethod]
    public void SelectWindowsSdkDir_UsesNewestWhenProjectTargetsNoPlatformVersion()
    {
        string? picked = NuGetResolver.SelectWindowsSdkDir(InstalledSdks, null, _ => true);

        Assert.AreEqual(@"C:\Kits\10.0.28000.0", picked);
    }

    [TestMethod]
    public void SelectWindowsSdkDir_SkipsCandidatesWithoutWindowsWinmd()
    {
        // An SDK folder can exist without UnionMetadata content; it must not be chosen
        // just because its version matches.
        string? picked = NuGetResolver.SelectWindowsSdkDir(
            InstalledSdks, "10.0.26100.0", dir => dir.EndsWith("19041.0", StringComparison.Ordinal));

        Assert.AreEqual(@"C:\Kits\10.0.19041.0", picked);
    }

    [TestMethod]
    public void FindPackagesFromAssets_SkipsLibraryAbsentFromSelectedTarget()
    {
        // "libraries" is global across every restored target framework. This package is
        // built only by the non-Windows target, so it is not on the Windows compile
        // surface. Without the target check it reaches the scan fallback and its .winmd
        // is indexed, which confirms an API the Windows build cannot compile against.
        string packageRoot = Path.Combine(_dir, "packages");
        string offTarget = Path.Combine(packageRoot, "contoso.nonwindows", "1.0.0", "metadata");
        Directory.CreateDirectory(offTarget);
        File.WriteAllText(Path.Combine(offTarget, "Contoso.NonWindows.winmd"), "x");

        string inTarget = Path.Combine(packageRoot, "contoso.runtime", "2.0.0", "metadata");
        Directory.CreateDirectory(inTarget);
        File.WriteAllText(Path.Combine(inTarget, "Contoso.Runtime.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.26100.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Runtime/2.0.0"] = new { },
                },
                ["net8.0"] = new Dictionary<string, object>
                {
                    ["Contoso.NonWindows/1.0.0"] = new { },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Runtime/2.0.0"] = new { type = "package", path = "contoso.runtime/2.0.0" },
                ["Contoso.NonWindows/1.0.0"] = new { type = "package", path = "contoso.nonwindows/1.0.0" },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        Assert.AreEqual(1, packages.Count);
        Assert.AreEqual("Contoso.Runtime", packages[0].Id);
        CollectionAssert.AreEquivalent(
            ScannedRuntimeWinmd,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void FindPackagesFromAssets_ScansEveryLibraryWhenNoTargetWasSelected()
    {
        // With no "targets" element there is no selected target to judge membership
        // against, so no library may be treated as out-of-target. Every one stays
        // eligible for the scan fallback, as it was before that check existed.
        string packageRoot = Path.Combine(_dir, "packages");
        string metadata = Path.Combine(packageRoot, "contoso.runtime", "2.0.0", "metadata");
        Directory.CreateDirectory(metadata);
        File.WriteAllText(Path.Combine(metadata, "Contoso.Runtime.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Runtime/2.0.0"] = new { type = "package", path = "contoso.runtime/2.0.0" },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        Assert.AreEqual(1, packages.Count);
        CollectionAssert.AreEquivalent(
            ScannedRuntimeWinmd,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void FindPackagesFromAssets_SystemPrefixedPackage_IsIndexedEvenWhenTransitive()
    {
        // The id prefix alone cannot say what belongs to the framework. A project that
        // writes <PackageReference Include="System.Contoso" /> can call every type in it,
        // but the prefix filter drops it, so each of those types answers "does not exist"
        // — the one answer that stops an agent from writing code that would compile.
        // The same is true of a package that only arrives transitively (e.g.
        // System.Transitive/1.0.0 pulled in by another dependency): PackageReference
        // compile assets flow transitively, so it must be indexed too even though the
        // project never names it directly.
        string packageRoot = Path.Combine(_dir, "packages");
        foreach (string id in new[] { "system.contoso", "system.transitive" })
        {
            string metadata = Path.Combine(packageRoot, id, "1.0.0", "metadata");
            Directory.CreateDirectory(metadata);
            File.WriteAllText(Path.Combine(metadata, "Contoso.Runtime.winmd"), "x");
        }

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            libraries = new Dictionary<string, object>
            {
                ["System.Contoso/1.0.0"] = new { type = "package", path = "system.contoso/1.0.0" },
                ["System.Transitive/1.0.0"] = new { type = "package", path = "system.transitive/1.0.0" },
            },
            project = new
            {
                frameworks = new Dictionary<string, object>
                {
                    ["net8.0"] = new
                    {
                        dependencies = new Dictionary<string, object>
                        {
                            ["System.Contoso"] = new { target = "Package" },
                        },
                    },
                },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        CollectionAssert.AreEquivalent(
            SystemContosoAndTransitiveIds,
            packages.Select(p => p.Id).ToArray(),
            "both the directly referenced and transitive System.* packages are indexed");
    }

    [TestMethod]
    public void FindPackagesFromAssets_JunctionedPackageDirectory_IsNotProbed()
    {
        // The package folder itself is checked, but the id/version directory beneath it is
        // named by the same repo-controlled file. A junction committed there is followed by
        // every probe below, so `winapp find-api refresh` on a fresh clone reads from
        // wherever it points.
        string packageRoot = Path.Combine(_dir, "packages");
        Directory.CreateDirectory(packageRoot);

        string outside = Path.Combine(_dir, "Elsewhere", "metadata");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Contoso.Runtime.winmd"), "x");

        string link = Path.Combine(packageRoot, "contoso.runtime");
        if (!TryCreateJunction(link, Path.Combine(_dir, "Elsewhere")))
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        try
        {
            string path = WriteAssets(JsonSerializer.Serialize(new
            {
                packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
                libraries = new Dictionary<string, object>
                {
                    ["Contoso.Runtime/2.0.0"] = new { type = "package", path = "contoso.runtime" },
                },
            }));

            Assert.AreEqual(
                0,
                NuGetResolver.FindPackagesFromAssets(path, projectDir: _dir).Count,
                "a redirected package directory must not be probed or read");
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public void FindPackagesFromAssets_PackageFolderRootIsAJunction_StillResolves()
    {
        // A developer may relocate the global NuGet cache onto another volume with a junction
        // (e.g. %USERPROFILE%\.nuget\packages -> D:\nuget). The junction is the package-folder
        // *root*, not a repo-named id/version segment, so packages under it must still resolve
        // — rejecting them would make every NuGet API disappear for that configuration.
        string projectDir = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(projectDir);

        string realCache = Path.Combine(_dir, "realcache");
        string selected = Path.Combine(realCache, "contoso.metadata", "1.0.0", "lib", "net8.0-windows10.0.26100.0");
        Directory.CreateDirectory(selected);
        File.WriteAllText(Path.Combine(selected, "Contoso.winmd"), "x");

        string linkedCache = Path.Combine(_dir, "linkedcache");
        if (!TryCreateJunction(linkedCache, realCache))
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        try
        {
            string path = WriteAssets(JsonSerializer.Serialize(new
            {
                packageFolders = new Dictionary<string, object> { [linkedCache] = new { } },
                targets = new Dictionary<string, object>
                {
                    ["net8.0-windows10.0.26100.0"] = new Dictionary<string, object>
                    {
                        ["Contoso.Metadata/1.0.0"] = new
                        {
                            compile = new Dictionary<string, object>
                            {
                                ["lib/net8.0-windows10.0.26100.0/Contoso.winmd"] = new { },
                            },
                        },
                    },
                },
                libraries = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new { type = "package", path = "contoso.metadata/1.0.0" },
                },
            }));

            List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path, projectDir: projectDir);

            Assert.AreEqual(1, packages.Count, "a package under a junctioned cache root must still resolve");
            CollectionAssert.AreEquivalent(
                SelectedWinmdOnly,
                packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
        }
        finally
        {
            Directory.Delete(linkedCache);
        }
    }

    [TestMethod]
    public void FindPackagesFromAssets_SelectsWindowsTargetWhenSeveralWereRestored()
    {
        // A multi-targeted project lists several targets, and the non-Windows one can be
        // listed first. Reading its compile assets reports every Windows-only type in the
        // package as missing, even though the Windows build compiles against them.
        string packageRoot = Path.Combine(_dir, "packages");
        string packageDir = Path.Combine(packageRoot, "contoso.metadata", "1.0.0");
        string portable = Path.Combine(packageDir, "lib", "net8.0");
        string windows = Path.Combine(packageDir, "lib", "net8.0-windows10.0.19041.0");
        Directory.CreateDirectory(portable);
        Directory.CreateDirectory(windows);
        File.WriteAllText(Path.Combine(portable, "Portable.winmd"), "x");
        File.WriteAllText(Path.Combine(windows, "Contoso.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object> { ["lib/net8.0/Portable.winmd"] = new { } },
                    },
                },
                ["net8.0-windows10.0.19041.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object> { ["lib/net8.0-windows10.0.19041.0/Contoso.winmd"] = new { } },
                    },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Metadata/1.0.0"] = new { type = "package", path = "contoso.metadata/1.0.0" },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        Assert.AreEqual(1, packages.Count);
        CollectionAssert.AreEquivalent(
            SelectedWinmdOnly,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void FindPackagesFromAssets_SelectsWindowsTargetWithoutAnSdkVersion()
    {
        // A desktop project commonly multi-targets net8.0 and net8.0-windows7.0, which
        // names no Windows SDK version at all. Requiring a three-part version here reads
        // the portable target's assets and reports every Windows-only type as missing.
        string packageRoot = Path.Combine(_dir, "packages");
        string packageDir = Path.Combine(packageRoot, "contoso.metadata", "1.0.0");
        string portable = Path.Combine(packageDir, "lib", "net8.0");
        string windows = Path.Combine(packageDir, "lib", "net8.0-windows7.0");
        Directory.CreateDirectory(portable);
        Directory.CreateDirectory(windows);
        File.WriteAllText(Path.Combine(portable, "Portable.winmd"), "x");
        File.WriteAllText(Path.Combine(windows, "Contoso.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object> { ["lib/net8.0/Portable.winmd"] = new { } },
                    },
                },
                ["net8.0-windows7.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object> { ["lib/net8.0-windows7.0/Contoso.winmd"] = new { } },
                    },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Metadata/1.0.0"] = new { type = "package", path = "contoso.metadata/1.0.0" },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        Assert.AreEqual(1, packages.Count);
        CollectionAssert.AreEquivalent(
            SelectedWinmdOnly,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void ReadTargetPlatformVersion_PicksTheSameTargetTheCompileAssetsComeFrom()
    {
        // Compile assets are read from the highest Windows target. Reading the SDK
        // version from the first one instead pairs 26100 package assets with 19041 SDK
        // metadata, so an API introduced in 26100 is reported missing.
        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.19041.0"] = new Dictionary<string, object>(),
                ["net8.0-windows10.0.26100.0"] = new Dictionary<string, object>(),
            },
            libraries = new Dictionary<string, object>(),
        }));

        Assert.AreEqual("10.0.26100.0", NuGetResolver.ReadTargetPlatformVersion(path));
    }

    [TestMethod]
    public void FindPackagesFromAssets_TreatsPlaceholderOnlyCompileGroupAsNoAssets()
    {
        // A compile group of nothing but NuGet's "_._" placeholder means the package
        // deliberately exposes no compile-time assets for this target. Reading that as
        // "restore named nothing" scans the package and confirms an API from a target
        // the project does not build.
        string packageRoot = Path.Combine(_dir, "packages");
        string packageDir = Path.Combine(packageRoot, "contoso.metadata", "1.0.0");
        string placeholder = Path.Combine(packageDir, "lib", "net8.0");
        string other = Path.Combine(packageDir, "lib", "uap10.0");
        Directory.CreateDirectory(placeholder);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(placeholder, "_._"), string.Empty);
        File.WriteAllText(Path.Combine(other, "Legacy.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.19041.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object> { ["lib/net8.0/_._"] = new { } },
                    },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Metadata/1.0.0"] = new { type = "package", path = "contoso.metadata/1.0.0" },
            },
        }));

        Assert.AreEqual(0, NuGetResolver.FindPackagesFromAssets(path).Count);
    }

    [TestMethod]
    public void FindPackagesFromAssets_SkipsNetworkPackageFolders()
    {
        // packageFolders comes from a file inside the repository, so cloning a repository
        // is enough to choose it. A UNC value turns a local read-only query into an
        // outbound authentication attempt against a host the repository picked.
        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [@"\\192.0.2.1\share"] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.19041.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new { },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Metadata/1.0.0"] = new { type = "package", path = "contoso.metadata/1.0.0" },
            },
        }));

        Assert.AreEqual(0, NuGetResolver.FindPackagesFromAssets(path).Count);
    }

    [TestMethod]
    public void FindPackagesFromAssets_IgnoresCompileAssetsOutsideThePackage()
    {
        // A compile asset name is combined with the package directory, and a rooted or
        // climbing value silently wins over it — reading metadata from anywhere on disk.
        string packageRoot = Path.Combine(_dir, "packages");
        string packageDir = Path.Combine(packageRoot, "contoso.metadata", "1.0.0");
        string outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(packageDir);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Escaped.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.19041.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object>
                        {
                            ["../../../outside/Escaped.winmd"] = new { },
                        },
                    },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Metadata/1.0.0"] = new { type = "package", path = "contoso.metadata/1.0.0" },
            },
        }));

        Assert.AreEqual(0, NuGetResolver.FindPackagesFromAssets(path).Count);
    }

    [TestMethod]
    public void RuntimeReleaseLabel_KeepsNameEncodedReleaseForOnePointX()
    {
        // 1.x encodes the release in the name and uses an unrelated package version.
        Assert.AreEqual(
            "1.8",
            NuGetResolver.RuntimeReleaseLabel("Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_arm64__8wekyb3d8bbwe"));
    }

    [TestMethod]
    public void RuntimeReleaseLabel_CombinesMajorOnlyNameWithPackageVersion()
    {
        // 2.x carries only the major in the name; the real release is in the version,
        // so a bare "2" would understate which runtime answered.
        Assert.AreEqual(
            "2.4",
            NuGetResolver.RuntimeReleaseLabel("Microsoft.WindowsAppRuntime.2_2.4.0.0_arm64__8wekyb3d8bbwe"));
    }

    [TestMethod]
    public void RuntimeReleaseLabel_PreservesExperimentalSuffix()
    {
        Assert.AreEqual(
            "1.7-experimental3",
            NuGetResolver.RuntimeReleaseLabel("Microsoft.WindowsAppRuntime.1.7-experimental3_7000.392.2319.0_arm64__8wekyb3d8bbwe"));
    }

    [TestMethod]
    public void RuntimeReleaseLabel_ReturnsFolderNameWhenNotARuntimePackage()
    {
        Assert.AreEqual("SomethingElse", NuGetResolver.RuntimeReleaseLabel("SomethingElse"));
    }

    #region winapp.yaml projects (Electron and other non-MSBuild apps)

    /// <summary>
    /// Lays out a project that has no MSBuild project file: <c>winapp.yaml</c> plus the
    /// <c>.winapp/winmds.lock.json</c> that <c>winapp restore</c> writes, with the named
    /// package's <c>.winmd</c> files staged under a NuGet-cache-shaped folder so the
    /// resolver's XML-doc lookup has a real package root to walk up to.
    /// </summary>
    private string WriteWinappProject(
        string packageId,
        string version,
        string[] winmdNames,
        int schema = 3,
        bool createWinmdFiles = true,
        string? xmlDocName = null)
    {
        string projectDir = Path.Combine(_dir, "app");
        Directory.CreateDirectory(Path.Combine(projectDir, ".winapp"));
        File.WriteAllText(Path.Combine(projectDir, "winapp.yaml"), $"packages:\n  - name: {packageId}\n    version: {version}\n");

        string packageRoot = Path.Combine(_dir, "nuget", packageId.ToLowerInvariant(), version);
        string libDir = Path.Combine(packageRoot, "lib", "uap10.0");
        Directory.CreateDirectory(libDir);

        var winmdPaths = new List<string>();
        foreach (string name in winmdNames)
        {
            string path = Path.Combine(libDir, name);
            if (createWinmdFiles)
            {
                File.WriteAllText(path, "not real metadata");
            }
            winmdPaths.Add(path);
        }
        if (xmlDocName is not null)
        {
            // FindXmlDocsInPackageFolder skips XML under 1 KB as carrying no real docs.
            File.WriteAllText(Path.Combine(libDir, xmlDocName), new string('x', 2048));
        }

        string lockfile = JsonSerializer.Serialize(new
        {
            schema,
            generated_at = DateTime.UtcNow.ToString("o"),
            packages = new[] { new { name = packageId, version, winmds = winmdPaths } },
        });
        File.WriteAllText(Path.Combine(projectDir, ".winapp", "winmds.lock.json"), lockfile);
        return projectDir;
    }

    [TestMethod]
    public void FindPackagesFromWinmdsLockfile_ReadsPackagesForAProjectWithNoProjectFile()
    {
        string projectDir = WriteWinappProject("Microsoft.WindowsAppSDK", "1.5.240607001", ["Microsoft.UI.Xaml.winmd", "Microsoft.UI.Text.winmd"]);

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromWinmdsLockfile(projectDir);

        Assert.HasCount(1, packages);
        Assert.AreEqual("Microsoft.WindowsAppSDK", packages[0].Id);
        Assert.AreEqual("1.5.240607001", packages[0].Version);
        Assert.HasCount(2, packages[0].WinMdFiles);
    }

    [TestMethod]
    public void FindPackagesFromWinmdsLockfile_FindsXmlDocsInThePackageFolder()
    {
        string projectDir = WriteWinappProject(
            "Microsoft.WindowsAppSDK", "1.5.240607001", ["Microsoft.UI.Xaml.winmd"], xmlDocName: "Microsoft.UI.Xaml.xml");

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromWinmdsLockfile(projectDir);

        Assert.HasCount(1, packages);
        Assert.HasCount(1, packages[0].XmlDocFiles);
        Assert.EndsWith("Microsoft.UI.Xaml.xml", packages[0].XmlDocFiles[0]);
    }

    [TestMethod]
    public void FindPackagesFromWinmdsLockfile_SkipsEntriesWhoseFilesAreGone()
    {
        // A lockfile written before the NuGet cache was cleared still names the files.
        // Indexing them would fail per-package; contributing nothing is the honest answer.
        string projectDir = WriteWinappProject(
            "Microsoft.WindowsAppSDK", "1.5.240607001", ["Microsoft.UI.Xaml.winmd"], createWinmdFiles: false);

        Assert.IsEmpty(NuGetResolver.FindPackagesFromWinmdsLockfile(projectDir));
    }

    [TestMethod]
    public void FindPackagesFromWinmdsLockfile_IgnoresAnUnknownSchema()
    {
        string projectDir = WriteWinappProject(
            "Microsoft.WindowsAppSDK", "1.5.240607001", ["Microsoft.UI.Xaml.winmd"], schema: 99);

        Assert.IsEmpty(NuGetResolver.FindPackagesFromWinmdsLockfile(projectDir));
    }

    [TestMethod]
    public void FindPackagesFromWinmdsLockfile_ReturnsNothingWhenThereIsNoLockfile()
    {
        string projectDir = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(projectDir);

        Assert.IsEmpty(NuGetResolver.FindPackagesFromWinmdsLockfile(projectDir));
    }

    [TestMethod]
    public void FindRestoreOutput_UsesTheLockfileWhenThereIsNoProjectAssetsJson()
    {
        string projectDir = WriteWinappProject("Microsoft.WindowsAppSDK", "1.5.240607001", ["Microsoft.UI.Xaml.winmd"]);

        string? restoreOutput = NuGetResolver.FindRestoreOutput(projectDir);

        Assert.IsNotNull(restoreOutput);
        Assert.EndsWith("winmds.lock.json", restoreOutput);
    }

    [TestMethod]
    public void FindRestoreOutput_PrefersProjectAssetsJsonWhenBothExist()
    {
        string projectDir = WriteWinappProject("Microsoft.WindowsAppSDK", "1.5.240607001", ["Microsoft.UI.Xaml.winmd"]);
        Directory.CreateDirectory(Path.Combine(projectDir, "obj"));
        File.WriteAllText(Path.Combine(projectDir, "obj", "project.assets.json"), "{}");

        string? restoreOutput = NuGetResolver.FindRestoreOutput(projectDir);

        Assert.IsNotNull(restoreOutput);
        Assert.EndsWith("project.assets.json", restoreOutput);
    }

    #endregion
}
