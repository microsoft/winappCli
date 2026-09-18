// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Configuration;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class NugetStorageTests
{
    private static readonly string[] PrivateAndMirrorSources = ["private", "mirror"];
    private static readonly string[] PrivateSourceOnly = ["private"];

    private DirectoryInfo _root = null!;
    private DirectoryInfo _invocation = null!;
    private string _defaultPackages = null!;
    private RecordingDiagnostics _diagnostics = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), $".nuget-storage-test-{Guid.NewGuid():N}"));
        _invocation = _root.CreateSubdirectory("invocation");
        _defaultPackages = Path.Combine(_root.FullName, "default-packages");
        _diagnostics = new RecordingDiagnostics();
        WriteConfig(_invocation);
    }

    [TestCleanup]
    public void Cleanup() => _root.Delete(recursive: true);

    private NugetSourceProvider CreateProvider()
    {
        return new NugetSourceProvider(new CurrentDirectoryProvider(_invocation.FullName), storageDiagnostics: _diagnostics)
        {
            LoadSettings = root => Settings.LoadSpecificSettings(root, "nuget.config"),
            GetEnvironmentVariable = _ => null,
            ResolveGlobalPackagesFolder = _ => _defaultPackages,
        };
    }

    private string LocalPackages => Path.Combine(_invocation.FullName, ".winapp", "cache", "nuget", "packages");

    private static void WriteConfig(DirectoryInfo directory, string extra = "")
    {
        var document = XDocument.Parse(
            "<configuration><config><clear /></config><packageSources><clear /></packageSources><packageSourceMapping><clear /></packageSourceMapping></configuration>");
        foreach (var section in XElement.Parse($"<configuration>{extra}</configuration>").Elements())
        {
            var existing = document.Root!.Element(section.Name);
            if (existing is null)
            {
                document.Root.Add(new XElement(section));
            }
            else
            {
                existing.ReplaceWith(new XElement(section));
            }
        }
        document.Save(Path.Combine(directory.FullName, "nuget.config"));
    }

    private void DenyDefaultReads(NugetSourceProvider provider)
    {
        var read = provider.ReadPackagesDirectory;
        provider.ReadPackagesDirectory = path =>
        {
            if (path.Equals(_defaultPackages, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("default storage denied");
            }
            read(path);
        };
    }

    [TestMethod]
    public void DeniedDefault_UsesInvocationDirectoryAndWarnsOnce()
    {
        _root.CreateSubdirectory(".winapp").CreateSubdirectory("cache");
        var provider = CreateProvider();
        DenyDefaultReads(provider);

        Assert.AreEqual(LocalPackages, provider.GetPackagesDirectory().FullName);
        Assert.AreEqual(LocalPackages, provider.GetPackagesDirectory(requireWrite: true).FullName);
        Assert.HasCount(1, _diagnostics.Messages);
        StringAssert.Contains(_diagnostics.Messages[0], LocalPackages);
        Assert.IsFalse(Directory.Exists(Path.Combine(_root.FullName, ".winapp", "cache", "nuget")));
    }

    [TestMethod]
    public void ExplicitEnvironment_DeniedFolderFailsWithoutFallback()
    {
        var provider = CreateProvider();
        provider.GetEnvironmentVariable = key => key == "NUGET_PACKAGES" ? _defaultPackages : null;
        DenyDefaultReads(provider);

        var error = Assert.ThrowsExactly<NugetStorageException>(() => provider.GetPackagesDirectory());

        StringAssert.Contains(error.Message, "explicitly configured");
        Assert.IsEmpty(_diagnostics.Messages);
        Assert.IsFalse(Directory.Exists(LocalPackages));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("relative-packages")]
    public void ExplicitEnvironment_InvalidPathFailsWithoutFallback(string value)
    {
        var provider = CreateProvider();
        provider.GetEnvironmentVariable = key => key == "NUGET_PACKAGES" ? value : null;

        var error = Assert.ThrowsExactly<NugetStorageException>(() => provider.GetPackagesDirectory());

        StringAssert.Contains(error.Message, "fully qualified");
        Assert.IsEmpty(_diagnostics.Messages);
        Assert.IsFalse(Directory.Exists(LocalPackages));
    }

    [TestMethod]
    public void ExplicitConfiguration_DeniedFolderFailsWithoutFallback()
    {
        WriteConfig(_invocation, $"""<config><add key="globalPackagesFolder" value="{_defaultPackages}" /></config>""");
        var provider = CreateProvider();
        DenyDefaultReads(provider);

        var error = Assert.ThrowsExactly<NugetStorageException>(() => provider.GetPackagesDirectory());

        StringAssert.Contains(error.Message, "explicitly configured");
        Assert.IsEmpty(_diagnostics.Messages);
        Assert.IsFalse(Directory.Exists(LocalPackages));
    }

    [TestMethod]
    public void ExplicitConfiguration_EmptyFolderFailsWithoutFallback()
    {
        WriteConfig(_invocation, """<config><add key="globalPackagesFolder" value=" " /></config>""");
        var provider = CreateProvider();

        var error = Assert.ThrowsExactly<NugetStorageException>(() => provider.GetPackagesDirectory());

        StringAssert.Contains(error.Message, "globalPackagesFolder");
        Assert.IsFalse(Directory.Exists(LocalPackages));
    }

    [TestMethod]
    public void InstanceTestingOverride_RemainsIsolatedFromConfiguredGlobalStorage()
    {
        var currentDirectory = new CurrentDirectoryProvider(_invocation.FullName);
        var directories = new WinappDirectoryService(currentDirectory);
        var isolated = _root.CreateSubdirectory("isolated-cache");
        directories.SetCacheDirectoryForTesting(isolated);
        var provider = new NugetSourceProvider(currentDirectory, directories, _diagnostics)
        {
            LoadSettings = _ => throw new AssertFailedException("An instance test cache must not access machine configuration for its package path."),
        };

        Assert.AreEqual(Path.Combine(isolated.FullName, "packages"), provider.GetPackagesDirectory().FullName);
        Assert.IsEmpty(_diagnostics.Messages);
    }

    [TestMethod]
    public async Task WarmReadOnlyPackage_InstallsWithoutAWriteProbeOrFallback()
    {
        var package = Directory.CreateDirectory(Path.Combine(_defaultPackages, "warm.package", "1.0.0"));
        File.WriteAllText(Path.Combine(package.FullName, ".nupkg.metadata"), "{}");
        File.WriteAllText(Path.Combine(package.FullName, "warm.package.nuspec"), Nuspec("Warm.Package"));
        var provider = CreateProvider();
        provider.WritePackagesDirectory = _ => Assert.Fail("A warm readable package must not need writable storage.");
        var service = new NugetService(provider, new NugetPackageDownloader(provider));

        var installed = await service.InstallPackageAsync("Warm.Package", "1.0.0", CreateTaskContext(), TestContext.CancellationToken);

        Assert.AreEqual("1.0.0", installed["Warm.Package"]);
        Assert.AreEqual(package.FullName, service.GetNuGetPackageDir("Warm.Package", "1.0.0").FullName);
        Assert.IsEmpty(_diagnostics.Messages);
        Assert.IsFalse(Directory.Exists(LocalPackages));
    }

    [TestMethod]
    public void RequiredConfigurationDenied_FailsWithoutReplacingPrivateFeeds()
    {
        var provider = CreateProvider();
        provider.LoadSettings = _ => throw new UnauthorizedAccessException("private nuget.config denied");

        var error = Assert.ThrowsExactly<NugetStorageException>(() => provider.GetPackagesDirectory());

        StringAssert.Contains(error.Message, "required NuGet configuration");
        StringAssert.Contains(error.Message, "configured feeds and credentials cannot be replaced");
        Assert.IsEmpty(_diagnostics.Messages);
        Assert.IsFalse(Directory.Exists(LocalPackages));
    }

    [TestMethod]
    public void DefaultAndLocalUnavailable_ReportsBothWithoutSuccessWarning()
    {
        var provider = CreateProvider();
        DenyDefaultReads(provider);
        provider.WritePackagesDirectory = _ => throw new UnauthorizedAccessException("local storage denied");

        var error = Assert.ThrowsExactly<NugetStorageException>(() => provider.GetPackagesDirectory());

        StringAssert.Contains(error.Message, "default NuGet packages folder");
        StringAssert.Contains(error.Message, ".winapp\\cache\\nuget\\packages could not be used");
        Assert.IsEmpty(_diagnostics.Messages);
    }

    [TestMethod]
    public void Fallback_PreservesPrivateSourceOrderMappingCredentialsAndSignaturePolicy()
    {
        WriteConfig(_invocation, """
            <packageSources>
              <clear />
              <add key="private" value="https://private.invalid/v3/index.json" />
              <add key="mirror" value="https://mirror.invalid/v3/index.json" />
            </packageSources>
            <packageSourceCredentials>
              <private><add key="Username" value="test-user" /><add key="ClearTextPassword" value="test-password" /></private>
            </packageSourceCredentials>
            <packageSourceMapping>
              <clear />
              <packageSource key="private"><package pattern="Private.*" /></packageSource>
              <packageSource key="mirror"><package pattern="*" /></packageSource>
            </packageSourceMapping>
            <config><add key="signatureValidationMode" value="require" /></config>
            """);
        var provider = CreateProvider();
        var settings = provider.Settings;
        DenyDefaultReads(provider);

        Assert.AreEqual(LocalPackages, provider.GetPackagesDirectory().FullName);

        Assert.AreSame(settings, provider.Settings);
        var sources = new PackageSourceProvider(settings).LoadPackageSources().ToList();
        CollectionAssert.AreEqual(PrivateAndMirrorSources, sources.Select(source => source.Name).ToArray());
        Assert.AreEqual("test-user", sources[0].Credentials!.Username);
        Assert.AreEqual("test-password", sources[0].Credentials!.PasswordText);
        CollectionAssert.AreEqual(PrivateSourceOnly,
            provider.GetRepositoriesForPackage("Private.Package").Select(source => source.PackageSource.Name).ToArray());
        Assert.AreEqual("require", settings.GetSection("config")!.Items.OfType<AddItem>()
            .Single(item => item.Key == "signatureValidationMode").Value);
    }

    [TestMethod]
    public void ScopeKey_ChangesWithSelectedFolderAndRemainsInstanceIsolated()
    {
        Directory.CreateDirectory(_defaultPackages);
        var provider = CreateProvider();
        var original = provider.ConfigScopeKey;
        var write = provider.WritePackagesDirectory;
        provider.WritePackagesDirectory = path =>
        {
            if (path == _defaultPackages)
            {
                throw new UnauthorizedAccessException("read only");
            }
            write(path);
        };

        provider.GetPackagesDirectory(requireWrite: true);

        Assert.AreNotEqual(original, provider.ConfigScopeKey);
        StringAssert.Contains(provider.ConfigScopeKey, $"gpf={LocalPackages}");
        Assert.AreEqual(original, CreateProvider().ConfigScopeKey);
    }

    [TestMethod]
    public void ChildRestore_UsesFallbackWithoutChangingProcessEnvironment()
    {
        var provider = CreateProvider();
        DenyDefaultReads(provider);
        var original = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        var child = new ProcessStartInfo("dotnet") { WorkingDirectory = _invocation.FullName };
        var dotnet = new DotNetService(provider);

        dotnet.ConfigurePackageEnvironment(child, ["restore", "App.csproj"]);

        Assert.AreEqual(LocalPackages, child.Environment["NUGET_PACKAGES"]);
        Assert.AreEqual(original, Environment.GetEnvironmentVariable("NUGET_PACKAGES"));
        Assert.AreEqual(provider.GetPackagesDirectory().FullName, child.Environment["NUGET_PACKAGES"]);
    }

    [TestMethod]
    [DataRow(false, "restore")]
    [DataRow(false, "build")]
    [DataRow(true, "restore")]
    [DataRow(true, "build")]
    public void ChildWarmReadOnlyCache_DoesNotProbeWrites(bool explicitlyConfigured, string verb)
    {
        var package = Directory.CreateDirectory(Path.Combine(_defaultPackages, "warm.package", "1.0.0"));
        File.WriteAllText(Path.Combine(package.FullName, ".nupkg.metadata"), "{}");
        File.WriteAllText(Path.Combine(package.FullName, "warm.package.nuspec"), Nuspec("Warm.Package"));
        if (explicitlyConfigured)
        {
            WriteConfig(_invocation, $"""<config><add key="globalPackagesFolder" value="{_defaultPackages}" /></config>""");
        }
        var provider = CreateProvider();
        provider.WritePackagesDirectory = _ => Assert.Fail("Launching dotnet with a warm readable cache must not probe writes.");
        var child = new ProcessStartInfo("dotnet") { WorkingDirectory = _invocation.FullName };

        new DotNetService(provider).ConfigurePackageEnvironment(child, [verb, "App.csproj"]);

        Assert.AreEqual(_defaultPackages, child.Environment["NUGET_PACKAGES"]);
        Assert.IsEmpty(_diagnostics.Messages);
        Assert.IsFalse(Directory.Exists(LocalPackages));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChildProcess_ReceivesTheSelectedPackagesFolder(bool tokenArguments)
    {
        var provider = CreateProvider();
        DenyDefaultReads(provider);
        var project = Path.Combine(_invocation.FullName, "Environment.proj");
        File.WriteAllText(project, """
            <Project><Target Name="Report"><Message Text="PACKAGES=$(NUGET_PACKAGES)" Importance="High" /></Target></Project>
            """);
        var dotnet = new DotNetService(provider);

        var result = tokenArguments
            ? await dotnet.RunDotnetCommandAsync(_invocation, ["msbuild", project, "-t:Report", "-nologo"],
                cancellationToken: TestContext.CancellationToken)
            : await dotnet.RunDotnetCommandAsync(_invocation, $"msbuild \"{project}\" -t:Report -nologo",
                TestContext.CancellationToken);

        Assert.AreEqual(0, result.ExitCode, result.Error + result.Output);
        StringAssert.Contains(result.Output, $"PACKAGES={LocalPackages}");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChildStorageFailure_PreservesErrorAndDoesNotReplay(bool tokenArguments)
    {
        Directory.CreateDirectory(_defaultPackages);
        var provider = CreateProvider();
        provider.WritePackagesDirectory = _ => Assert.Fail("Child storage selection must not probe writes.");
        var project = Path.Combine(_invocation.FullName, "Failure.proj");
        File.WriteAllText(project, """
            <Project><Target Name="Report">
              <WriteLinesToFile File="attempts.txt" Lines="ran" Overwrite="false" />
              <Error Text="Access to NuGet packages is denied." />
            </Target></Project>
            """);
        var dotnet = new DotNetService(provider);

        var result = tokenArguments
            ? await dotnet.RunDotnetCommandAsync(_invocation, ["msbuild", project, "-t:Report", "-nologo"],
                cancellationToken: TestContext.CancellationToken)
            : await dotnet.RunDotnetCommandAsync(_invocation, $"msbuild \"{project}\" -t:Report -nologo",
                TestContext.CancellationToken);

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Output + result.Error, "Access to NuGet packages is denied.");
        Assert.HasCount(1, File.ReadAllLines(Path.Combine(_invocation.FullName, "attempts.txt")));
        Assert.IsEmpty(_diagnostics.Messages, "Failure guidance belongs to the command error, not buffered success warnings.");
        StringAssert.Contains(result.Error, "set NUGET_PACKAGES to a permitted writable directory");
        Assert.IsFalse(Directory.Exists(LocalPackages));
    }

    [TestMethod]
    public void ChildNoRestoreBuildInSelectedProjectScope_ReusesFallbackWithoutProbes()
    {
        Directory.CreateDirectory(_defaultPackages);
        var provider = CreateProvider();
        var forbidProbes = false;
        var load = provider.LoadSettings;
        provider.LoadSettings = path =>
        {
            Assert.IsFalse(forbidProbes, "An already selected project scope must not reload configuration.");
            return load(path);
        };
        var read = provider.ReadPackagesDirectory;
        provider.ReadPackagesDirectory = path =>
        {
            Assert.IsFalse(forbidProbes, "A no-restore child must not probe previously selected package storage.");
            read(path);
        };
        var write = provider.WritePackagesDirectory;
        provider.WritePackagesDirectory = path =>
        {
            Assert.IsFalse(forbidProbes, "A no-restore child must not probe writes.");
            if (path == _defaultPackages)
            {
                throw new UnauthorizedAccessException("read only");
            }
            write(path);
        };
        provider.GetPackagesDirectory(requireWrite: true);
        var project = _root.CreateSubdirectory("project");
        WriteConfig(project);
        var dotnet = new DotNetService(provider);
        dotnet.ConfigurePackageEnvironment(new ProcessStartInfo("dotnet") { WorkingDirectory = project.FullName }, ["restore"]);
        forbidProbes = true;
        var child = new ProcessStartInfo("dotnet") { WorkingDirectory = project.FullName };

        dotnet.ConfigurePackageEnvironment(child, ["build", "--no-restore"]);

        Assert.AreEqual(LocalPackages, child.Environment["NUGET_PACKAGES"]);
        Assert.HasCount(1, _diagnostics.Messages);
    }

    [TestMethod]
    public void EvaluationInUnselectedProjectScope_DoesNotOverrideProjectConfiguration()
    {
        var provider = CreateProvider();
        DenyDefaultReads(provider);
        provider.GetPackagesDirectory();
        var project = _root.CreateSubdirectory("project");
        var projectPackages = Path.Combine(project.FullName, "project-packages");
        WriteConfig(project, $"""<config><add key="globalPackagesFolder" value="{projectPackages}" /></config>""");
        provider.LoadSettings = _ => throw new AssertFailedException("Evaluation must leave an unseen project's NuGet configuration to dotnet.");
        var child = new ProcessStartInfo("dotnet") { WorkingDirectory = project.FullName };
        child.Environment.Remove("NUGET_PACKAGES");

        new DotNetService(provider).ConfigurePackageEnvironment(child, ["msbuild", "App.csproj", "--getProperty:TargetPath"]);

        Assert.IsFalse(child.Environment.ContainsKey("NUGET_PACKAGES"), "The invocation's fallback must not override an unseen project's explicit package folder.");
    }

    [TestMethod]
    public void EvaluationInInvocationScope_ReusesSelectedFallbackWithoutProbes()
    {
        var provider = CreateProvider();
        DenyDefaultReads(provider);
        provider.GetPackagesDirectory();
        provider.LoadSettings = _ => throw new AssertFailedException("Evaluation must not reload configuration.");
        provider.ReadPackagesDirectory = _ => Assert.Fail("Evaluation must not probe storage.");
        provider.WritePackagesDirectory = _ => Assert.Fail("Evaluation must not probe storage.");
        var child = new ProcessStartInfo("dotnet") { WorkingDirectory = _invocation.FullName };

        new DotNetService(provider).ConfigurePackageEnvironment(child, ["msbuild", "App.csproj", "--getProperty:TargetPath"]);

        Assert.AreEqual(LocalPackages, child.Environment["NUGET_PACKAGES"]);
    }

    [TestMethod]
    public void ChildExplicitConfiguration_IsNotReplacedByPreviouslySelectedFallback()
    {
        var provider = CreateProvider();
        DenyDefaultReads(provider);
        provider.GetPackagesDirectory(requireWrite: true);
        var project = _root.CreateSubdirectory("project");
        WriteConfig(project, $"""<config><add key="globalPackagesFolder" value="{_defaultPackages}" /></config>""");
        var child = new ProcessStartInfo("dotnet") { WorkingDirectory = project.FullName };

        var error = Assert.ThrowsExactly<NugetStorageException>(
            () => new DotNetService(provider).ConfigurePackageEnvironment(child, ["publish"]));

        StringAssert.Contains(error.Message, "explicitly configured");
    }

    [TestMethod]
    public void FallbackPackageIdJunction_BlocksChildAndInProcessAccess()
    {
        var provider = CreateProvider();
        DenyDefaultReads(provider);
        provider.GetPackagesDirectory();
        var target = _root.CreateSubdirectory("outside-package");
        var targetFile = Path.Combine(target.FullName, "sentinel.txt");
        File.WriteAllText(targetFile, "unchanged");
        var link = Path.Combine(LocalPackages, "linked.package");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target.FullName },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
        }
        try
        {
            using (File.Open(targetFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                AssertFallbackLinkRejected(provider);
            }
            Assert.AreEqual("unchanged", File.ReadAllText(targetFile));
            Assert.HasCount(1, target.GetFileSystemInfos());
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public void FallbackLeafLink_BlocksChildAndInProcessAccess()
    {
        var provider = CreateProvider();
        DenyDefaultReads(provider);
        provider.GetPackagesDirectory();
        var target = Path.Combine(_root.FullName, "outside.nuspec");
        File.WriteAllText(target, "must not read or overwrite");
        var package = Directory.CreateDirectory(Path.Combine(LocalPackages, "linked.package", "1.0.0"));
        File.WriteAllText(Path.Combine(package.FullName, ".nupkg.metadata"), "{}");
        var link = Path.Combine(package.FullName, "linked.package.nuspec");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"File symbolic links are unavailable: {ex.Message}");
        }
        try
        {
            using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                AssertFallbackLinkRejected(provider);
                var error = Assert.ThrowsExactly<IOException>(
                    () => provider.ValidatePackagePath(LocalPackages, package.FullName));
                StringAssert.Contains(error.Message, "link or reparse point");
            }
            Assert.AreEqual("must not read or overwrite", File.ReadAllText(target));
        }
        finally
        {
            File.Delete(link);
        }
    }

    private void AssertFallbackLinkRejected(NugetSourceProvider provider)
    {
        var child = new ProcessStartInfo("dotnet") { WorkingDirectory = _invocation.FullName };
        child.Environment.Remove("NUGET_PACKAGES");
        var childError = Assert.ThrowsExactly<NugetStorageException>(
            () => new DotNetService(provider).ConfigurePackageEnvironment(child, ["restore"]));
        StringAssert.Contains(childError.Message, "link or reparse point");
        Assert.IsFalse(child.Environment.ContainsKey("NUGET_PACKAGES"), "Unsafe local storage must not be exposed to a child.");
        var service = new NugetService(provider, new NugetPackageDownloader(provider));
        var inProcessError = Assert.ThrowsExactly<NugetStorageException>(
            () => service.GetNuGetPackageDir("Linked.Package", "1.0.0"));
        StringAssert.Contains(inProcessError.Message, "link or reparse point");
    }

    [TestMethod]
    [DataRow("--version", null)]
    [DataRow("--info", null)]
    [DataRow("new", "list")]
    [DataRow("new", "search")]
    [DataRow("sln", "list")]
    [DataRow("build", "--help")]
    public void UnrelatedDotnetCommands_DoNotLoadNuGetOrCreateStorage(string verb, string? argument)
    {
        var provider = CreateProvider();
        provider.LoadSettings = _ => throw new AssertFailedException("NuGet settings must remain lazy.");
        var child = new ProcessStartInfo("dotnet") { WorkingDirectory = _invocation.FullName };

        new DotNetService(provider).ConfigurePackageEnvironment(child, argument is null ? [verb] : [verb, argument]);

        Assert.IsFalse(Directory.Exists(LocalPackages));
        Assert.IsFalse(Directory.Exists(_defaultPackages));
    }

    [TestMethod]
    [DataRow("build", "--no-restore")]
    [DataRow("publish", "--no-restore")]
    [DataRow("publish", "--no-build")]
    [DataRow("run", "--no-build")]
    [DataRow("run", "--no-restore")]
    [DataRow("msbuild", "--getProperty:TargetPath")]
    [DataRow("msbuild", "-getProperty:TargetPath")]
    [DataRow("msbuild", "/getProperty:TargetPath")]
    [DataRow("msbuild", "--getItem:Compile")]
    [DataRow("build", "--getProperty:TargetPath")]
    public void EvaluationOrNoRestore_DoesNotLoadNuGetOrProbeStorage(string verb, string argument)
    {
        var provider = CreateProvider();
        provider.LoadSettings = _ => throw new AssertFailedException("Evaluation without restore must not load NuGet configuration.");
        provider.ReadPackagesDirectory = _ => Assert.Fail("Evaluation without restore must not probe package storage.");
        provider.WritePackagesDirectory = _ => Assert.Fail("Evaluation without restore must not probe package storage.");
        var child = new ProcessStartInfo("dotnet") { WorkingDirectory = _invocation.FullName };
        child.Environment.Remove("NUGET_PACKAGES");

        new DotNetService(provider).ConfigurePackageEnvironment(child, [verb, "App.csproj", argument]);

        Assert.IsFalse(child.Environment.ContainsKey("NUGET_PACKAGES"));
        Assert.IsFalse(Directory.Exists(LocalPackages));
        Assert.IsFalse(Directory.Exists(_defaultPackages));
    }

    [TestMethod]
    [DataRow("build")]
    [DataRow("publish")]
    [DataRow("run")]
    public void RestoreDisabledWithRuntimeArgument_DoesNotSelectPackages(string verb)
    {
        var provider = CreateProvider();
        provider.LoadSettings = _ => throw new AssertFailedException("-r is a runtime identifier, not an MSBuild restore request.");
        var child = new ProcessStartInfo("dotnet") { WorkingDirectory = _invocation.FullName };

        new DotNetService(provider).ConfigurePackageEnvironment(child,
            [verb, "App.csproj", "--no-restore", "-r", "win-arm64"]);

        Assert.IsFalse(Directory.Exists(_defaultPackages));
    }

    [TestMethod]
    public void RunApplicationArguments_DoNotDisablePackagePreparation()
    {
        var provider = CreateProvider();
        var child = new ProcessStartInfo("dotnet") { WorkingDirectory = _invocation.FullName };

        new DotNetService(provider).ConfigurePackageEnvironment(child,
            ["run", "--", "--no-build", "--no-restore"]);

        Assert.AreEqual(_defaultPackages, child.Environment["NUGET_PACKAGES"]);
    }

    [TestMethod]
    [DataRow("-t:Restore")]
    [DataRow("--target:Build")]
    [DataRow("/target:Build")]
    [DataRow("-restore")]
    [DataRow("/r")]
    [DataRow("--getTargetResult:Build")]
    [DataRow("@restore.rsp")]
    public void PropertyQueryWithTargetOrRestore_StillSelectsPackageStorage(string option)
    {
        var provider = CreateProvider();
        var child = new ProcessStartInfo("dotnet") { WorkingDirectory = _invocation.FullName };

        new DotNetService(provider).ConfigurePackageEnvironment(child,
            ["msbuild", "App.csproj", "--getProperty:TargetPath", option]);

        Assert.AreEqual(_defaultPackages, child.Environment["NUGET_PACKAGES"]);
    }

    [TestMethod]
    [DataRow(false, "msbuild")]
    [DataRow(true, "msbuild")]
    [DataRow(false, "build")]
    [DataRow(true, "build")]
    public async Task PropertyEvaluation_StandardSdkSucceedsWithoutNuGetConfiguration(bool tokenArguments, string verb)
    {
        var project = Path.Combine(_invocation.FullName, "App.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
              <Target Name="MustNotRunTargets" BeforeTargets="Restore;Build"><Error Text="Only evaluation is permitted." /></Target>
            </Project>
            """);
        var direct = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _invocation.FullName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { verb, project, "--getProperty:TargetPath" },
        };
        var baseline = await DotNetService.RunDotnetProcessAsync(direct, TestContext.CancellationToken);
        Assert.AreEqual(0, baseline.ExitCode, baseline.Error + baseline.Output);
        StringAssert.Contains(baseline.Output, "App.dll");

        var provider = CreateProvider();
        provider.LoadSettings = _ => throw new UnauthorizedAccessException("user nuget.config denied");
        var dotnet = new DotNetService(provider);
        var result = tokenArguments
            ? await dotnet.RunDotnetCommandAsync(_invocation, [verb, project, "--getProperty:TargetPath"],
                cancellationToken: TestContext.CancellationToken)
            : await dotnet.RunDotnetCommandAsync(_invocation, $"{verb} \"{project}\" --getProperty:TargetPath",
                TestContext.CancellationToken);

        Assert.AreEqual(0, result.ExitCode, result.Error + result.Output);
        Assert.AreEqual(baseline.Output, result.Output);
        Assert.IsFalse(Directory.Exists(LocalPackages));
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompletedFallbackGraph_IsReusedAfterStorageSwitch(bool failDuringDownload)
    {
        var feed = _root.CreateSubdirectory("feed");
        WritePackage(feed, "Root.Package", "Child.Package");
        WritePackage(feed, "Child.Package");
        WriteConfig(_invocation, $"""<packageSources><clear /><add key="private" value="{feed.FullName}" /></packageSources>""");
        var previousProvider = CreateProvider();
        DenyDefaultReads(previousProvider);
        var previous = new NugetService(previousProvider, new NugetPackageDownloader(previousProvider));
        await previous.InstallPackageAsync("Root.Package", "1.0.0", CreateTaskContext(), TestContext.CancellationToken);
        WriteConfig(_invocation, $"""
            <packageSources><clear /><add key="private" value="{feed.FullName}" /></packageSources>
            <packageSourceMapping><clear /><packageSource key="private"><package pattern="Root.Package" /></packageSource></packageSourceMapping>
            """);

        Directory.CreateDirectory(_defaultPackages);
        var provider = CreateProvider();
        if (failDuringDownload)
        {
            File.WriteAllText(Path.Combine(_defaultPackages, "root.package"), "blocks package extraction");
        }
        else
        {
            feed.Delete(recursive: true);
            var write = provider.WritePackagesDirectory;
            provider.WritePackagesDirectory = path =>
            {
                if (path == _defaultPackages)
                {
                    throw new UnauthorizedAccessException("read only");
                }
                write(path);
            };
        }
        var downloadAttempts = 0;
        var downloader = new NugetPackageDownloader(provider)
        {
            DeleteTempFile = path =>
            {
                downloadAttempts++;
                File.Delete(path);
                if (feed.Exists)
                {
                    feed.Delete(recursive: true);
                }
            },
        };
        var service = new NugetService(provider, downloader);

        var graph = await service.InstallPackageAsync("Root.Package", "1.0", CreateTaskContext(), TestContext.CancellationToken);

        Assert.AreEqual(failDuringDownload ? 1 : 0, downloadAttempts, "No download may be attempted after selecting the completed fallback.");
        Assert.HasCount(2, graph);
        Assert.AreEqual(LocalPackages, service.GetNuGetGlobalPackagesDir().FullName);
        foreach (var package in graph)
        {
            Assert.AreEqual("1.0.0", package.Value);
            Assert.IsTrue(service.IsPackageInstalled(package.Key, package.Value));
            StringAssert.StartsWith(service.GetNuGetPackageDir(package.Key, package.Value).FullName, LocalPackages);
        }
    }

    [TestMethod]
    public async Task MissingDependencyInReadOnlyCache_MovesTheWholeGraphToFallback()
    {
        var feed = _root.CreateSubdirectory("feed");
        WritePackage(feed, "Root.Package", "Child.Package");
        WritePackage(feed, "Child.Package");
        WriteConfig(_invocation, $"""<packageSources><clear /><add key="private" value="{feed.FullName}" /></packageSources>""");
        var cachedRoot = Directory.CreateDirectory(Path.Combine(_defaultPackages, "root.package", "1.0.0"));
        File.WriteAllText(Path.Combine(cachedRoot.FullName, ".nupkg.metadata"), "{}");
        File.WriteAllText(Path.Combine(cachedRoot.FullName, "root.package.nuspec"), Nuspec("Root.Package", "Child.Package"));
        var provider = CreateProvider();
        var write = provider.WritePackagesDirectory;
        provider.WritePackagesDirectory = path =>
        {
            if (path == _defaultPackages)
            {
                throw new UnauthorizedAccessException("read only");
            }
            write(path);
        };
        var service = new NugetService(provider, new NugetPackageDownloader(provider));

        var graph = await service.InstallPackageAsync("Root.Package", "1.0.0", CreateTaskContext(), TestContext.CancellationToken);

        Assert.HasCount(2, graph);
        Assert.AreEqual(LocalPackages, service.GetNuGetGlobalPackagesDir().FullName);
        foreach (var package in graph)
        {
            Assert.IsTrue(service.IsPackageInstalled(package.Key, package.Value));
            StringAssert.StartsWith(service.GetNuGetPackageDir(package.Key, package.Value).FullName, LocalPackages);
        }
        Assert.IsEmpty(Directory.GetFiles(LocalPackages, ".winapp-download-*"));
    }

    private static TaskContext CreateTaskContext()
    {
        var console = new TestConsole();
        return new TaskContext(new GroupableTask("NuGet storage test", null), null, console, NullLogger<TaskContext>.Instance, new Lock());
    }

    private static string Nuspec(string id, string? dependency = null) => $"""
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata><id>{id}</id><version>1.0.0</version><authors>winapp-tests</authors><description>Storage test</description>
          {(dependency is null ? "" : $"<dependencies><dependency id=\"{dependency}\" version=\"[1.0.0]\" /></dependencies>")}
          </metadata>
        </package>
        """;

    private static void WritePackage(DirectoryInfo feed, string id, string? dependency = null)
    {
        using var archive = ZipFile.Open(Path.Combine(feed.FullName, $"{id}.1.0.0.nupkg"), ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry($"{id}.nuspec").Open()))
        {
            writer.Write(Nuspec(id, dependency));
        }
        using var content = new StreamWriter(archive.CreateEntry("lib/net10.0/test.txt").Open());
        content.Write("test");
    }

    private sealed class RecordingDiagnostics : IStorageDiagnostics
    {
        public List<string> Messages { get; } = [];
        public void Warning(string code, string message) => Messages.Add(message);
    }
}
