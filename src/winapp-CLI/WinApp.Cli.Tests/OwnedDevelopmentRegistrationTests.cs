// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class OwnedDevelopmentRegistrationTests : BaseCommandTests
{
    private readonly FakePackageRegistrationService _registration = new();
    private readonly FakePriService _pri = new();
    private readonly FakeWindowsAppRuntimeService _runtime = new();
    private DirectoryInfo _input = null!;
    private DirectoryInfo _layout = null!;
    private FileInfo _manifest = null!;
    private IMsixService _service = null!;
    private DirectoryInfo StateRoot => GetRequiredService<IWinappDirectoryService>().GetGlobalWinappDirectory();

    protected override IServiceCollection ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IPackageRegistrationService>(_registration)
            .AddSingleton<IPriService>(_pri)
            .AddSingleton<IWindowsAppRuntimeService>(_runtime)
            .AddSingleton<IDevModeService, FakeDevModeService>()
            .AddSingleton<IDotNetService, FakeDotNetService>()
            .AddSingleton<INugetService, FakeNugetService>();

    [TestInitialize]
    public void Setup()
    {
        _service = GetRequiredService<IMsixService>();
        _input = _tempDirectory.CreateSubdirectory("input");
        _layout = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "AppX"));
        _manifest = new FileInfo(Path.Combine(_input.FullName, "appxmanifest.xml"));
        File.WriteAllText(_manifest.FullName, """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:build="http://schemas.microsoft.com/developer/appx/2015/build"
                     IgnorableNamespaces="build">
              <Identity Name="Owned.App" Publisher="CN=Owned" Version="1.0.0.0" ProcessorArchitecture="x64" />
              <Properties><DisplayName>Owned</DisplayName><PublisherDisplayName>Owned</PublisherDisplayName><Logo>logo.png</Logo></Properties>
              <Resources><Resource Language="en-US" /></Resources>
              <Applications><Application Id="App" Executable="Owned.exe" EntryPoint="Windows.FullTrustApplication" /></Applications>
              <build:Metadata><build:Item Name="makepri.exe" Version="1.0" /></build:Metadata>
            </Package>
            """);
        File.WriteAllText(Path.Combine(_input.FullName, "Owned.exe"), "executable");
        File.WriteAllText(Path.Combine(_input.FullName, "resources.pri"), "original-resource-map");
        File.WriteAllText(Path.Combine(_input.FullName, "logo.png"), "image");
    }

    private Task<MsixIdentityResult> Run(bool unique = false, bool clean = false, DirectoryInfo? layout = null,
        LayoutReconciliation reconciliation = LayoutReconciliation.Exact, bool ensureAlias = false, CancellationToken cancellationToken = default) =>
        _service.AddLooseLayoutIdentityAsync(_manifest, _input, layout ?? _layout, TestTaskContext,
            reconciliation, clean, selfContained: true, ensureExecutionAlias: ensureAlias,
            developmentIdentity: new DevelopmentIdentityOptions(_input.FullName, unique), cancellationToken: cancellationToken);

    private void MakeRawManifestWithoutPri()
    {
        var document = AppxManifestDocument.Load(_manifest.FullName);
        document.Document.Root!.Element(AppxManifestDocument.BuildNs + "Metadata")!.Remove();
        document.Save(_manifest.FullName);
        File.Delete(Path.Combine(_input.FullName, "resources.pri"));
    }

    private async Task CreateJunctionAsync(string link, DirectoryInfo target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/d", "/c", "mklink", "/J", link, target.FullName },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.IsNotNull(process);
        await process.WaitForExitAsync(TestContext.CancellationToken);
        if (process.ExitCode != 0)
        {
            Assert.Inconclusive($"Could not create a junction: {await process.StandardError.ReadToEndAsync(TestContext.CancellationToken)}");
        }
    }

    [TestMethod]
    public async Task FirstRun_ObservesExactIdentityAndStoresAdjacentReceipt()
    {
        var result = await Run();
        var receipt = DevelopmentRegistrationStore.Read(_layout);
        Assert.IsNotNull(receipt);
        Assert.AreEqual(result.Identity!.PackageFullName, receipt.Identity.PackageFullName);
        Assert.AreEqual(_registration.FakeDevPackages.Single().FullName, result.Identity!.PackageFullName);
        Assert.AreEqual(1L, result.Identity.Revision);
        Assert.IsFalse(File.Exists(DevelopmentRegistrationStore.PendingPath(_layout)));
        Assert.AreEqual(1, DevelopmentRegistrationStore.FindByOwner(StateRoot, _input.FullName).Count);
        Assert.IsFalse(_layout.EnumerateFiles().Any(file => file.Name.Contains("winapp-registration", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SecondLayoutCollision_DoesNotMutateEitherLayout()
    {
        await Run();
        var first = File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml"));
        var other = _tempDirectory.CreateSubdirectory("other");
        File.WriteAllText(Path.Combine(other.FullName, "sentinel"), "untouched");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(layout: other));
        StringAssert.Contains(failure.Message, "--unique-identity");
        CollectionAssert.AreEqual(first, File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml")));
        Assert.AreEqual("sentinel", other.EnumerateFiles().Single().Name);
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task DifferentOwnersUniqueMode_RegisterSideBySide()
    {
        var first = await Run(unique: true);
        var secondOwner = _tempDirectory.CreateSubdirectory("second-owner");
        var secondLayout = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "second-layout"));
        var second = await _service.AddLooseLayoutIdentityAsync(_manifest, _input, secondLayout, TestTaskContext,
            selfContained: true, developmentIdentity: new DevelopmentIdentityOptions(secondOwner.FullName, true));
        Assert.AreNotEqual(first.Identity!.PackageFamilyName, second.Identity!.PackageFamilyName);
        Assert.HasCount(2, _registration.FakeDevPackages);
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task UnrelatedFamilyAtSameLayout_IsNeverMutatedOrRemoved()
    {
        _layout.Create();
        File.WriteAllText(Path.Combine(_layout.FullName, "sentinel"), "unchanged");
        _registration.FakeDevPackages.Add(new DevPackageInfo("Other_1.0.0.0_x64__fake", "Other", "1.0.0.0", _layout.FullName, true, "CN=Other", "Other_fake"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run());
        Assert.AreEqual("sentinel", _layout.EnumerateFiles().Single().Name);
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task Skip_IncrementsRevisionAndSyncsPayloadWithoutRegistration()
    {
        await Run();
        var receipt = DevelopmentRegistrationStore.Read(_layout)!;
        File.WriteAllText(Path.Combine(_input.FullName, "Owned.exe"), "updated-executable");
        var result = await Run();
        Assert.AreEqual("updated-executable", File.ReadAllText(Path.Combine(_layout.FullName, "Owned.exe")));
        Assert.AreEqual(2L, result.Identity!.Revision);
        Assert.HasCount(1, _registration.RegisterLooseLayoutCalls);
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevelopmentRegistrationStore.RemoveOwnedAsync(_registration, StateRoot, receipt, true));
        StringAssert.Contains(failure.Message, "superseded");
        Assert.HasCount(1, _registration.FakeDevPackages);
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task Clean_RemovesOnlyExactOwnedPackageWithoutPreservingData()
    {
        var first = await Run();
        _registration.FakeDevPackages.Add(new DevPackageInfo("Unrelated_1.0.0.0_x64__fake", "Unrelated", "1.0.0.0", _input.FullName, false));
        await Run(clean: true);
        Assert.HasCount(1, _registration.UnregisterByFullNameCalls);
        Assert.AreEqual((first.Identity!.PackageFullName!, false), _registration.UnregisterByFullNameCalls[0]);
        Assert.IsTrue(_registration.FakeDevPackages.Any(package => package.Name == "Unrelated"));
    }

    [TestMethod]
    public async Task OriginalUniqueSwitch_RemovesPriorBeforeChangingLiveManifest()
    {
        var first = await Run();
        var oldBytes = File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml"));
        _registration.OnUnregisterByFullName = (_, _) =>
            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml")));
        var unique = await Run(unique: true);
        Assert.AreNotEqual(first.PackageName, unique.PackageName);
        Assert.AreEqual(first.Identity!.PackageFullName, _registration.UnregisterByFullNameCalls.Single().PackageFullName);
        Assert.HasCount(1, _pri.ReindexIdentityCalls);
        _registration.OnUnregisterByFullName = null;
        var original = await Run();
        Assert.AreEqual(first.PackageName, original.PackageName);
        Assert.HasCount(1, _registration.FakeDevPackages);
    }

    [TestMethod]
    public async Task ChangedVersion_RemovesOnlyPriorOwnedVersion()
    {
        var first = await Run();
        File.WriteAllText(_manifest.FullName, File.ReadAllText(_manifest.FullName).Replace("1.0.0.0", "2.0.0.0", StringComparison.Ordinal));
        var second = await Run();
        Assert.AreNotEqual(first.Identity!.PackageFullName, second.Identity!.PackageFullName);
        Assert.AreEqual(first.Identity.PackageFullName, _registration.UnregisterByFullNameCalls.Single().PackageFullName);
    }

    [TestMethod]
    public async Task CleanDuringModeSwitch_PreservesTheOriginalIdentityData()
    {
        var original = await Run();
        var unique = await Run(unique: true, clean: true);
        Assert.AreEqual((original.Identity!.PackageFullName!, true), _registration.UnregisterByFullNameCalls[0]);
        Assert.AreEqual((unique.Identity!.PackageFullName!, false), _registration.UnregisterByFullNameCalls[1]);
    }

    [TestMethod]
    [DataRow("publisher")]
    [DataRow("location")]
    [DataRow("signed")]
    [DataRow("family")]
    public async Task LiveMetadataMismatch_RefusesRemovalAndMutation(string mismatch)
    {
        await Run();
        var before = File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml"));
        var live = _registration.FakeDevPackages.Single();
        _registration.FakeDevPackages[0] = mismatch switch
        {
            "publisher" => live with { Publisher = "cn=Owned" },
            "location" => live with { InstallLocation = _input.FullName },
            "signed" => live with { IsDevelopmentMode = false },
            _ => live with { PackageFamilyName = "different_family" },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(clean: true));
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml")));
    }

    [TestMethod]
    public async Task CorruptReceipt_RefusesMutation()
    {
        await Run();
        File.WriteAllText(DevelopmentRegistrationStore.ReceiptPath(_layout), "{invalid");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(clean: true));
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
        Assert.HasCount(1, _registration.RegisterLooseLayoutCalls);
    }

    [TestMethod]
    public async Task ChangedOnDiskManifest_DoesNotPermitCleanRemoval()
    {
        await Run();
        File.AppendAllText(Path.Combine(_layout.FullName, "appxmanifest.xml"), "<!-- external edit -->");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(clean: true));
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task SameLayoutDifferentOwner_RefusesAdoption()
    {
        await Run();
        var otherOwner = _tempDirectory.CreateSubdirectory("other-owner");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.AddLooseLayoutIdentityAsync(_manifest, _input, _layout, TestTaskContext,
                selfContained: true, developmentIdentity: new DevelopmentIdentityOptions(otherOwner.FullName, true)));
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task UnmanagedExactDevelopmentRegistration_CanBeProvedAndAdopted()
    {
        _layout.Create();
        File.Copy(_manifest.FullName, Path.Combine(_layout.FullName, "appxmanifest.xml"));
        var document = AppxManifestDocument.Load(_manifest.FullName);
        _registration.FakeDevPackages.Add(new DevPackageInfo(
            DevelopmentIdentityHelper.ComputeFullName(document), document.IdentityName!, document.IdentityVersion!,
            _layout.FullName, true, document.IdentityPublisher!,
            DevelopmentIdentityHelper.ComputeFamilyName(document.IdentityName!, document.IdentityPublisher!)));
        var result = await Run();
        Assert.AreEqual(_registration.FakeDevPackages.Single().FullName, result.Identity!.PackageFullName);
        Assert.IsNotNull(DevelopmentRegistrationStore.Read(_layout));
    }

    [TestMethod]
    public async Task FailedRegistration_RetainsJournalAndRecoversNextRun()
    {
        _registration.RegisterLooseLayoutThrows = new InvalidOperationException("registration failed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run());
        Assert.IsNull(DevelopmentRegistrationStore.Read(_layout));
        Assert.IsNotNull(DevelopmentRegistrationStore.ReadPending(_layout));
        _registration.RegisterLooseLayoutThrows = null;
        var result = await Run();
        Assert.AreEqual(2L, result.Identity!.Revision);
        Assert.IsNull(DevelopmentRegistrationStore.ReadPending(_layout));
    }

    [TestMethod]
    public async Task RegistrationCancellation_PropagatesAndRetainsRecoveryJournal()
    {
        _registration.RegisterLooseLayoutThrows = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Run());
        Assert.IsNotNull(DevelopmentRegistrationStore.ReadPending(_layout));
        Assert.IsNull(DevelopmentRegistrationStore.Read(_layout));
    }

    [TestMethod]
    public async Task MissingObservedRegistration_DoesNotFabricateCommittedSuccess()
    {
        _registration.ObserveLooseRegistrations = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run());
        Assert.IsNull(DevelopmentRegistrationStore.Read(_layout));
        Assert.IsNotNull(DevelopmentRegistrationStore.ReadPending(_layout));
    }

    [TestMethod]
    public async Task SidecarCommitFailure_RetainsJournalAndRecoversObservedPackage()
    {
        await Run();
        using (var lockedReceipt = new FileStream(DevelopmentRegistrationStore.ReceiptPath(_layout), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Assert.ThrowsAsync<Exception>(() => Run());
            Assert.IsTrue(failure is IOException or UnauthorizedAccessException,
                $"Expected a locked-file I/O failure, received {failure.GetType().Name}.");
        }
        Assert.IsNotNull(DevelopmentRegistrationStore.ReadPending(_layout));
        Assert.HasCount(1, _registration.FakeDevPackages);
        var result = await Run();
        Assert.HasCount(1, _registration.RegisterLooseLayoutCalls);
        Assert.AreEqual(3L, result.Identity!.Revision);
        Assert.IsNull(DevelopmentRegistrationStore.ReadPending(_layout));
    }

    [TestMethod]
    public async Task FailedPayloadCopy_RetainsPendingJournalAndDoesNotClaimSuccess()
    {
        await Run();
        var receipt = DevelopmentRegistrationStore.Read(_layout)!;
        File.WriteAllText(Path.Combine(_input.FullName, "Owned.exe"), "new payload that must be copied");
        using (var lockedPayload = new FileStream(Path.Combine(_layout.FullName, "Owned.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Assert.ThrowsAsync<Exception>(() => Run());
            Assert.IsTrue(failure is IOException or UnauthorizedAccessException,
                $"Expected a locked-file I/O failure, received {failure.GetType().Name}.");
        }
        Assert.AreEqual(receipt.Revision, DevelopmentRegistrationStore.Read(_layout)!.Revision);
        Assert.IsNotNull(DevelopmentRegistrationStore.ReadPending(_layout));
        Assert.IsTrue(DevelopmentRegistrationStore.FindAll(StateRoot).Single().IsPending);
        await Run();
        Assert.AreEqual("new payload that must be copied", File.ReadAllText(Path.Combine(_layout.FullName, "Owned.exe")));
        Assert.IsNull(DevelopmentRegistrationStore.ReadPending(_layout));
    }

    [TestMethod]
    public async Task PendingJournal_BlocksCleanupUntilRecovered()
    {
        await Run();
        var receipt = DevelopmentRegistrationStore.Read(_layout)!;
        DevelopmentRegistrationStore.Begin(StateRoot, _layout, new PendingDevelopmentRegistration
        {
            Prior = receipt,
            Candidate = receipt with { Identity = receipt.Identity with { Revision = receipt.Revision + 1 } },
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevelopmentRegistrationStore.RemoveOwnedAsync(_registration, StateRoot, receipt, true));
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
        Assert.IsNotNull(DevelopmentRegistrationStore.ReadPending(_layout));
    }

    [TestMethod]
    public async Task Cleanup_VerifiesAbsenceBeforeClearingReceipt()
    {
        await Run();
        var receipt = DevelopmentRegistrationStore.Read(_layout)!;
        _registration.FakeUnregisterByFullNameResult = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevelopmentRegistrationStore.RemoveOwnedAsync(_registration, StateRoot, receipt, true));
        Assert.IsNotNull(DevelopmentRegistrationStore.Read(_layout));
        _registration.FakeUnregisterByFullNameResult = true;
        Assert.IsTrue(await DevelopmentRegistrationStore.RemoveOwnedAsync(_registration, StateRoot, receipt, true));
        Assert.IsNull(DevelopmentRegistrationStore.Read(_layout));
        Assert.IsEmpty(_registration.FakeDevPackages);
    }

    [TestMethod]
    public async Task Cleanup_AlreadyAbsentClearsStaleReceiptAndReturnsFalse()
    {
        await Run();
        var receipt = DevelopmentRegistrationStore.Read(_layout)!;
        _registration.FakeDevPackages.Clear();

        Assert.IsFalse(await DevelopmentRegistrationStore.RemoveOwnedAsync(
            _registration, StateRoot, receipt, preserveAppData: true, ct: TestContext.CancellationToken));
        Assert.IsNull(DevelopmentRegistrationStore.Read(_layout));
        Assert.IsEmpty(DevelopmentRegistrationStore.FindAll(StateRoot));
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task Cleanup_RepeatedAfterConfirmedRemovalReturnsFalse()
    {
        await Run();
        var receipt = DevelopmentRegistrationStore.Read(_layout)!;
        Assert.IsTrue(await DevelopmentRegistrationStore.RemoveOwnedAsync(_registration, StateRoot, receipt, true));
        Assert.IsFalse(await DevelopmentRegistrationStore.RemoveOwnedAsync(_registration, StateRoot, receipt, true));
        Assert.HasCount(1, _registration.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task RegistrationAndOwnerDiscovery_UseTheSameGlobalStateRoot()
    {
        await Run();
        var localState = GetRequiredService<IWinappDirectoryService>().GetLocalWinappDirectory();
        Assert.AreNotEqual(StateRoot.FullName, localState.FullName);
        Assert.HasCount(1, DevelopmentRegistrationStore.FindByOwner(StateRoot, _input.FullName));
        Assert.IsEmpty(DevelopmentRegistrationStore.FindAll(localState));
    }

    [TestMethod]
    public async Task Cleanup_MissingIndexDirectoryDoesNotPreventExactRemoval()
    {
        await Run();
        var receipt = DevelopmentRegistrationStore.Read(_layout)!;
        var indexDirectory = Path.Combine(StateRoot.FullName, "development-registrations");
        File.Delete(Directory.EnumerateFiles(indexDirectory, "*.path").Single());
        Directory.Delete(indexDirectory);

        Assert.IsTrue(await DevelopmentRegistrationStore.RemoveOwnedAsync(_registration, StateRoot, receipt, true));
        Assert.IsNull(DevelopmentRegistrationStore.Read(_layout));
        Assert.IsEmpty(_registration.FakeDevPackages);
        Assert.IsFalse(Directory.Exists(indexDirectory));
    }

    [TestMethod]
    public async Task Cleanup_ExplicitReceiptWorksWithAnAbsentDiscoveryRoot()
    {
        await Run();
        var receipt = DevelopmentRegistrationStore.Read(_layout)!;
        var otherStateRoot = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "absent-global-state"));

        Assert.IsTrue(await DevelopmentRegistrationStore.RemoveOwnedAsync(_registration, otherStateRoot, receipt, true));
        Assert.IsNull(DevelopmentRegistrationStore.Read(_layout));
        Assert.IsEmpty(_registration.FakeDevPackages);
        Assert.IsFalse(Directory.Exists(otherStateRoot.FullName));
        Assert.IsEmpty(DevelopmentRegistrationStore.FindAll(StateRoot));
    }

    [TestMethod]
    public async Task StoreRemove_NonDirectoryIndexAncestorIsNotTreatedAsMissing()
    {
        await Run();
        var invalidRoot = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "state-root-is-a-file"));
        File.WriteAllText(invalidRoot.FullName, "not a directory");

        var failure = Assert.Throws<InvalidOperationException>(() => DevelopmentRegistrationStore.Remove(invalidRoot, _layout));
        StringAssert.Contains(failure.Message, "not a directory");
        Assert.IsNotNull(DevelopmentRegistrationStore.Read(_layout));
        Assert.AreEqual("not a directory", File.ReadAllText(invalidRoot.FullName));
    }

    [TestMethod]
    public async Task StoreRemove_LockedIndexIsNotSwallowedAndReceiptIsRetained()
    {
        await Run();
        var hint = Directory.EnumerateFiles(Path.Combine(StateRoot.FullName, "development-registrations"), "*.path").Single();
        using var lockedHint = new FileStream(hint, FileMode.Open, FileAccess.Read, FileShare.Read);

        var failure = Assert.Throws<Exception>(() => DevelopmentRegistrationStore.Remove(StateRoot, _layout));
        Assert.IsTrue(failure is IOException or UnauthorizedAccessException);
        Assert.IsNotNull(DevelopmentRegistrationStore.Read(_layout));
        Assert.IsTrue(File.Exists(hint));
    }

    [TestMethod]
    public async Task Cleanup_MissingReceiptWithLivePackageThrowsWithoutRemoving()
    {
        await Run();
        var receipt = DevelopmentRegistrationStore.Read(_layout)!;
        File.Delete(DevelopmentRegistrationStore.ReceiptPath(_layout));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevelopmentRegistrationStore.RemoveOwnedAsync(_registration, StateRoot, receipt, true));
        StringAssert.Contains(failure.Message, "missing");
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
        Assert.HasCount(1, _registration.FakeDevPackages);
    }

    [TestMethod]
    [DataRow("publisher")]
    [DataRow("location")]
    [DataRow("signed")]
    [DataRow("family")]
    public async Task Cleanup_LiveMetadataDisagreementThrowsWithoutRemoving(string mismatch)
    {
        await Run();
        var receipt = DevelopmentRegistrationStore.Read(_layout)!;
        var live = _registration.FakeDevPackages.Single();
        _registration.FakeDevPackages[0] = mismatch switch
        {
            "publisher" => live with { Publisher = "cn=Owned" },
            "location" => live with { InstallLocation = _input.FullName },
            "signed" => live with { IsDevelopmentMode = false },
            _ => live with { PackageFamilyName = "different_family" },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevelopmentRegistrationStore.RemoveOwnedAsync(_registration, StateRoot, receipt, true));
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
        Assert.IsNotNull(DevelopmentRegistrationStore.Read(_layout));
    }

    [TestMethod]
    public async Task Cleanup_UnexpectedLayoutOccupantThrowsBeforeRemovingOwnedPackage()
    {
        await Run();
        var receipt = DevelopmentRegistrationStore.Read(_layout)!;
        _registration.FakeDevPackages.Add(new DevPackageInfo(
            "Other_1.0.0.0_x64__fake", "Other", "1.0.0.0", _layout.FullName, false, "CN=Other", "Other_fake"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevelopmentRegistrationStore.RemoveOwnedAsync(_registration, StateRoot, receipt, true));
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
        Assert.HasCount(2, _registration.FakeDevPackages);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FindAll_CorruptIndexHintFailsClosed(bool absoluteButMismatched)
    {
        await Run();
        var hint = Directory.EnumerateFiles(Path.Combine(StateRoot.FullName, "development-registrations"), "*.path").Single();
        File.WriteAllText(hint, absoluteButMismatched ? Path.Combine(_tempDirectory.FullName, "unrelated") : "relative-layout");

        Assert.Throws<InvalidOperationException>(() => DevelopmentRegistrationStore.FindAll(StateRoot));
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task FindAll_StaleIndexHintWithoutReceiptIsIgnored()
    {
        await Run();
        File.Delete(DevelopmentRegistrationStore.ReceiptPath(_layout));
        Assert.IsEmpty(DevelopmentRegistrationStore.FindAll(StateRoot));
    }

    [TestMethod]
    public async Task Materialize_TransformsWithoutHostProvisioningOrRegistration()
    {
        var result = await _service.MaterializeLooseLayoutAsync(_manifest, _input, _layout, TestTaskContext,
            LayoutReconciliation.Exact, selfContained: true,
            developmentIdentity: new DevelopmentIdentityOptions(_input.FullName, true));
        Assert.AreEqual("Unique", result.Identity!.Mode);
        Assert.IsNull(result.Identity.PackageFullName);
        Assert.AreEqual(0L, result.Identity.Revision);
        Assert.IsEmpty(_registration.RegisterLooseLayoutCalls);
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
        Assert.IsEmpty(_registration.InstallPackageCalls);
        Assert.IsNull(DevelopmentRegistrationStore.Read(_layout));
    }

    [TestMethod]
    public async Task Materialize_RefusesLiveHostLayoutWithoutMutation()
    {
        await Run();
        var before = File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.MaterializeLooseLayoutAsync(_manifest, _input, _layout, TestTaskContext,
                LayoutReconciliation.Exact, selfContained: true,
                developmentIdentity: new DevelopmentIdentityOptions(_input.FullName, true)));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml")));
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task PriFailure_PreservesPriorLayoutRegistrationAndSource()
    {
        await Run();
        var before = File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml"));
        var source = File.ReadAllBytes(_manifest.FullName);
        _pri.ReindexIdentityException = new InvalidOperationException("invalid resources");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unique: true));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml")));
        CollectionAssert.AreEqual(source, File.ReadAllBytes(_manifest.FullName));
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task CompiledResourceGraphWithoutPri_RejectsUniqueBeforePublishing()
    {
        File.Delete(Path.Combine(_input.FullName, "resources.pri"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unique: true));
        Assert.IsFalse(Directory.Exists(_layout.FullName));
        Assert.IsEmpty(_registration.RegisterLooseLayoutCalls);
        Assert.AreEqual(0, _pri.GeneratePriFileCallCount);
    }

    [TestMethod]
    public async Task UniqueGeneratedAlias_IsDerivedAfterNameAndReturnedAsMapping()
    {
        var service = (MsixService)_service;
        var inspectedAliases = new List<string>();
        service.ResolveAliasProxy = alias =>
        {
            inspectedAliases.Add(alias);
            return new FileInfo(Path.Combine(_tempDirectory.FullName, "absent-aliases", alias));
        };
        service.ReadAliasOwner = _ => throw new AssertFailedException("An absent alias has no owner to inspect.");

        var result = await Run(unique: true, ensureAlias: true);
        var identity = result.Identity!;
        var originalAlias = ExecutionAliasResolver.BuildDefaultAliasName(
            DevelopmentIdentityHelper.ComputeFamilyName(identity.OriginalPackageName, identity.Publisher))!;
        var effectiveAlias = ExecutionAliasResolver.BuildDefaultAliasName(identity.PackageFamilyName)!;
        Assert.AreEqual(effectiveAlias, identity.Aliases[originalAlias]);
        Assert.AreEqual(effectiveAlias, AppxManifestDocument.Load(Path.Combine(_layout.FullName, "appxmanifest.xml")).GetExecutionAliases().Single());
        Assert.AreNotEqual(originalAlias, effectiveAlias);
        Assert.IsTrue(inspectedAliases.Count > 0);
        Assert.IsTrue(inspectedAliases.All(alias => alias == effectiveAlias));
        Assert.AreEqual(effectiveAlias, DevelopmentRegistrationStore.Read(_layout)!.Identity.Aliases[originalAlias]);
    }

    [TestMethod]
    public async Task UniqueAuthoredAliases_AllEffectiveOwnersCheckedBeforePriorRemoval()
    {
        await Run();
        var before = File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml"));
        var document = AppxManifestDocument.Load(_manifest.FullName);
        document.EnsureExecutionAlias("first.exe");
        document.Document.Descendants(AppxManifestDocument.Uap5Ns + "ExecutionAlias").Single()
            .AddAfterSelf(new XElement(AppxManifestDocument.Uap5Ns + "ExecutionAlias", new XAttribute("Alias", "second.exe")));
        document.Save(_manifest.FullName);
        var expected = DevelopmentIdentityHelper.Create(document, _input.FullName, _layout.FullName, true);
        var proxies = _tempDirectory.CreateSubdirectory("alias-proxies");
        foreach (var alias in expected.Aliases.Values)
        {
            File.WriteAllText(Path.Combine(proxies.FullName, alias), "proxy");
        }
        var ownersRead = new List<string>();
        var service = (MsixService)_service;
        service.ResolveAliasProxy = alias => new FileInfo(Path.Combine(proxies.FullName, alias));
        service.ReadAliasOwner = path =>
        {
            var alias = Path.GetFileName(path);
            ownersRead.Add(alias);
            return alias == expected.Aliases["first.exe"] ? expected.PackageFamilyName : "Foreign_family";
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unique: true));
        StringAssert.Contains(failure.Message, expected.Aliases["second.exe"]);
        CollectionAssert.AreEquivalent(expected.Aliases.Values.ToArray(), ownersRead.ToArray());
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml")));
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
        Assert.HasCount(1, _registration.RegisterLooseLayoutCalls);
        Assert.AreEqual(1L, DevelopmentRegistrationStore.Read(_layout)!.Revision);
        Assert.IsNull(DevelopmentRegistrationStore.ReadPending(_layout));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public async Task UniqueAliasWithUnknownOwner_FailsClosed(string? owner)
    {
        var proxy = new FileInfo(Path.Combine(_tempDirectory.FullName, "occupied-alias.exe"));
        File.WriteAllText(proxy.FullName, "not an app-execution-link");
        var service = (MsixService)_service;
        service.ResolveAliasProxy = _ => proxy;
        service.ReadAliasOwner = _ => owner;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unique: true, ensureAlias: true));
        StringAssert.Contains(failure.Message, "cannot be verified");
        Assert.IsEmpty(_registration.RegisterLooseLayoutCalls);
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
        Assert.IsFalse(Directory.Exists(_layout.FullName));
    }

    [TestMethod]
    public async Task UniqueAliasOwnedByExpectedFamily_AllowsRegistrationAndSkip()
    {
        var proxy = new FileInfo(Path.Combine(_tempDirectory.FullName, "owned-alias.exe"));
        File.WriteAllText(proxy.FullName, "proxy");
        var expected = DevelopmentIdentityHelper.Create(
            AppxManifestDocument.Load(_manifest.FullName), _input.FullName, _layout.FullName, true);
        var service = (MsixService)_service;
        service.ResolveAliasProxy = _ => proxy;
        service.ReadAliasOwner = _ => expected.PackageFamilyName.ToUpperInvariant();

        await Run(unique: true, ensureAlias: true);
        var second = await Run(unique: true, ensureAlias: true);
        Assert.HasCount(1, _registration.RegisterLooseLayoutCalls);
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
        Assert.AreEqual(2L, second.Identity!.Revision);
        Assert.HasCount(1, second.Identity.Aliases);
    }

    [TestMethod]
    public async Task UniqueTargetMaterialization_ReturnsGeneratedAliasWithoutProbingHostAliases()
    {
        ((MsixService)_service).ResolveAliasProxy = _ => throw new AssertFailedException("A target must not inspect host execution aliases.");
        var result = await _service.MaterializeLooseLayoutAsync(_manifest, _input, _layout, TestTaskContext,
            LayoutReconciliation.Exact, selfContained: true, ensureExecutionAlias: true,
            developmentIdentity: new DevelopmentIdentityOptions(_input.FullName, true));
        Assert.AreEqual(ExecutionAliasResolver.BuildDefaultAliasName(result.Identity!.PackageFamilyName), result.Identity.Aliases.Values.Single());
        Assert.IsEmpty(_registration.RegisterLooseLayoutCalls);
    }

    [TestMethod]
    public async Task RawUniqueConsoleWithoutPri_StrictlyGeneratesImagesAfterIdentityAndAlias()
    {
        MakeRawManifestWithoutPri();
        var source = File.ReadAllBytes(_manifest.FullName);
        ((MsixService)_service).ResolveAliasProxy = alias =>
            new FileInfo(Path.Combine(_tempDirectory.FullName, "absent-aliases", alias));
        string? indexedName = null;
        _pri.GeneratePriFileAction = candidate =>
        {
            var effective = AppxManifestDocument.Load(Path.Combine(candidate.FullName, "appxmanifest.xml"));
            indexedName = effective.IdentityName;
            Assert.AreNotEqual("Owned.App", indexedName);
            Assert.AreEqual(ExecutionAliasResolver.BuildDefaultAliasName(
                DevelopmentIdentityHelper.ComputeFamilyName(effective.IdentityName!, effective.IdentityPublisher!)),
                effective.GetExecutionAliases().Single());
            File.WriteAllText(Path.Combine(candidate.FullName, "resources.pri"), "strict image resources");
        };

        var result = await Run(unique: true, ensureAlias: true);
        Assert.AreEqual(result.PackageName, indexedName);
        Assert.AreEqual(1, _pri.CreatePriConfigCallCount);
        Assert.AreEqual(1, _pri.GeneratePriFileCallCount);
        Assert.IsEmpty(_pri.ReindexIdentityCalls);
        Assert.AreEqual("strict image resources", File.ReadAllText(Path.Combine(_layout.FullName, "resources.pri")));
        CollectionAssert.AreEqual(source, File.ReadAllBytes(_manifest.FullName));
        Assert.IsFalse(File.Exists(Path.Combine(_input.FullName, "resources.pri")));
    }

    [TestMethod]
    public async Task RawLocalizedManifestWithoutPri_RejectsImageOnlyFallback()
    {
        MakeRawManifestWithoutPri();
        var document = AppxManifestDocument.Load(_manifest.FullName);
        document.Document.Descendants(AppxManifestDocument.DefaultNs + "DisplayName").Single().Value = "ms-resource:AppName";
        document.Save(_manifest.FullName);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unique: true));
        Assert.AreEqual(0, _pri.GeneratePriFileCallCount);
        Assert.IsEmpty(_registration.RegisterLooseLayoutCalls);
        Assert.IsFalse(Directory.Exists(_layout.FullName));
    }

    [TestMethod]
    public async Task SourceAndLayoutOverlap_IsRejectedWithoutSourceMutation()
    {
        var bytes = File.ReadAllBytes(_manifest.FullName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(layout: _input));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(_manifest.FullName));
        Assert.IsEmpty(_registration.RegisterLooseLayoutCalls);
    }

    [TestMethod]
    public async Task AdditiveLayout_PreservesUnrelatedFiles()
    {
        _layout.Create();
        File.WriteAllText(Path.Combine(_layout.FullName, "user-owned.txt"), "do not delete");
        await Run(reconciliation: LayoutReconciliation.Additive);
        Assert.AreEqual("do not delete", File.ReadAllText(Path.Combine(_layout.FullName, "user-owned.txt")));
    }

    [TestMethod]
    public async Task NestedOutput_ExcludesOldLayoutAndAdjacentOwnershipState()
    {
        _layout = new DirectoryInfo(Path.Combine(_input.FullName, "AppX"));
        await Run();
        await Run();
        Assert.IsFalse(Directory.Exists(Path.Combine(_layout.FullName, "AppX")));
        Assert.IsFalse(_layout.EnumerateFiles("*", SearchOption.AllDirectories)
            .Any(file => file.Name.Contains("winapp-registration", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RawCustomOutput_ExcludesPreviouslyGeneratedDefaultLayoutAfterCleanup()
    {
        MakeRawManifestWithoutPri();
        _pri.GeneratePriFileAction = candidate =>
            File.WriteAllText(Path.Combine(candidate.FullName, "resources.pri"), "image resources");
        _layout = new DirectoryInfo(Path.Combine(_input.FullName, "AppX"));
        await Run(unique: true);
        var receipt = DevelopmentRegistrationStore.Read(_layout)!;
        Assert.IsTrue(await DevelopmentRegistrationStore.RemoveOwnedAsync(_registration, StateRoot, receipt, true));
        Assert.IsNull(DevelopmentRegistrationStore.Read(_layout));
        var previousManifest = File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml"));
        File.WriteAllText(Path.Combine(_layout.FullName, "stale.xbf"), "generated output, not source content");
        var sourceManifest = File.ReadAllBytes(_manifest.FullName);
        var custom = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "custom-layout"));

        var result = await Run(unique: true, layout: custom);

        Assert.AreEqual(receipt.Identity.EffectivePackageName, result.PackageName);
        Assert.IsFalse(Directory.Exists(Path.Combine(custom.FullName, "AppX")));
        Assert.AreEqual(2, _pri.GeneratePriFileCallCount);
        Assert.IsEmpty(_pri.ReindexIdentityCalls);
        Assert.IsFalse(File.Exists(Path.Combine(_input.FullName, "resources.pri")));
        CollectionAssert.AreEqual(sourceManifest, File.ReadAllBytes(_manifest.FullName));
        CollectionAssert.AreEqual(previousManifest, File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml")));
        Assert.AreEqual("generated output, not source content", File.ReadAllText(Path.Combine(_layout.FullName, "stale.xbf")));
        Assert.AreEqual(result.Identity!.LayoutPath,
            DevelopmentIdentityHelper.CanonicalizePath(_registration.FakeDevPackages.Single().InstallLocation!));
    }

    [TestMethod]
    [DataRow("AppX")]
    [DataRow(@"Assets\AppX")]
    public async Task RawCustomOutput_PreservesOrdinaryAppXPayloadDirectories(string relativeDirectory)
    {
        MakeRawManifestWithoutPri();
        _pri.GeneratePriFileAction = candidate =>
            File.WriteAllText(Path.Combine(candidate.FullName, "resources.pri"), "image resources");
        var payload = _input.CreateSubdirectory(relativeDirectory);
        File.WriteAllText(Path.Combine(payload.FullName, "payload.txt"), "legitimate package payload");
        var custom = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "custom-layout"));

        await Run(unique: true, layout: custom);

        Assert.AreEqual("legitimate package payload",
            File.ReadAllText(Path.Combine(custom.FullName, relativeDirectory, "payload.txt")));
        Assert.AreEqual("legitimate package payload", File.ReadAllText(Path.Combine(payload.FullName, "payload.txt")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MaterializeCustomOutput_ExcludesManagedLayoutsAndTheirAdjacentState(bool pendingOnly)
    {
        MakeRawManifestWithoutPri();
        _pri.GeneratePriFileAction = candidate =>
            File.WriteAllText(Path.Combine(candidate.FullName, "resources.pri"), "image resources");
        var managed = new DirectoryInfo(Path.Combine(_input.FullName, "previous-layout"));
        await Run(unique: true, layout: managed);
        var receipt = DevelopmentRegistrationStore.Read(managed)!;
        if (pendingOnly)
        {
            DevelopmentRegistrationStore.Begin(StateRoot, managed, new PendingDevelopmentRegistration { Candidate = receipt });
            File.Delete(DevelopmentRegistrationStore.ReceiptPath(managed));
        }
        var statePath = pendingOnly
            ? DevelopmentRegistrationStore.PendingPath(managed)
            : DevelopmentRegistrationStore.ReceiptPath(managed);
        var receiptTemp = DevelopmentRegistrationStore.ReceiptPath(managed) + ".fixture.new";
        var pendingTemp = DevelopmentRegistrationStore.PendingPath(managed) + ".fixture.new";
        File.WriteAllText(receiptTemp, "uncommitted receipt");
        File.WriteAllText(pendingTemp, "uncommitted journal");
        File.WriteAllText(Path.Combine(_input.FullName, "settings.json"), """{"userPayload":true}""");
        var previousManifest = File.ReadAllBytes(Path.Combine(managed.FullName, "appxmanifest.xml"));
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "target-layout"));

        var result = await _service.MaterializeLooseLayoutAsync(_manifest, _input, target, TestTaskContext,
            LayoutReconciliation.Exact, selfContained: true,
            developmentIdentity: new DevelopmentIdentityOptions(_input.FullName, true));

        Assert.IsNull(result.Identity!.PackageFullName);
        Assert.IsFalse(Directory.Exists(Path.Combine(target.FullName, managed.Name)));
        Assert.IsFalse(target.EnumerateFiles("*", SearchOption.AllDirectories)
            .Any(file => file.Name.Contains("winapp-registration", StringComparison.Ordinal)));
        Assert.AreEqual("""{"userPayload":true}""", File.ReadAllText(Path.Combine(target.FullName, "settings.json")));
        Assert.IsTrue(File.Exists(statePath));
        Assert.AreEqual("uncommitted receipt", File.ReadAllText(receiptTemp));
        Assert.AreEqual("uncommitted journal", File.ReadAllText(pendingTemp));
        CollectionAssert.AreEqual(previousManifest, File.ReadAllBytes(Path.Combine(managed.FullName, "appxmanifest.xml")));
        Assert.HasCount(1, _registration.RegisterLooseLayoutCalls);
        Assert.HasCount(1, _registration.FakeDevPackages);
        Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UniqueAncestorJunction_ConvergesWithPhysicalInputAndSupportsExactCleanup(bool aliasFirst)
    {
        _layout = new DirectoryInfo(Path.Combine(_input.FullName, "AppX"));
        var alias = Path.Combine(_tempDirectory.FullName, "input-alias");
        var source = File.ReadAllBytes(_manifest.FullName);
        await CreateJunctionAsync(alias, _input);
        try
        {
            Task<MsixIdentityResult> ThroughJunction() =>
                _service.AddLooseLayoutIdentityAsync(
                    new FileInfo(Path.Combine(alias, _manifest.Name)),
                    new DirectoryInfo(alias),
                    new DirectoryInfo(Path.Combine(alias, "AppX")),
                    TestTaskContext, LayoutReconciliation.Exact, selfContained: true,
                    developmentIdentity: new DevelopmentIdentityOptions(alias, true),
                    cancellationToken: TestContext.CancellationToken);

            var first = aliasFirst ? await ThroughJunction() : await Run(unique: true);
            var second = aliasFirst ? await Run(unique: true) : await ThroughJunction();

            Assert.AreEqual(first.Identity!.PackageFamilyName, second.Identity!.PackageFamilyName);
            Assert.AreEqual(first.Identity.PackageFullName, second.Identity.PackageFullName);
            Assert.AreEqual(DevelopmentIdentityHelper.CanonicalizePath(_input.FullName), second.Identity.OwnerPath);
            Assert.AreEqual(DevelopmentIdentityHelper.CanonicalizePath(_layout.FullName), second.Identity.LayoutPath);
            Assert.AreEqual(2L, second.Identity.Revision);
            Assert.HasCount(1, _registration.RegisterLooseLayoutCalls);
            Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
            CollectionAssert.AreEqual(source, File.ReadAllBytes(_manifest.FullName));

            Directory.Delete(alias);
            var receipt = DevelopmentRegistrationStore.Read(new DirectoryInfo(second.Identity.LayoutPath))!;
            Assert.IsTrue(await DevelopmentRegistrationStore.RemoveOwnedAsync(_registration, StateRoot, receipt, true));
            Assert.AreEqual(second.Identity.PackageFullName, _registration.UnregisterByFullNameCalls.Single().PackageFullName);
            Assert.IsNull(DevelopmentRegistrationStore.Read(_layout));
        }
        finally
        {
            if (Directory.Exists(alias))
            {
                Directory.Delete(alias);
            }
        }
    }

    [TestMethod]
    public async Task OriginalMode_StillRejectsAncestorJunctionWithoutRegistration()
    {
        var alias = Path.Combine(_tempDirectory.FullName, "input-alias");
        await CreateJunctionAsync(alias, _input);
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.AddLooseLayoutIdentityAsync(
                    new FileInfo(Path.Combine(alias, _manifest.Name)),
                    new DirectoryInfo(alias),
                    new DirectoryInfo(Path.Combine(alias, "AppX")),
                    TestTaskContext, LayoutReconciliation.Exact, selfContained: true,
                    developmentIdentity: new DevelopmentIdentityOptions(alias, false),
                    cancellationToken: TestContext.CancellationToken));
            StringAssert.Contains(failure.Message, "symbolic link or junction");
            Assert.IsEmpty(_registration.RegisterLooseLayoutCalls);
            Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
            Assert.IsFalse(Directory.Exists(Path.Combine(_input.FullName, "AppX")));
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    [TestMethod]
    public async Task UniqueMode_StillRejectsDescendantJunctionBeforeChangingRegistration()
    {
        await Run(unique: true);
        var receipt = DevelopmentRegistrationStore.Read(_layout)!;
        var manifest = File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml"));
        var outside = _tempDirectory.CreateSubdirectory("external-payload");
        File.WriteAllText(Path.Combine(outside.FullName, "payload.txt"), "external");
        var link = Path.Combine(_input.FullName, "linked-payload");
        await CreateJunctionAsync(link, outside);
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unique: true));
            StringAssert.Contains(failure.Message, "symbolic link or junction");
            Assert.HasCount(1, _registration.RegisterLooseLayoutCalls);
            Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
            Assert.AreEqual(receipt.Revision, DevelopmentRegistrationStore.Read(_layout)!.Revision);
            CollectionAssert.AreEqual(manifest, File.ReadAllBytes(Path.Combine(_layout.FullName, "appxmanifest.xml")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public async Task UniqueMode_RejectsPayloadJunctionEvenWithValidAdjacentReceipt()
    {
        await Run(unique: true);
        var receipt = DevelopmentRegistrationStore.Read(_layout)!;
        var link = Path.Combine(_input.FullName, "linked-managed-layout");
        var linkedReceipt = DevelopmentRegistrationStore.ReceiptPath(new DirectoryInfo(link));
        await CreateJunctionAsync(link, _layout);
        try
        {
            File.Copy(DevelopmentRegistrationStore.ReceiptPath(_layout), linkedReceipt);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unique: true));

            StringAssert.Contains(failure.Message, "symbolic link or junction");
            Assert.HasCount(1, _registration.RegisterLooseLayoutCalls);
            Assert.IsEmpty(_registration.UnregisterByFullNameCalls);
            Assert.AreEqual(receipt.Revision, DevelopmentRegistrationStore.Read(_layout)!.Revision);
        }
        finally
        {
            Directory.Delete(link);
            File.Delete(linkedReceipt);
        }
    }

    [TestMethod]
    public async Task CancelledRun_DoesNotCreateStateOrMutateLayout()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Run(cancellationToken: cancelled.Token));
        Assert.IsFalse(Directory.Exists(_layout.FullName));
        Assert.IsEmpty(_registration.RegisterLooseLayoutCalls);
    }
}
