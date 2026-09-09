// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Single-file-mode tests for <see cref="ProjectRunService"/>: classifying a <c>.cs</c> input, and
/// building the mirrored build/evaluate argument sets a .NET file-based app needs.
/// </summary>
[TestClass]
public class ProjectRunServiceSingleFileTests : IDisposable
{
    private DirectoryInfo _tempDir = null!;
    private ProjectRunService _service = null!;
    private TestConsole _testConsole = null!;

    /// <summary>Releases the console the service writes to. MSTest disposes the instance after each test.</summary>
    public void Dispose()
    {
        _testConsole?.Dispose();
        GC.SuppressFinalize(this);
    }

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Directory.CreateTempSubdirectory("winapp_sfrun_");
        _testConsole = new TestConsole();
        var dotnet = new FakeDotNetService();
        _service = new ProjectRunService(
            dotnet,
            new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, dotnet),
            new FakeCsWinRTMetadataShimService(),
            _testConsole,
            NullLogger<ProjectRunService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _tempDir?.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering handle must not fail the test run.
        }
    }

    private FileInfo WriteSingleFile(string name = "counter.cs")
    {
        var path = Path.Join(_tempDir.FullName, name);
        File.WriteAllText(path, "Console.WriteLine(\"hi\");");
        return new FileInfo(path);
    }

    #region Input resolution

    [TestMethod]
    public async Task ResolveInput_CsFile_ResolvesToSingleFileMode()
    {
        var singleFile = WriteSingleFile();

        var resolution = await _service.ResolveInputAsync(singleFile, TestContext.CancellationToken);

        Assert.AreEqual(WinAppRunMode.SingleFile, resolution.Mode);
        Assert.AreEqual(singleFile.FullName, resolution.SingleFile!.FullName);
        Assert.IsNull(resolution.Csproj, "A file-based app has no .csproj");
        Assert.AreEqual(_tempDir.FullName, resolution.ProjectDirectory.FullName,
            "MSBuildProjectDirectory for a file-based app is the .cs file's own directory");
    }

    [TestMethod]
    public async Task ResolveInput_CsFileWithProjectSelector_Errors()
    {
        var singleFile = WriteSingleFile();

        var exception = await Assert.ThrowsExactlyAsync<ProjectRunException>(async () =>
            await _service.ResolveInputAsync(singleFile, TestContext.CancellationToken, projectSelector: "MyApp"));

        StringAssert.Contains(exception.Message, "--project");
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryContainingOnlyACsFile_StaysInFolderMode()
    {
        // Single-file mode is reachable ONLY from an explicitly-typed .cs path. Inferring it from a
        // directory would change how existing build-output folders are classified.
        WriteSingleFile();

        var resolution = await _service.ResolveInputAsync(_tempDir, TestContext.CancellationToken);

        Assert.AreEqual(WinAppRunMode.Folder, resolution.Mode);
        Assert.IsNull(resolution.SingleFile);
    }

    [TestMethod]
    public async Task ResolveInput_UnsupportedFileType_MentionsCsInTheGuidance()
    {
        var path = Path.Join(_tempDir.FullName, "notes.txt");
        File.WriteAllText(path, "hello");

        var exception = await Assert.ThrowsExactlyAsync<ProjectRunException>(async () =>
            await _service.ResolveInputAsync(new FileInfo(path), TestContext.CancellationToken));

        StringAssert.Contains(exception.Message, ".cs file-based app");
    }

    #endregion

    #region Build-root ownership

    /// <summary>
    /// Drives <c>ResolveSingleFileIdentityAsync</c> with a canned evaluate result so the build root it
    /// infers from <c>OutputPath</c> can be asserted without running MSBuild.
    /// </summary>
    private async Task<string?> ResolveBuildRootAsync(string outputPath)
    {
        var singleFile = WriteSingleFile();
        var escaped = outputPath.Replace("\\", "\\\\");
        var evaluateResult = (0, "{\"Properties\": {\"OutputPath\": \"" + escaped + "\", \"WindowsPackageType\": \"MSIX\"}}", string.Empty);
        var dotnet = new FakeDotNetService
        {
            // The evaluate goes through the string overload; the RID probe uses the argument-list one.
            // Both answer with the same evaluated properties, exactly as one project would.
            RunDotnetCommandHandler = _ => evaluateResult,
            RunDotnetArgumentListHandler = _ => evaluateResult,
        };
        var service = new ProjectRunService(
            dotnet,
            new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, dotnet),
            new FakeCsWinRTMetadataShimService(),
            _testConsole,
            NullLogger<ProjectRunService>.Instance);

        var resolution = await service.ResolveSingleFileIdentityAsync(
            singleFile, SingleFileIdentityInputs.Default, TestContext.CancellationToken);

        return resolution.BuildRootDirectory;
    }

    [TestMethod]
    public async Task ResolveIdentity_StandardBinLayout_InfersTheBuildRoot()
    {
        // The SDK's own layout: %TEMP%\dotnet\runfile\<stem>-<hash>\bin\debug. Two levels up is the
        // per-file root that proves a registration came from THIS .cs.
        var root = await ResolveBuildRootAsync(@"C:\Temp\dotnet\runfile\counter-abc\bin\debug\");

        Assert.AreEqual(@"C:\Temp\dotnet\runfile\counter-abc", root);
    }

    [TestMethod]
    [DataRow(@"C:\apps\B\out\", DisplayName = "custom OutputPath with no bin segment")]
    [DataRow(@"C:\apps\B\", DisplayName = "OutputPath directly under a project folder")]
    public async Task ResolveIdentity_NonBinLayout_InfersNoBuildRoot(string outputPath)
    {
        // This value becomes a TRUSTED ROOT for removing a registration and its app data, so an
        // unverified shape must not produce one. '-p OutputPath=C:\apps\B\out' would otherwise reduce to
        // 'C:\apps', and `winapp unregister B\counter.cs` could then delete a same-identity registration
        // belonging to 'C:\apps\A'. With no root the caller falls back to identity alone.
        var root = await ResolveBuildRootAsync(outputPath);

        Assert.IsNull(root, "An unverified layout must not widen the ownership root");
    }

    #endregion

    #region --no-build output discovery

    /// <summary>
    /// Drives the build+evaluate path with canned evaluate output, so the <c>--no-build</c> fallback can
    /// be observed without running MSBuild. The first evaluate reports a RID-qualified directory that
    /// does not exist; the second (no-RID) reports one that does.
    /// </summary>
    private async Task<string?> ResolveNoBuildOutputAsync(bool architectureIsExplicit)
    {
        var singleFile = WriteSingleFile();
        var plainOutput = _tempDir.CreateSubdirectory("bin").CreateSubdirectory("debug");
        var ridOutput = Path.Join(_tempDir.FullName, "bin", "debug_win-x64");

        // A packaged app resolves a concrete Executable from the output, so the plain directory has to
        // look like a real build: the fallback is only useful if what it finds is actually runnable.
        File.WriteAllText(Path.Join(plainOutput.FullName, "counter.exe"), "exe");

        string Respond(string args)
        {
            // The pre-evaluate probe asks what RuntimeIdentifier the file declares; answering empty makes
            // winapp inject one, which is the situation this fallback exists for. The evaluate that
            // carries the RID then reports the missing RID-qualified path, and the no-RID fallback
            // reports the plain directory a normal `dotnet build app.cs` produced.
            var dir = args.Contains("win-x64", StringComparison.OrdinalIgnoreCase) ? ridOutput : plainOutput.FullName;
            return "{\"Properties\": {\"TargetDir\": \"" + dir.Replace("\\", "\\\\") + "\", \"AssemblyName\": \"counter\", \"RuntimeIdentifier\": \"\", \"WindowsPackageType\": \"MSIX\", \"OutputType\": \"Exe\"}}";
        }

        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args => (0, Respond(args), string.Empty),
            RunDotnetArgumentListHandler = args => (0, Respond(string.Join(' ', args)), string.Empty),
        };
        var service = new ProjectRunService(
            dotnet,
            new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, dotnet),
            new FakeCsWinRTMetadataShimService(),
            _testConsole,
            NullLogger<ProjectRunService>.Instance);

        var options = new SingleFileRunOptions("Debug", "x64", architectureIsExplicit, NoBuild: true, NoRestore: false, []);

        try
        {
            var outcome = await service.BuildAndResolveSingleFileAsync(singleFile, options, TestContext.CancellationToken);
            return outcome.Resolution?.OutputDirectory;
        }
        catch (ProjectRunException)
        {
            // The explicit-architecture case reports the missing RID-qualified output rather than
            // substituting one, which is the behavior under test.
            return null;
        }
    }

    [TestMethod]
    [DataRow("RuntimeIdentifier=win-arm64", DisplayName = "plain")]
    [DataRow(" RuntimeIdentifier=win-arm64", DisplayName = "leading space")]
    [DataRow("RuntimeIdentifier =win-arm64", DisplayName = "space before '='")]
    public async Task UserRuntimeIdentifierProperty_IsNeverOverriddenByAnInjectedRid(string property)
    {
        // A user -p:RuntimeIdentifier is theirs to own. The injection check and the forwarding filter
        // parsed the token differently: the check used a raw StartsWith while the filter trimmed, so a
        // padded name was invisible to the check (winapp injected the host RID) and visible to the filter
        // (which then dropped the user's value) — the app silently built for the wrong architecture.
        var singleFile = WriteSingleFile();
        var output = _tempDir.CreateSubdirectory("bin").CreateSubdirectory("debug");
        File.WriteAllText(Path.Join(output.FullName, "counter.exe"), "exe");

        var evaluated = "{\"Properties\": {\"TargetDir\": \"" + output.FullName.Replace("\\", "\\\\") +
            "\", \"AssemblyName\": \"counter\", \"RuntimeIdentifier\": \"\", \"WindowsPackageType\": \"MSIX\", \"OutputType\": \"Exe\"}}";
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = _ => (0, evaluated, string.Empty),
            RunDotnetArgumentListHandler = _ => (0, evaluated, string.Empty),
        };
        var service = new ProjectRunService(
            dotnet,
            new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, dotnet),
            new FakeCsWinRTMetadataShimService(),
            _testConsole,
            NullLogger<ProjectRunService>.Instance);
        var options = new SingleFileRunOptions("Debug", "x64", ArchitectureIsExplicit: false, NoBuild: false, NoRestore: false, [property]);

        await service.BuildAndResolveSingleFileAsync(singleFile, options, TestContext.CancellationToken);

        var invocations = dotnet.StringInvocations
            .Concat(dotnet.ArgumentListInvocations.Select(a => string.Join(' ', a)))
            .ToList();
        Assert.IsFalse(invocations.Any(a => a.Contains("-r win-x64", StringComparison.OrdinalIgnoreCase)),
            "winapp must not inject a RID over the user's own RuntimeIdentifier property");
        Assert.IsTrue(invocations.Any(a => a.Contains("win-arm64", StringComparison.OrdinalIgnoreCase)),
            "The user's RuntimeIdentifier must reach the build");
    }

    [TestMethod]
    public async Task NoBuild_InferredArchitecture_FallsBackToAPlainBuildOutput()
    {
        // winapp injects -r win-<arch>, which adds a path segment, so `dotnet build app.cs` followed by
        // `winapp run app.cs --no-build` would otherwise fail with "debug_win-x64 does not exist" while
        // bin\debug sat right there.
        var output = await ResolveNoBuildOutputAsync(architectureIsExplicit: false);

        Assert.IsNotNull(output, "--no-build should find the plain build output");
        StringAssert.EndsWith(output.TrimEnd(Path.DirectorySeparatorChar), "debug");
    }

    [TestMethod]
    public async Task NoBuild_ExplicitArchitecture_DoesNotSubstituteAnUnqualifiedOutput()
    {
        // An unqualified output carries no architecture in its path. Accepting it under an explicit
        // --arch/--runtime would launch whatever happened to be built while provisioning runtime
        // packages for the REQUESTED target — a silent mismatch. Report the miss instead.
        var output = await ResolveNoBuildOutputAsync(architectureIsExplicit: true);

        Assert.IsNull(output, "An explicitly requested architecture must not fall back to an unqualified build");
    }

    #endregion

    #region Argument construction

    private static SingleFileRunOptions Options(
        string configuration = "Debug",
        bool noBuild = false,
        bool noRestore = false,
        string? injectedRid = null,
        params string[] properties) =>
        new(configuration, "x64", ArchitectureIsExplicit: false, noBuild, noRestore, properties)
        {
            InjectedRuntimeIdentifier = injectedRid,
        };

    [TestMethod]
    public void BuildPassArguments_InjectedRid_DropsAConflictingRuntimeIdentifierProperty()
    {
        // MSBuild is last-wins and -p is emitted after -r, so forwarding a conflicting
        // -p:RuntimeIdentifier would let `--arch x64 -p RuntimeIdentifier=win-arm64` build arm64 while
        // winapp provisions an x64 Windows App Runtime.
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileBuildPassArguments(
            singleFile, Options(injectedRid: "win-x64", properties: "RuntimeIdentifier=win-arm64"), "minimal");

        StringAssert.Contains(args, "-r win-x64");
        Assert.IsFalse(args.Contains("win-arm64", StringComparison.OrdinalIgnoreCase),
            "A conflicting -p:RuntimeIdentifier must not survive alongside the injected RID");
    }

    [TestMethod]
    public void EvaluateArguments_InjectedRid_DropsAConflictingRuntimeIdentifierProperty()
    {
        // Both passes must agree, or the evaluate reads a different output directory than the build wrote.
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileEvaluateArguments(
            singleFile, Options(injectedRid: "win-x64", properties: "RuntimeIdentifier=win-arm64"));

        StringAssert.Contains(args, "-r win-x64");
        Assert.IsFalse(args.Contains("win-arm64", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void BuildPassArguments_NoInjectedRid_KeepsTheUsersRuntimeIdentifier()
    {
        // No RID injected means the app declared its own (or the user did), so the property is theirs to
        // own and must flow through untouched.
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileBuildPassArguments(
            singleFile, Options(properties: "RuntimeIdentifier=win-arm64"), "minimal");

        StringAssert.Contains(args, "RuntimeIdentifier=win-arm64");
        Assert.IsFalse(args.Contains("-r ", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProbeArguments_KeepTheUsersRuntimeIdentifier_EvenWhenARidWouldBeInjected()
    {
        // The probe omits -r entirely and exists to discover whether a RuntimeIdentifier is declared, so
        // filtering the property here would blind it to the very thing it asks about.
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileProbeArguments(
            singleFile, Options(injectedRid: "win-x64", properties: "RuntimeIdentifier=win-arm64"), "RuntimeIdentifier");

        StringAssert.Contains(args, "RuntimeIdentifier=win-arm64");
        Assert.IsFalse(args.Contains("-r ", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildPassArguments_UseDotnetBuildWithConfigurationOnly()
    {
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileBuildPassArguments(singleFile, Options(), "minimal");

        StringAssert.StartsWith(args, "build ");
        StringAssert.Contains(args, singleFile.FullName);
        StringAssert.Contains(args, "-c Debug");
        StringAssert.Contains(args, "-tl:off");
    }

    [TestMethod]
    public void BuildPassArguments_NeverInjectARuntimeIdentifierOrPlatform()
    {
        // A file-based app declares its own Platform via #:property. Injecting -r/-p:Platform would move
        // the output away from the path the evaluate pass reads back, desynchronizing the two passes.
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileBuildPassArguments(singleFile, Options(), "minimal");

        Assert.IsFalse(args.Contains("-r win-", StringComparison.OrdinalIgnoreCase), $"Unexpected RID in: {args}");
        Assert.IsFalse(args.Contains("-p:Platform", StringComparison.OrdinalIgnoreCase), $"Unexpected Platform in: {args}");
    }

    [TestMethod]
    public void BuildPassArguments_NativeTerminal_OmitsTerminalLoggerOff()
    {
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileBuildPassArguments(singleFile, Options(), "minimal", nativeTerminal: true);

        Assert.IsFalse(args.Contains("-tl:off", StringComparison.Ordinal),
            "On a real terminal, dotnet's native terminal logger should render the live build");
    }

    [TestMethod]
    public void BuildPassArguments_NoRestore_IsForwarded()
    {
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileBuildPassArguments(singleFile, Options(noRestore: true), "minimal");

        StringAssert.Contains(args, "--no-restore");
    }

    [TestMethod]
    public void EvaluateArguments_UseDotnetBuildNotDotnetMsbuild()
    {
        // MSBuild has no .cs project loader: `dotnet msbuild counter.cs --getProperty:X` fails with
        // MSB4025. `dotnet build counter.cs --getProperty:X` evaluates without building.
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileEvaluateArguments(singleFile, Options());

        StringAssert.StartsWith(args, "build ");
        Assert.IsFalse(args.StartsWith("msbuild", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EvaluateArguments_RequestEveryPropertyTheInferenceReads()
    {
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileEvaluateArguments(singleFile, Options());

        foreach (var property in ProjectRunService.SingleFileRequestedProperties)
        {
            StringAssert.Contains(args, $"--getProperty:{property}");
        }

        // $(Version) is what the version inference reads; $(VersionPrefix) is empty once Version is set.
        StringAssert.Contains(args, "--getProperty:Version");
        StringAssert.Contains(args, "--getProperty:WinAppManifestPath");
    }

    [TestMethod]
    public void EvaluateArguments_DoNotRequestPlatform()
    {
        // Platform is NOT the architecture for a file-based app: '#:property Platform=arm64' is accepted
        // but leaves RuntimeIdentifier empty and still emits an x64 apphost on an x64 host. Reading it
        // would provision arm64 runtime packages for an x64 binary, so it must not be requested at all.
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileEvaluateArguments(singleFile, Options());

        Assert.IsFalse(args.Contains("--getProperty:Platform", StringComparison.Ordinal), $"Unexpected in: {args}");
        StringAssert.Contains(args, "--getProperty:RuntimeIdentifier");
    }

    [TestMethod]
    public void EvaluateArguments_MirrorTheBuildPassConfigurationAndProperties()
    {
        // The evaluate must describe the output the build actually wrote, so both passes get the same
        // Configuration and the same forwarded -p.
        var singleFile = WriteSingleFile();
        var options = Options("Release", properties: "Foo=Bar");

        var buildArgs = ProjectRunService.BuildSingleFileBuildPassArguments(singleFile, options, "minimal");
        var evaluateArgs = ProjectRunService.BuildSingleFileEvaluateArguments(singleFile, options);

        StringAssert.Contains(buildArgs, "-c Release");
        StringAssert.Contains(evaluateArgs, "-c Release");
        StringAssert.Contains(buildArgs, "-p:Foo=Bar");
        StringAssert.Contains(evaluateArgs, "-p:Foo=Bar");
    }

    [TestMethod]
    public void EvaluateArguments_DropAUserPropertyOwnedByADedicatedSwitch()
    {
        // A -p:Configuration would otherwise fight the -c the two passes share.
        var singleFile = WriteSingleFile();
        var options = Options("Debug", properties: "Configuration=Release");

        var args = ProjectRunService.BuildSingleFileEvaluateArguments(singleFile, options);

        Assert.IsFalse(args.Contains("-p:Configuration=Release", StringComparison.Ordinal), $"Unexpected in: {args}");
        StringAssert.Contains(args, "-c Debug");
    }

    [TestMethod]
    [DataRow("TargetFramework=net10.0-windows10.0.26100.0", DisplayName = "TargetFramework")]
    [DataRow("RuntimeIdentifier=win-arm64", DisplayName = "RuntimeIdentifier")]
    public void Arguments_ForwardPropertiesProjectModeReservesForItsOwnSwitches(string property)
    {
        // --arch/--runtime/--framework are rejected for a .cs, so -p is the ONLY way to express these.
        // Project mode reserves them for its dedicated switches; reusing that filter here would drop them
        // from both passes and silently ignore what the user asked for. Both passes must carry them so
        // they still agree on the output path.
        var singleFile = WriteSingleFile();
        var options = Options(properties: property);

        var buildArgs = ProjectRunService.BuildSingleFileBuildPassArguments(singleFile, options, "minimal");
        var evaluateArgs = ProjectRunService.BuildSingleFileEvaluateArguments(singleFile, options);

        StringAssert.Contains(buildArgs, $"-p:{property}");
        StringAssert.Contains(evaluateArgs, $"-p:{property}");
    }

    #endregion

    public TestContext TestContext { get; set; } = null!;
}


