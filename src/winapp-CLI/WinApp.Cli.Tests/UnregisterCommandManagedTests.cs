// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class UnregisterCommandManagedTests : BaseCommandTests
{
    private FakePackageRegistrationService _packages = null!;
    private FakeProjectRunService _projects = null!;

    private const string ManifestXml = """
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
          <Identity Name="TestPackage" Publisher="CN=TestPublisher" Version="1.0.0.0" ProcessorArchitecture="x64" />
          <Properties><DisplayName>Test App</DisplayName><PublisherDisplayName>Test</PublisherDisplayName><Logo>logo.png</Logo></Properties>
          <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
          <Applications><Application Id="App" Executable="app.exe" EntryPoint="Windows.FullTrustApplication">
            <uap:VisualElements DisplayName="Test App" Description="Test" BackgroundColor="transparent" Square150x150Logo="logo.png" Square44x44Logo="logo.png" />
          </Application></Applications>
        </Package>
        """;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _packages = new FakePackageRegistrationService();
        _projects = new FakeProjectRunService();
        return services.AddSingleton<IPackageRegistrationService>(_packages)
            .AddSingleton<IProjectRunService>(_projects);
    }

    [TestMethod]
    [DataRow(".cs", false)]
    [DataRow(".cs", true)]
    [DataRow(".csproj", false)]
    [DataRow(".csproj", true)]
    [DataRow(".sln", true)]
    [DataRow(".slnx", true)]
    [DataRow("directory", true)]
    public async Task InputResolvesRecordedOwnerWithoutBuildingOrEvaluatingIdentity(string inputKind, bool unique)
    {
        var projectDirectory = _tempDirectory.CreateSubdirectory("app");
        FileSystemInfo input;
        FileSystemInfo owner;
        if (inputKind == "directory")
        {
            input = owner = projectDirectory;
        }
        else
        {
            input = WriteFile(Path.Join(projectDirectory.FullName, "App" + inputKind), "");
            owner = input;
            if (inputKind is ".sln" or ".slnx")
            {
                var project = WriteFile(Path.Join(projectDirectory.FullName, "App.csproj"), "<Project />");
                owner = project;
                _projects.InputResolutionOverride = new RunInputResolution(
                    WinAppRunMode.Project, project, projectDirectory, Solution: (FileInfo)input);
            }
        }
        var registration = CreateRegistration(owner, "layout", unique);

        var exitCode = await InvokeAsync(input.FullName, "--json");

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.HasCount(1, _projects.ResolveInputCalls);
        AssertNoBuildOrIdentityEvaluation();
        Assert.AreEqual(registration.Identity.PackageFullName, _packages.UnregisterByFullNameCalls.Single().PackageFullName);
        Assert.IsFalse(_packages.UnregisterByFullNameCalls.Single().PreserveAppData);
        Assert.IsFalse(_packages.FindDevPackagesCalls.Any(name => name.EndsWith(".debug", StringComparison.Ordinal)));
        Assert.IsNull(DevelopmentRegistrationStore.Read(new DirectoryInfo(registration.Identity.LayoutPath)));
    }

    [TestMethod]
    public async Task CurrentDirectoryAndResolvedProjectUseTheSameOwner()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.csproj"), "<Project />");
        _projects.InputResolutionOverride = new RunInputResolution(WinAppRunMode.Project, owner, _tempDirectory);
        var registration = CreateRegistration(owner, "layout");

        var exitCode = await InvokeAsync("--json");

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.AreEqual(registration.Identity.PackageFullName, _packages.UnregisterByFullNameCalls.Single().PackageFullName);
        AssertNoBuildOrIdentityEvaluation();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitLayoutWorksAfterSourceIsDeletedWithoutAManifestArgument(bool includeDeletedInput)
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.cs"), "");
        var registration = CreateRegistration(owner, "layout");
        owner.Delete();
        string[] args = includeDeletedInput
            ? [owner.FullName, "--output-appx-directory", registration.Identity.LayoutPath, "--json"]
            : ["--output-appx-directory", registration.Identity.LayoutPath, "--json"];

        var exitCode = await InvokeAsync(args);

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.IsEmpty(_projects.ResolveInputCalls);
        AssertNoBuildOrIdentityEvaluation();
        Assert.AreEqual(registration.Identity.PackageFullName, _packages.UnregisterByFullNameCalls.Single().PackageFullName);
    }

    [TestMethod]
    public async Task ExplicitLayoutDoesNotRequireAnIndexInTheCurrentStateRoot()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.cs"), "");
        var registration = CreateRegistration(owner, "layout");
        var otherStateRoot = _tempDirectory.CreateSubdirectory("other-state");
        GetRequiredService<IWinappDirectoryService>().SetCacheDirectoryForTesting(otherStateRoot);

        var exitCode = await InvokeAsync("--output-appx-directory", registration.Identity.LayoutPath, "--json");

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.AreEqual(registration.Identity.PackageFullName, _packages.UnregisterByFullNameCalls.Single().PackageFullName);
        Assert.IsNull(DevelopmentRegistrationStore.Read(new DirectoryInfo(registration.Identity.LayoutPath)));
        AssertNoBuildOrIdentityEvaluation();
    }

    [TestMethod]
    public async Task ExplicitLayoutThroughAncestorJunctionUsesPhysicalOwnership()
    {
        var physical = _tempDirectory.CreateSubdirectory("physical");
        var owner = WriteFile(Path.Join(physical.FullName, "App.cs"), "");
        var registration = CreateRegistration(owner, Path.Join("physical", "AppX"));
        var alias = Path.Join(_tempDirectory.FullName, "alias");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/d", "/c", "mklink", "/J", alias, physical.FullName },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.IsNotNull(process);
        await process.WaitForExitAsync(TestContext.CancellationToken);
        if (process.ExitCode != 0)
        {
            Assert.Inconclusive($"Could not create a directory junction: {await process.StandardError.ReadToEndAsync(TestContext.CancellationToken)}");
        }

        try
        {
            var exitCode = await InvokeAsync("--output-appx-directory", Path.Join(alias, "AppX"), "--json");

            Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
            Assert.AreEqual(registration.Identity.PackageFullName, _packages.UnregisterByFullNameCalls.Single().PackageFullName);
            Assert.IsNull(DevelopmentRegistrationStore.Read(new DirectoryInfo(registration.Identity.LayoutPath)));
            AssertNoBuildOrIdentityEvaluation();
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    [TestMethod]
    public async Task ExplicitPhysicalLayoutStillRejectsASymbolicLinkReceipt()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.cs"), "");
        var registration = CreateRegistration(owner, "layout");
        var receiptPath = DevelopmentRegistrationStore.ReceiptPath(new DirectoryInfo(registration.Identity.LayoutPath));
        var actualReceipt = Path.Join(_tempDirectory.FullName, "receipt-target.json");
        File.Move(receiptPath, actualReceipt);
        try
        {
            File.CreateSymbolicLink(receiptPath, actualReceipt);
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Inconclusive("Creating symbolic links is not permitted on this machine.");
        }

        var exitCode = await InvokeAsync("--output-appx-directory", registration.Identity.LayoutPath, "--force", "--json");

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(JsonError(), "symbolic link");
        Assert.IsEmpty(_packages.UnregisterByFullNameCalls);
        Assert.HasCount(1, _packages.FakeDevPackages);
        Assert.IsTrue(File.Exists(actualReceipt));
    }

    [TestMethod]
    public async Task UnrelatedHintWithAMissingSidecarDoesNotBlockOwnerSelection()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.csproj"), "<Project />");
        var other = WriteFile(Path.Join(_tempDirectory.FullName, "Other.csproj"), "<Project />");
        var selected = CreateRegistration(owner, "selected");
        var stale = CreateRegistration(other, "stale");
        var staleLayout = new DirectoryInfo(stale.Identity.LayoutPath);
        File.Delete(DevelopmentRegistrationStore.ReceiptPath(staleLayout));
        File.Delete(Path.Join(staleLayout.FullName, "appxmanifest.xml"));
        staleLayout.Delete();
        _packages.FakeDevPackages.RemoveAll(package => package.FullName == stale.Identity.PackageFullName);

        var exitCode = await InvokeAsync(owner.FullName, "--json");

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.AreEqual(selected.Identity.PackageFullName, _packages.UnregisterByFullNameCalls.Single().PackageFullName);
        AssertNoBuildOrIdentityEvaluation();
    }

    [TestMethod]
    public async Task MissingSidecarHintIsNotProofToRemoveItsLivePackage()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.csproj"), "<Project />");
        var registration = CreateRegistration(owner, "layout");
        File.Delete(DevelopmentRegistrationStore.ReceiptPath(new DirectoryInfo(registration.Identity.LayoutPath)));

        var exitCode = await InvokeAsync(owner.FullName, "--force", "--json");

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.IsEmpty(_packages.UnregisterByFullNameCalls);
        Assert.HasCount(1, _packages.FakeDevPackages);
        AssertNoBuildOrIdentityEvaluation();
    }

    [TestMethod]
    public async Task AlreadyAbsentOwnedPackageReportsAbsenceRatherThanRemoval()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.csproj"), "<Project />");
        var registration = CreateRegistration(owner, "layout");
        _packages.FakeDevPackages.Clear();

        var exitCode = await InvokeAsync(owner.FullName, "--json");

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.IsEmpty(_packages.UnregisterByFullNameCalls);
        Assert.IsFalse(JsonDocument.Parse(TestAnsiConsole.Output).RootElement.TryGetProperty("Unregistered", out _));
        Assert.IsNull(DevelopmentRegistrationStore.Read(new DirectoryInfo(registration.Identity.LayoutPath)));
    }

    [TestMethod]
    public async Task CorruptIndexedSidecarIsNotSilentlyTreatedAsAMissingHint()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.csproj"), "<Project />");
        var other = WriteFile(Path.Join(_tempDirectory.FullName, "Other.csproj"), "<Project />");
        CreateRegistration(owner, "selected");
        var corrupt = CreateRegistration(other, "corrupt");
        File.WriteAllText(DevelopmentRegistrationStore.ReceiptPath(new DirectoryInfo(corrupt.Identity.LayoutPath)), "{invalid");

        var exitCode = await InvokeAsync(owner.FullName, "--json");

        Assert.AreEqual(1, exitCode);
        Assert.IsNotNull(JsonError());
        Assert.IsEmpty(_packages.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task MultipleOwnerRecordsRequireAnExplicitLayout()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.cs"), "");
        CreateRegistration(owner, "unique", unique: true);
        CreateRegistration(owner, "normal", unique: false);

        var exitCode = await InvokeAsync(owner.FullName, "--force", "--json");

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(JsonError(), "--output-appx-directory");
        Assert.IsEmpty(_packages.UnregisterByFullNameCalls);
        AssertNoBuildOrIdentityEvaluation();
    }

    [TestMethod]
    public async Task ExplicitLayoutSelectsOnlyOneOfSeveralOwnerRecords()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.cs"), "");
        var unique = CreateRegistration(owner, "unique", unique: true);
        var normal = CreateRegistration(owner, "normal", unique: false);

        var exitCode = await InvokeAsync(owner.FullName, "--output-appx-directory", unique.Identity.LayoutPath, "--json");

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.AreEqual(unique.Identity.PackageFullName, _packages.UnregisterByFullNameCalls.Single().PackageFullName);
        Assert.IsNotNull(DevelopmentRegistrationStore.Read(new DirectoryInfo(normal.Identity.LayoutPath)));
        Assert.IsTrue(_packages.FakeDevPackages.Any(package => package.FullName == normal.Identity.PackageFullName));
    }

    [TestMethod]
    public async Task ForceCannotRemoveAnExplicitLayoutOwnedByAnotherApp()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "Owner.cs"), "");
        var other = WriteFile(Path.Join(_tempDirectory.FullName, "Other.cs"), "");
        var registration = CreateRegistration(owner, "layout");

        var exitCode = await InvokeAsync(other.FullName, "--output-appx-directory", registration.Identity.LayoutPath, "--force", "--json");

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(JsonError(), "different app");
        Assert.IsEmpty(_packages.UnregisterByFullNameCalls);
    }

    [TestMethod]
    [DataRow("location")]
    [DataRow("publisher")]
    [DataRow("family")]
    [DataRow("non-development")]
    [DataRow("full-name")]
    public async Task ForceCannotBypassLiveRegistrationDisagreement(string mismatch)
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.csproj"), "<Project />");
        var registration = CreateRegistration(owner, "layout");
        var live = _packages.FakeDevPackages.Single();
        _packages.FakeDevPackages[0] = mismatch switch
        {
            "location" => live with { InstallLocation = _tempDirectory.CreateSubdirectory("other").FullName },
            "publisher" => live with { Publisher = "CN=OtherPublisher" },
            "family" => live with { PackageFamilyName = "other_family" },
            "non-development" => live with { IsDevelopmentMode = false },
            "full-name" => live with { FullName = live.FullName.Replace("_1.0.0.0_", "_2.0.0.0_", StringComparison.Ordinal) },
            _ => throw new InvalidOperationException(),
        };

        var exitCode = await InvokeAsync(owner.FullName, "--force", "--json");

        Assert.AreEqual(1, exitCode, TestAnsiConsole.Output);
        Assert.IsNotNull(JsonError());
        Assert.IsEmpty(_packages.UnregisterByFullNameCalls);
        Assert.IsNotNull(DevelopmentRegistrationStore.Read(new DirectoryInfo(registration.Identity.LayoutPath)));
    }

    [TestMethod]
    public async Task CorruptSidecarFailsBeforeAnyLegacyLookup()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.cs"), "");
        var registration = CreateRegistration(owner, "layout");
        File.WriteAllText(DevelopmentRegistrationStore.ReceiptPath(new DirectoryInfo(registration.Identity.LayoutPath)), "{invalid");

        var exitCode = await InvokeAsync("--output-appx-directory", registration.Identity.LayoutPath, "--force", "--json");

        Assert.AreEqual(1, exitCode);
        Assert.IsNotNull(JsonError());
        Assert.IsEmpty(_packages.FindDevPackagesCalls);
        Assert.IsEmpty(_packages.UnregisterByFullNameCalls);
        AssertNoBuildOrIdentityEvaluation();
    }

    [TestMethod]
    public async Task ManifestAndExplicitLayoutMustIdentifyTheSamePackage()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.cs"), "");
        var registration = CreateRegistration(owner, "layout");
        var manifest = WriteFile(Path.Join(_tempDirectory.FullName, "wrong.appxmanifest"),
            ManifestXml.Replace("TestPackage", "OtherPackage", StringComparison.Ordinal));

        var exitCode = await InvokeAsync("--manifest", manifest.FullName, "--output-appx-directory", registration.Identity.LayoutPath, "--force", "--json");

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(JsonError(), "does not match");
        Assert.IsEmpty(_packages.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task SourceManifestSelectsItsUniqueIdentityWithoutSweepingOriginalOrDebugNames()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.csproj"), "<Project />");
        var registration = CreateRegistration(owner, "layout");
        var manifest = WriteFile(Path.Join(_tempDirectory.FullName, "Package.appxmanifest"), ManifestXml);
        _packages.FakeDevPackages.Add(new DevPackageInfo("TestPackage.debug_1.0.0.0_x64__other",
            "TestPackage.debug", "1.0.0.0", _tempDirectory.FullName, true));

        var exitCode = await InvokeAsync("--manifest", manifest.FullName, "--json");

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.AreEqual(registration.Identity.PackageFullName, _packages.UnregisterByFullNameCalls.Single().PackageFullName);
        Assert.IsTrue(_packages.FindDevPackagesCalls.All(name => name == registration.Identity.EffectivePackageName));
    }

    [TestMethod]
    public async Task LegacyForceCannotRemoveAnotherAppsManagedNormalIdentity()
    {
        var ownDirectory = _tempDirectory.CreateSubdirectory("owner");
        var owner = WriteFile(Path.Join(ownDirectory.FullName, "App.csproj"), "<Project />");
        var registration = CreateRegistration(owner, "layout", unique: false);
        var manifest = WriteFile(Path.Join(_tempDirectory.FullName, "Package.appxmanifest"), ManifestXml);

        var exitCode = await InvokeAsync("--manifest", manifest.FullName, "--force", "--json");

        Assert.AreEqual(1, exitCode, TestAnsiConsole.Output);
        StringAssert.Contains(JsonError(), "managed");
        Assert.IsEmpty(_packages.UnregisterByFullNameCalls);
        Assert.IsNotNull(DevelopmentRegistrationStore.Read(new DirectoryInfo(registration.Identity.LayoutPath)));
    }

    [TestMethod]
    public async Task PruneForceDoesNotBypassManagedOwnership()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.cs"), "");
        var registration = CreateRegistration(owner, "layout");
        _packages.FakeOrphanedDevPackages = [.. _packages.FakeDevPackages];

        var exitCode = await InvokeAsync("--prune", "--force", "--json");

        Assert.AreEqual(1, exitCode);
        Assert.IsEmpty(_packages.UnregisterByFullNameCalls);
        Assert.IsNotNull(DevelopmentRegistrationStore.Read(new DirectoryInfo(registration.Identity.LayoutPath)));
    }

    [TestMethod]
    [DataRow("implicit")]
    [DataRow("directory")]
    [DataRow("project")]
    [DataRow("solution")]
    public async Task ReceiptlessProjectUsesGuardedManifestCleanupWithoutBuilding(string inputKind)
    {
        var project = WriteFile(Path.Join(_tempDirectory.FullName, "App.csproj"), "<Project />");
        var solution = WriteFile(Path.Join(_tempDirectory.FullName, "App.slnx"), "<Solution />");
        WriteFile(Path.Join(_tempDirectory.FullName, "Package.appxmanifest"), ManifestXml);
        _projects.InputResolutionOverride = new RunInputResolution(
            WinAppRunMode.Project, project, _tempDirectory,
            Solution: inputKind == "solution" ? solution : null);
        var layout = _tempDirectory.CreateSubdirectory("bin\\AppX");
        var other = Directory.CreateDirectory(_tempDirectory.FullName + "-unrelated");
        const string ownedFullName = "TestPackage_1.0.0.0_x64__legacy";
        try
        {
            _packages.FakeDevPackages =
            [
                new DevPackageInfo(ownedFullName, "TestPackage", "1.0.0.0", layout.FullName, true, "CN=TestPublisher"),
                new DevPackageInfo("TestPackage_2.0.0.0_x64__other", "TestPackage", "2.0.0.0", other.FullName, true, "CN=TestPublisher"),
                new DevPackageInfo("TestPackage_3.0.0.0_x64__store", "TestPackage", "3.0.0.0", layout.FullName, false, "CN=TestPublisher"),
            ];
            string[] arguments = inputKind switch
            {
                "directory" => [_tempDirectory.FullName, "--json"],
                "project" => [project.FullName, "--json"],
                "solution" => [solution.FullName, "--json"],
                _ => ["--json"],
            };

            var exitCode = await InvokeAsync(arguments);

            Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
            Assert.AreEqual(ownedFullName, _packages.UnregisterByFullNameCalls.Single().PackageFullName);
            Assert.IsEmpty(_packages.UnregisterCalls, "Legacy fallback must still remove only the package it vetted.");
            AssertNoBuildOrIdentityEvaluation();
            Assert.IsNull(DevelopmentRegistrationStore.Read(layout));
        }
        finally
        {
            other.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task ProjectWithoutARecordedDeploymentReportsAbsenceWithoutBuilding()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.csproj"), "<Project />");

        var exitCode = await InvokeAsync(owner.FullName, "--json");

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        AssertNoBuildOrIdentityEvaluation();
        Assert.IsEmpty(_packages.FindDevPackagesCalls);
        Assert.IsEmpty(_packages.UnregisterByFullNameCalls);
        Assert.IsFalse(JsonDocument.Parse(TestAnsiConsole.Output).RootElement.TryGetProperty("Error", out _));
    }

    [TestMethod]
    public async Task ExplicitLayoutWithoutMetadataReportsAbsenceWithoutGuessingAnIdentity()
    {
        var layout = _tempDirectory.CreateSubdirectory("unowned");
        WriteFile(Path.Join(layout.FullName, "appxmanifest.xml"), ManifestXml);
        _packages.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__other", "TestPackage", "1.0.0.0", layout.FullName, true),
        ];

        var exitCode = await InvokeAsync("--output-appx-directory", layout.FullName, "--force", "--json");

        Assert.AreEqual(0, exitCode, TestAnsiConsole.Output);
        Assert.IsEmpty(_packages.FindDevPackagesCalls);
        Assert.IsEmpty(_packages.UnregisterByFullNameCalls);
        AssertNoBuildOrIdentityEvaluation();
    }

    [TestMethod]
    public async Task AmbiguousProjectSelectionDoesNotBuildOrRemoveAnything()
    {
        var solution = WriteFile(Path.Join(_tempDirectory.FullName, "Apps.slnx"), "<Solution />");
        _projects.ResolveInputThrows = new ProjectRunException("Multiple runnable projects. Specify a .csproj.");

        var exitCode = await InvokeAsync(solution.FullName, "--json");

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(JsonError(), ".csproj");
        AssertNoBuildOrIdentityEvaluation();
        Assert.IsEmpty(_packages.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public void TargetManifestSelectionUsesOriginalIdentityAndHostOwner()
    {
        var ownerDirectory = _tempDirectory.CreateSubdirectory("owner");
        var otherDirectory = _tempDirectory.CreateSubdirectory("other");
        var owner = WriteFile(Path.Join(ownerDirectory.FullName, "App.cs"), "");
        var other = WriteFile(Path.Join(otherDirectory.FullName, "App.cs"), "");
        var first = TargetState(CreateRegistration(owner, "first"), "first");
        var second = TargetState(CreateRegistration(other, "second"), "second");
        var manifest = WriteFile(Path.Join(ownerDirectory.FullName, "Package.appxmanifest"), ManifestXml);

        var matches = UnregisterCommand.Handler.SelectTargetDeployments(
            [first, second], MsixService.ParseAppxManifestAsync(ManifestXml), manifest, hostLayout: null);

        Assert.HasCount(1, matches);
        Assert.AreEqual("first", matches[0].DeploymentId);
    }

    [TestMethod]
    public void TargetExplicitLayoutSelectionDoesNotNeedAnOwnerOrManifest()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.cs"), "");
        var registration = CreateRegistration(owner, "layout");
        var state = TargetState(registration, "selected");
        owner.Delete();

        var matches = UnregisterCommand.Handler.SelectTargetDeployments(
            [state], identity: null, manifest: null, registration.Identity.LayoutPath);

        Assert.HasCount(1, matches);
        Assert.AreEqual("selected", matches[0].DeploymentId);
        Assert.AreEqual(state.Revision, matches[0].Revision);
    }

    [TestMethod]
    public void TargetSingleFileOwnerSelectsOnlyItsRecordedDeployment()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.cs"), "");
        var other = WriteFile(Path.Join(_tempDirectory.FullName, "Other.cs"), "");
        var selected = TargetState(CreateRegistration(owner, "selected"), "selected");
        var unrelated = TargetState(CreateRegistration(other, "unrelated"), "unrelated");

        var matches = UnregisterCommand.Handler.SelectTargetDeployments(
            [selected, unrelated], identity: null, manifest: null, hostLayout: null,
            DevelopmentIdentityHelper.CanonicalizePath(owner.FullName));

        Assert.HasCount(1, matches);
        Assert.AreEqual("selected", matches[0].DeploymentId);
        AssertNoBuildOrIdentityEvaluation();
    }

    [TestMethod]
    public void TargetLayoutAndOwnerMustBothMatch()
    {
        var owner = WriteFile(Path.Join(_tempDirectory.FullName, "App.cs"), "");
        var other = WriteFile(Path.Join(_tempDirectory.FullName, "Other.cs"), "");
        var selected = TargetState(CreateRegistration(owner, "selected"), "selected");
        var unrelated = TargetState(CreateRegistration(other, "unrelated"), "unrelated");

        var matches = UnregisterCommand.Handler.SelectTargetDeployments(
            [selected, unrelated], identity: null, manifest: null, unrelated.Package!.HostLayoutPath,
            DevelopmentIdentityHelper.CanonicalizePath(owner.FullName));

        Assert.IsEmpty(matches);
    }

    private static DeploymentState TargetState(DevelopmentRegistration registration, string deploymentId) => new()
    {
        SchemaVersion = 1,
        Revision = 7,
        DeploymentId = deploymentId,
        TargetEpoch = "epoch",
        Dirty = false,
        Package = new PackageOwnership
        {
            PackageName = registration.Identity.EffectivePackageName,
            Publisher = registration.Identity.Publisher,
            PackageFullName = registration.Identity.PackageFullName,
            PackageFamilyName = registration.Identity.PackageFamilyName,
            RegisteredLocation = @"C:\guest\layout",
            HostLayoutPath = registration.Identity.LayoutPath,
            Identity = registration.Identity with { LayoutPath = @"C:\guest\layout", Revision = 7 },
        },
    };

    private DevelopmentRegistration CreateRegistration(FileSystemInfo owner, string layoutName, bool unique = true)
    {
        var layout = _tempDirectory.CreateSubdirectory(layoutName);
        var document = AppxManifestDocument.Parse(ManifestXml);
        var canonicalOwner = DevelopmentIdentityHelper.CanonicalizePath(owner.FullName);
        var effectiveName = unique
            ? DevelopmentIdentityHelper.DeriveName(canonicalOwner, "TestPackage", "CN=TestPublisher")
            : "TestPackage";
        document.IdentityName = effectiveName;
        document.Save(Path.Join(layout.FullName, "appxmanifest.xml"));
        var identity = new DevelopmentIdentity
        {
            Mode = unique ? "Unique" : "Original",
            OriginalPackageName = "TestPackage",
            EffectivePackageName = effectiveName,
            Publisher = "CN=TestPublisher",
            Version = "1.0.0.0",
            Architecture = "x64",
            ResourceId = "",
            ApplicationId = "App",
            PackageFamilyName = DevelopmentIdentityHelper.ComputeFamilyName(effectiveName, "CN=TestPublisher"),
            PackageFullName = DevelopmentIdentityHelper.ComputeFullName(document),
            OwnerPath = canonicalOwner,
            LayoutPath = DevelopmentIdentityHelper.CanonicalizePath(layout.FullName),
            Revision = 1,
        };
        var registration = new DevelopmentRegistration
        {
            Identity = identity,
            ManifestHash = DevelopmentRegistrationStore.HashManifest(layout),
        };
        DevelopmentRegistrationStore.Commit(_testCacheDirectory, layout, registration);
        _packages.FakeDevPackages.Add(new DevPackageInfo(identity.PackageFullName!, effectiveName, identity.Version,
            layout.FullName, true, identity.Publisher, identity.PackageFamilyName));
        return registration;
    }

    private static FileInfo WriteFile(string path, string contents)
    {
        File.WriteAllText(path, contents);
        return new FileInfo(path);
    }

    private Task<int> InvokeAsync(params string[] args) =>
        ParseAndInvokeWithCaptureAsync(GetRequiredService<UnregisterCommand>(), args);

    private string JsonError() => JsonDocument.Parse(TestAnsiConsole.Output).RootElement.GetProperty("Error").GetString()!;

    private void AssertNoBuildOrIdentityEvaluation()
    {
        Assert.IsEmpty(_projects.BuildAndResolveCalls);
        Assert.IsEmpty(_projects.BuildAndResolveSingleFileCalls);
        Assert.IsEmpty(_projects.ResolveSingleFileIdentityCalls);
    }
}
