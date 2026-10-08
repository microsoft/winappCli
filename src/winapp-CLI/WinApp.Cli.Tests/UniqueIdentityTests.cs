// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class UniqueIdentityTests : BaseCommandTests
{
    private const string Publisher = "CN=TestPublisher";
    private FakePackageRegistrationService _registration = null!;
    private FakePriService _pri = null!;
    private IMsixService _msix = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _registration = new FakePackageRegistrationService { MatchByName = true };
        _pri = new FakePriService();
        return services
            .AddSingleton<IPackageRegistrationService>(_registration)
            .AddSingleton<IPriService>(_pri)
            .AddSingleton<IDevModeService, FakeDevModeService>()
            .AddSingleton<IWindowsAppRuntimeService, FakeWindowsAppRuntimeService>()
            .AddSingleton<IDotNetService, FakeDotNetService>()
            .AddSingleton<IProjectRunService, FakeProjectRunService>()
            .AddSingleton<INugetService, FakeNugetService>();
    }

    [TestInitialize]
    public void Setup() => _msix = GetRequiredService<IMsixService>();

    private static string Manifest(string name = "Contoso.App", string extensions = "", string applications = "") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
                 xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                 xmlns:build="http://schemas.microsoft.com/developer/appx/2015/build"
                 IgnorableNamespaces="uap uap3 uap5 uap10 build">
          <Identity Name="{name}" Publisher="{Publisher}" Version="1.0.0.0" ProcessorArchitecture="x64" />
          <Properties><DisplayName>App</DisplayName><PublisherDisplayName>Test</PublisherDisplayName><Logo>logo.png</Logo></Properties>
          <Applications>
            <Application Id="App" Executable="App.exe" EntryPoint="Windows.FullTrustApplication">{extensions}</Application>{applications}
          </Applications>
          <build:Metadata><build:Item Name="makepri.exe" Version="10.0.0.0" /></build:Metadata>
        </Package>
        """;

    private const string AliasExtension = """
        <Extensions><uap5:Extension Category="windows.appExecutionAlias"><uap5:AppExecutionAlias><uap5:ExecutionAlias Alias="tool.exe" /></uap5:AppExecutionAlias></uap5:Extension></Extensions>
        """;

    /// <summary>Writes an MSBuild-style build output (manifest, exe, recipe) and returns its manifest.</summary>
    private static FileInfo WriteBuildOutput(DirectoryInfo output, string manifest)
    {
        output.Create();
        var manifestFile = new FileInfo(Path.Join(output.FullName, "AppxManifest.xml"));
        File.WriteAllText(manifestFile.FullName, manifest);
        var exe = Path.Join(output.FullName, "App.exe");
        File.WriteAllText(exe, "exe");
        File.WriteAllText(Path.Join(output.FullName, "App.build.appxrecipe"), $"""
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <AppXManifest Include="{manifestFile.FullName}"><PackagePath>appxmanifest.xml</PackagePath></AppXManifest>
                <AppxPackagedFile Include="{exe}"><PackagePath>App.exe</PackagePath></AppxPackagedFile>
              </ItemGroup>
            </Project>
            """);
        return manifestFile;
    }

    private Task<MsixIdentityResult> RunAsync(FileInfo manifest, DirectoryInfo layout, string owner, bool unique, bool alias = false) =>
        _msix.AddLooseLayoutIdentityAsync(manifest, manifest.Directory!, layout, TestTaskContext, LayoutReconciliation.Exact,
            ensureExecutionAlias: alias, developmentIdentity: new DevelopmentIdentityOptions(owner, unique),
            cancellationToken: TestContext.CancellationToken);

    // ---- Derivation ----

    [TestMethod]
    public void DeriveName_IsStableForOnePathAndDiffersAcrossPaths()
    {
        var a = _tempDirectory.CreateSubdirectory("worktree-a").FullName;
        var b = _tempDirectory.CreateSubdirectory("worktree-b").FullName;

        var name = DevelopmentIdentityHelper.DeriveName(a, "Contoso.App");

        Assert.AreEqual(name, DevelopmentIdentityHelper.DeriveName(a.ToLowerInvariant() + "\\", "Contoso.App"));
        Assert.AreNotEqual(name, DevelopmentIdentityHelper.DeriveName(b, "Contoso.App"));
        StringAssert.Matches(name, new System.Text.RegularExpressions.Regex(@"^Contoso\.App\.w[0-9a-f]{24}$"));
    }

    [TestMethod]
    public void DeriveName_TruncatesLongNamesToFitThePackageNameLimit()
    {
        var name = DevelopmentIdentityHelper.DeriveName(_tempDirectory.FullName, new string('A', 50));

        Assert.AreEqual(50, name.Length);
        Assert.StartsWith(new string('A', 24) + ".w", name);
    }

    [TestMethod]
    public async Task CanonicalizePath_GivesAJunctionTheSameIdentityAsItsTarget()
    {
        var target = _tempDirectory.CreateSubdirectory("real-checkout");
        var link = Path.Join(_tempDirectory.FullName, "linked-checkout");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/d", "/c", "mklink", "/J", link, target.FullName])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        await process.WaitForExitAsync(TestContext.CancellationToken);
        if (process.ExitCode != 0)
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        Assert.AreEqual(DevelopmentIdentityHelper.CanonicalizePath(target.FullName), DevelopmentIdentityHelper.CanonicalizePath(link));
    }

    [TestMethod]
    public void ApplyDevelopmentIdentity_RenamesEveryStagedAliasOnce()
    {
        // A staged alias is whatever the manifest resolved to, e.g. $targetnametoken$.exe -> app.exe.
        var staged = AppxManifestDocument.Parse(Manifest(extensions: AliasExtension));
        var identity = DevelopmentIdentityHelper.Create(staged, _tempDirectory.FullName);

        identity = staged.ApplyDevelopmentIdentity(identity);
        var again = staged.ApplyDevelopmentIdentity(identity);

        var renamed = "tool" + identity.PackageName[^26..] + ".exe";
        Assert.AreEqual(renamed, identity.Aliases["tool.exe"]);
        Assert.AreEqual(renamed, staged.GetExecutionAliases().Single());
        Assert.AreEqual(renamed, again.Aliases[renamed], "A second rename must not add another suffix");
        Assert.AreEqual(identity.PackageName, staged.IdentityName);
        Assert.AreEqual(AppLauncherService.ComputeFamilyName(identity.PackageName, Publisher), identity.PackageFamilyName);
    }

    // ---- Supported manifests ----

    [TestMethod]
    [DataRow(AliasExtension)]
    [DataRow("""<Extensions><uap5:Extension Category="windows.appExecutionAlias"><uap5:AppExecutionAlias><uap5:ExecutionAlias Alias="a.exe" /><uap5:ExecutionAlias Alias="b.exe" /></uap5:AppExecutionAlias></uap5:Extension></Extensions>""")]
    public void ValidateUniqueIdentitySupport_AcceptsExecutionAliases(string extensions) =>
        AppxManifestDocument.Parse(Manifest(extensions: extensions)).ValidateUniqueIdentitySupport();

    [TestMethod]
    [DataRow("""<Extensions><uap:Extension Category="windows.protocol"><uap:Protocol Name="contoso" /></uap:Extension></Extensions>""", "", "windows.protocol")]
    [DataRow("""<Extensions><uap:Extension Category="windows.fileTypeAssociation"><uap:FileTypeAssociation Name="txt" /></uap:Extension></Extensions>""", "", "windows.fileTypeAssociation")]
    [DataRow("", """<Application Id="Second" Executable="B.exe" EntryPoint="Windows.FullTrustApplication" />""", "exactly one Application")]
    public void ValidateUniqueIdentitySupport_RejectsSharedRegistrations(string extensions, string applications, string reason)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            AppxManifestDocument.Parse(Manifest(extensions: extensions, applications: applications)).ValidateUniqueIdentitySupport());

        StringAssert.Contains(error.Message, reason);
        StringAssert.Contains(error.Message, "Run without --unique-identity");
    }

    [TestMethod]
    public void ValidateUniqueIdentitySupport_RejectsSparsePackages()
    {
        var manifest = Manifest().Replace("<Logo>logo.png</Logo>", "<Logo>logo.png</Logo><uap10:AllowExternalContent>true</uap10:AllowExternalContent>", StringComparison.Ordinal);

        Assert.ThrowsExactly<InvalidOperationException>(() => AppxManifestDocument.Parse(manifest).ValidateUniqueIdentitySupport());
    }

    // ---- Run ----

    [TestMethod]
    public async Task UniqueRun_RegistersTheDerivedNameWithoutChangingTheSourceManifest()
    {
        var output = new DirectoryInfo(Path.Join(_tempDirectory.FullName, "bin"));
        var manifest = WriteBuildOutput(output, Manifest(extensions: AliasExtension));
        var source = File.ReadAllText(manifest.FullName);
        var layout = new DirectoryInfo(Path.Join(output.FullName, "AppX"));

        var result = await RunAsync(manifest, layout, _tempDirectory.FullName, unique: true);

        var expected = DevelopmentIdentityHelper.DeriveName(_tempDirectory.FullName, "Contoso.App");
        Assert.AreEqual(expected, result.PackageName);
        Assert.AreEqual(expected, result.Identity!.PackageName);
        var staged = AppxManifestDocument.Load(Path.Join(layout.FullName, "appxmanifest.xml"));
        Assert.AreEqual(expected, staged.IdentityName);
        Assert.AreEqual(result.Identity.Aliases["tool.exe"], staged.GetExecutionAliases().Single());
        Assert.AreEqual(source, File.ReadAllText(manifest.FullName));
        Assert.HasCount(1, _registration.RegisterLooseLayoutCalls);
        CollectionAssert.AreEqual(new[] { (layout.FullName, expected) }, _pri.ReindexIdentityCalls);
    }

    [TestMethod]
    public async Task UniqueRun_GeneratedAliasComesFromTheDerivedFamily()
    {
        var output = new DirectoryInfo(Path.Join(_tempDirectory.FullName, "bin"));
        var layout = new DirectoryInfo(Path.Join(output.FullName, "AppX"));

        var result = await RunAsync(WriteBuildOutput(output, Manifest()), layout, _tempDirectory.FullName, unique: true, alias: true);

        var staged = AppxManifestDocument.Load(Path.Join(layout.FullName, "appxmanifest.xml"));
        Assert.AreEqual(ExecutionAliasResolver.BuildDefaultAliasName(result.Identity!.PackageFamilyName), staged.GetExecutionAliases().Single());
    }

    [TestMethod]
    public async Task UniqueRun_UnchangedRerunStillReportsTheIdentity()
    {
        var output = new DirectoryInfo(Path.Join(_tempDirectory.FullName, "bin"));
        var manifest = WriteBuildOutput(output, Manifest());
        var layout = new DirectoryInfo(Path.Join(output.FullName, "AppX"));
        var first = await RunAsync(manifest, layout, _tempDirectory.FullName, unique: true);
        _registration.FakeDevPackages =
        [
            new DevPackageInfo($"{first.PackageName}_1.0.0.0_x64__abc", first.PackageName, "1.0.0.0", layout.FullName, IsDevelopmentMode: true, Publisher),
        ];

        var second = await RunAsync(manifest, layout, _tempDirectory.FullName, unique: true);

        Assert.HasCount(1, _registration.RegisterLooseLayoutCalls, "The unchanged rerun should skip registration");
        Assert.AreEqual(first.Identity!.PackageFamilyName, second.Identity!.PackageFamilyName);
        Assert.AreEqual(first.Identity.OwnerPath, second.Identity.OwnerPath);
    }

    [TestMethod]
    public async Task UniqueRun_RerunKeepsTheSameName()
    {
        var output = new DirectoryInfo(Path.Join(_tempDirectory.FullName, "bin"));
        var manifest = WriteBuildOutput(output, Manifest());
        var layout = new DirectoryInfo(Path.Join(output.FullName, "AppX"));

        var first = await RunAsync(manifest, layout, _tempDirectory.FullName, unique: true);
        var second = await RunAsync(manifest, layout, _tempDirectory.FullName, unique: true);

        Assert.AreEqual(first.PackageName, second.PackageName);
        Assert.AreEqual(first.PackageName, AppxManifestDocument.Load(Path.Join(layout.FullName, "appxmanifest.xml")).IdentityName);
    }

    [TestMethod]
    public async Task UniqueRun_LeavesAnotherCheckoutsRegistrationAlone()
    {
        var other = Path.Join(_tempDirectory.FullName, "other-checkout", "AppX");
        _registration.FakeDevPackages =
        [
            new DevPackageInfo("Contoso.App_1.0.0.0_x64__abc", "Contoso.App", "1.0.0.0", other, IsDevelopmentMode: true, Publisher),
        ];
        var output = new DirectoryInfo(Path.Join(_tempDirectory.FullName, "bin"));

        await RunAsync(WriteBuildOutput(output, Manifest()), new DirectoryInfo(Path.Join(output.FullName, "AppX")), _tempDirectory.FullName, unique: true);

        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
        Assert.HasCount(1, _registration.RegisterLooseLayoutCalls);
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task SwitchingModes_ReplacesTheLayoutsOldRegistrationAndItsResources(bool firstUnique, bool secondUnique)
    {
        var output = new DirectoryInfo(Path.Join(_tempDirectory.FullName, "bin"));
        var manifest = WriteBuildOutput(output, Manifest());
        var layout = new DirectoryInfo(Path.Join(output.FullName, "AppX"));
        var first = await RunAsync(manifest, layout, _tempDirectory.FullName, unique: firstUnique);
        var stalePri = Path.Join(layout.FullName, "resources.pri");
        File.WriteAllText(stalePri, $"indexed for {first.PackageName}");
        var elsewhere = Path.Join(_tempDirectory.FullName, "other-checkout", "AppX");
        _registration.FakeDevPackages =
        [
            new DevPackageInfo($"{first.PackageName}_1.0.0.0_x64__mine", first.PackageName, "1.0.0.0", layout.FullName, IsDevelopmentMode: true, Publisher),
            new DevPackageInfo($"{first.PackageName}_1.0.0.0_x64__theirs", first.PackageName, "1.0.0.0", elsewhere, IsDevelopmentMode: true, Publisher),
        ];

        var second = await RunAsync(manifest, layout, _tempDirectory.FullName, unique: secondUnique);

        Assert.AreNotEqual(first.PackageName, second.PackageName);
        Assert.IsFalse(File.Exists(stalePri), "A PRI indexed for the other identity must not be reused");
        Assert.Contains(($"{first.PackageName}_1.0.0.0_x64__mine", true), _registration.UnregisterByFullNameCalls);
        if (secondUnique)
        {
            // A unique run never touches another checkout's registration of the original name.
            Assert.DoesNotContain(($"{first.PackageName}_1.0.0.0_x64__theirs", true), _registration.UnregisterByFullNameCalls);
        }
    }

    [TestMethod]
    public async Task UniqueRun_RejectsUnsupportedManifestBeforeStagingAnything()
    {
        var output = new DirectoryInfo(Path.Join(_tempDirectory.FullName, "bin"));
        var manifest = WriteBuildOutput(output, Manifest(extensions: """<Extensions><uap:Extension Category="windows.protocol"><uap:Protocol Name="contoso" /></uap:Extension></Extensions>"""));
        var layout = new DirectoryInfo(Path.Join(output.FullName, "AppX"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RunAsync(manifest, layout, _tempDirectory.FullName, unique: true));

        Assert.IsFalse(layout.Exists);
        Assert.IsEmpty(_registration.RegisterLooseLayoutCalls);
    }

    [TestMethod]
    public async Task PlainRun_AfterTheFolderIsMoved_ReplacesTheRegistrationLeftAtTheOldPath()
    {
        // Moving or renaming a checkout must not leave the next run stuck on stale state.
        var original = new DirectoryInfo(Path.Join(_tempDirectory.FullName, "app", "bin"));
        await RunAsync(WriteBuildOutput(original, Manifest()), new DirectoryInfo(Path.Join(original.FullName, "AppX")), original.FullName, unique: false);
        var moved = Path.Join(_tempDirectory.FullName, "app-moved");
        Directory.Move(Path.Join(_tempDirectory.FullName, "app"), moved);
        _registration.FakeDevPackages =
        [
            new DevPackageInfo("Contoso.App_1.0.0.0_x64__abc", "Contoso.App", "1.0.0.0", Path.Join(original.FullName, "AppX"), IsDevelopmentMode: true, Publisher),
        ];
        var movedOutput = new DirectoryInfo(Path.Join(moved, "bin"));

        // A rebuild in the new location rewrites the recipe's absolute paths.
        var result = await RunAsync(WriteBuildOutput(movedOutput, Manifest()),
            new DirectoryInfo(Path.Join(movedOutput.FullName, "AppX")), movedOutput.FullName, unique: false);

        Assert.AreEqual("Contoso.App", result.PackageName);
        Assert.IsNull(result.Identity);
        Assert.AreEqual("Contoso.App_1.0.0.0_x64__abc", _registration.UnregisterByFullNameCalls.Single().PackageFullName);
        Assert.HasCount(2, _registration.RegisterLooseLayoutCalls);
    }

    // ---- PRI ----

    [TestMethod]
    public async Task ReindexIdentity_RebuildsResourcesPriUnderTheNewName()
    {
        var tools = new FakeBuildToolsService { Handler = FakeBuildToolsService.EmulateSdkToolOutput };
        var layout = _tempDirectory.CreateSubdirectory("layout");
        File.WriteAllText(Path.Join(layout.FullName, "resources.pri"), "original");

        await new PriService(tools).ReindexIdentityAsync(layout, "Contoso.App.w0123", TestTaskContext, TestContext.CancellationToken);

        var (tool, arguments) = tools.Invocations.Single();
        Assert.AreEqual("makepri.exe", tool);
        StringAssert.Contains(arguments, "/in \"Contoso.App.w0123\"");
        Assert.AreNotEqual("original", File.ReadAllText(Path.Join(layout.FullName, "resources.pri")));
        Assert.AreEqual(1, layout.GetFileSystemInfos().Length, "Nothing but resources.pri belongs in the layout");
    }

    [TestMethod]
    public async Task ReindexIdentity_WithoutResourcesPri_DoesNothing()
    {
        var tools = new FakeBuildToolsService();

        await new PriService(tools).ReindexIdentityAsync(_tempDirectory, "Contoso.App.w0123", TestTaskContext, TestContext.CancellationToken);

        Assert.IsEmpty(tools.Invocations);
    }

    // ---- Unregister ----

    private Task<int> UnregisterAsync(params string[] args) =>
        ParseAndInvokeWithCaptureAsync(GetRequiredService<UnregisterCommand>(), args);

    [TestMethod]
    public async Task Unregister_ProjectInput_RemovesItsUniqueRegistration()
    {
        var project = _tempDirectory.CreateSubdirectory("worktree-a");
        var csproj = Path.Join(project.FullName, "App.csproj");
        File.WriteAllText(csproj, "<Project />");
        File.WriteAllText(Path.Join(project.FullName, "Package.appxmanifest"), Manifest());
        var derived = DevelopmentIdentityHelper.DeriveName(csproj, "Contoso.App");
        _registration.FakeDevPackages =
        [
            new DevPackageInfo($"{derived}_1.0.0.0_x64__abc", derived, "1.0.0.0", Path.Join(project.FullName, "bin", "AppX"), IsDevelopmentMode: true, Publisher),
        ];

        var exitCode = await UnregisterAsync(csproj);

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.AreEqual($"{derived}_1.0.0.0_x64__abc", _registration.UnregisterByFullNameCalls.Single().PackageFullName);
    }

    [TestMethod]
    public async Task Unregister_ProjectInput_DoesNotRemoveAnotherCheckoutsUniqueRegistration()
    {
        var mine = _tempDirectory.CreateSubdirectory("worktree-a");
        var csproj = Path.Join(mine.FullName, "App.csproj");
        File.WriteAllText(csproj, "<Project />");
        File.WriteAllText(Path.Join(mine.FullName, "Package.appxmanifest"), Manifest());
        var theirs = DevelopmentIdentityHelper.DeriveName(Path.Join(_tempDirectory.FullName, "worktree-b", "App.csproj"), "Contoso.App");
        _registration.FakeDevPackages =
        [
            new DevPackageInfo($"{theirs}_1.0.0.0_x64__abc", theirs, "1.0.0.0", Path.Join(_tempDirectory.FullName, "worktree-b", "AppX"), IsDevelopmentMode: true, Publisher),
        ];

        var exitCode = await UnregisterAsync(csproj);

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
        Assert.DoesNotContain(theirs, _registration.FindDevPackagesCalls);
    }

    [TestMethod]
    public async Task Unregister_ManifestInProjectFolder_FindsTheUniqueRegistrationOfItsProject()
    {
        var project = _tempDirectory.CreateSubdirectory("worktree-a");
        var csproj = Path.Join(project.FullName, "App.csproj");
        File.WriteAllText(csproj, "<Project />");
        var manifest = Path.Join(project.FullName, "Package.appxmanifest");
        File.WriteAllText(manifest, Manifest());
        var derived = DevelopmentIdentityHelper.DeriveName(csproj, "Contoso.App");

        var exitCode = await UnregisterAsync("--manifest", manifest);

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.Contains(derived, _registration.FindDevPackagesCalls);
    }

    [TestMethod]
    public async Task Unregister_FolderInput_FindsTheUniqueRegistrationOfTheProjectInside()
    {
        var project = _tempDirectory.CreateSubdirectory("worktree-a");
        var csproj = Path.Join(project.FullName, "App.csproj");
        File.WriteAllText(csproj, "<Project />");
        File.WriteAllText(Path.Join(project.FullName, "Package.appxmanifest"), Manifest());

        var exitCode = await UnregisterAsync(project.FullName);

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.Contains(DevelopmentIdentityHelper.DeriveName(csproj, "Contoso.App"), _registration.FindDevPackagesCalls);
        Assert.Contains(DevelopmentIdentityHelper.DeriveName(project.FullName, "Contoso.App"), _registration.FindDevPackagesCalls);
    }

    [TestMethod]
    public async Task Unregister_ProjectInputFromParent_LeavesASiblingCheckoutsPlainRegistration()
    {
        // From the parent: `run .\a --unique-identity`, `run .\b`, then `unregister .\a`.
        var a = _tempDirectory.CreateSubdirectory("a");
        var csproj = Path.Join(a.FullName, "App.csproj");
        File.WriteAllText(csproj, "<Project />");
        File.WriteAllText(Path.Join(a.FullName, "Package.appxmanifest"), Manifest());
        var derived = DevelopmentIdentityHelper.DeriveName(csproj, "Contoso.App");
        _registration.FakeDevPackages =
        [
            new DevPackageInfo($"{derived}_1.0.0.0_x64__abc", derived, "1.0.0.0", Path.Join(a.FullName, "bin", "AppX"), IsDevelopmentMode: true, Publisher),
            new DevPackageInfo("Contoso.App_1.0.0.0_x64__abc", "Contoso.App", "1.0.0.0", Path.Join(_tempDirectory.FullName, "b", "bin", "AppX"), IsDevelopmentMode: true, Publisher),
        ];

        var exitCode = await UnregisterAsync(csproj);

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.AreEqual($"{derived}_1.0.0.0_x64__abc", _registration.UnregisterByFullNameCalls.Single().PackageFullName);
    }

    [TestMethod]
    public async Task Unregister_BuildOutputFolder_UsesTheCurrentDirectorysManifestLikeRun()
    {
        // `run .\out --unique-identity` from a folder whose manifest sits beside the build output.
        File.WriteAllText(Path.Join(_tempDirectory.FullName, "Package.appxmanifest"), Manifest());
        var output = _tempDirectory.CreateSubdirectory("out");
        var derived = DevelopmentIdentityHelper.DeriveName(output.FullName, "Contoso.App");
        _registration.FakeDevPackages =
        [
            new DevPackageInfo($"{derived}_1.0.0.0_x64__abc", derived, "1.0.0.0", Path.Join(output.FullName, "AppX"), IsDevelopmentMode: true, Publisher),
        ];

        var exitCode = await UnregisterAsync(output.FullName);

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.AreEqual($"{derived}_1.0.0.0_x64__abc", _registration.UnregisterByFullNameCalls.Single().PackageFullName);
    }

    [TestMethod]
    public void Create_RejectsAPackageNameThatCouldInjectToolArguments()
    {
        var document = AppxManifestDocument.Parse(Manifest(name: "ab&quot; /cf &quot;C:\\evil.xml"));

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => DevelopmentIdentityHelper.Create(document, _tempDirectory.FullName));

        StringAssert.Contains(error.Message, "not a valid package name");
    }

    [TestMethod]
    public async Task ReindexIdentity_RejectsAnInvalidNameBeforeRunningMakePri()
    {
        var tools = new FakeBuildToolsService();
        File.WriteAllText(Path.Join(_tempDirectory.FullName, "resources.pri"), "original");

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            new PriService(tools).ReindexIdentityAsync(_tempDirectory, "ab\" /cf \"x", TestTaskContext, TestContext.CancellationToken));

        Assert.IsEmpty(tools.Invocations);
    }

    [TestMethod]
    public async Task Unregister_RejectsAnUnsupportedInputFile()
    {
        var file = Path.Join(_tempDirectory.FullName, "notes.txt");
        File.WriteAllText(file, "");

        var exitCode = await UnregisterAsync(file, "--json");

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, ".csproj");
    }
}
