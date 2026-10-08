// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class UnregisterCommandTests : BaseCommandTests
{
    private FakePackageRegistrationService _fakePackageRegistrationService = null!;
    private FakeProjectRunService _fakeProjectRunService = null!;

    [TestMethod]
    [DoNotParallelize]
    public async Task UnregisterCommand_SingleFileOnTarget_RejectsBeforeEvaluatingOrRemoving()
    {
        var input = CreateSingleFile();
        var command = GetRequiredService<WinAppRootCommand>();
        var originalError = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        int exitCode;
        try
        {
            exitCode = await ParseAndInvokeWithCaptureAsync(command,
                ["unregister", input.FullName, "--on", "sandbox", "--json"]);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.AreEqual(TargetOutput.InvalidCommandLineExitCode, exitCode);
        StringAssert.Contains(error.ToString(), "target_invalid_arguments");
        StringAssert.Contains(error.ToString(), "manifest");
        Assert.IsEmpty(_fakeProjectRunService.ResolveSingleFileIdentityInputs);
        Assert.IsEmpty(_fakePackageRegistrationService.UnregisterByFullNameCalls);
    }

    private const string TestManifestContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                 IgnorableNamespaces="uap rescap">
          <Identity Name="TestPackage"
                    Publisher="CN=TestPublisher"
                    Version="1.0.0.0" />
          <Properties>
            <DisplayName>Test Package</DisplayName>
            <PublisherDisplayName>Test Publisher</PublisherDisplayName>
            <Description>Test package</Description>
            <Logo>Assets\Logo.png</Logo>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Applications>
            <Application Id="TestApp" Executable="TestApp.exe" EntryPoint="TestApp.App">
              <uap:VisualElements DisplayName="Test App" Description="Test application"
                                  BackgroundColor="#777777" Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\Logo.png" />
            </Application>
          </Applications>
          <Capabilities>
            <rescap:Capability Name="runFullTrust" />
          </Capabilities>
        </Package>
        """;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakePackageRegistrationService = new FakePackageRegistrationService();
        _fakeProjectRunService = new FakeProjectRunService();
        return services
            .AddSingleton<IPackageRegistrationService>(_fakePackageRegistrationService)
            .AddSingleton<IProjectRunService>(_fakeProjectRunService);
    }

    private async Task<FileInfo> CreateTestManifestAsync(string? directory = null)
    {
        directory ??= _tempDirectory.FullName;
        var manifestPath = Path.Combine(directory, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, TestManifestContent, TestContext.CancellationToken);
        return new FileInfo(manifestPath);
    }

    [TestMethod]
    public async Task UnregisterCommand_WithManifest_UnregistersDevPackages()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                _tempDirectory.FullName, IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.FindDevPackagesCalls.Contains("TestPackage"));
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterByFullNameCalls.Any(c => c.PackageFullName == "TestPackage_1.0.0.0_x64__abc123"));
    }

    [TestMethod]
    public async Task UnregisterCommand_ChecksBothNameAndDebugVariant()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();
        _fakePackageRegistrationService.FakeDevPackages = [];

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.FindDevPackagesCalls.Contains("TestPackage"));
        Assert.IsTrue(_fakePackageRegistrationService.FindDevPackagesCalls.Contains("TestPackage.debug"));
    }

    [TestMethod]
    public async Task UnregisterCommand_SkipsNonDevModePackages()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                _tempDirectory.FullName, IsDevelopmentMode: false)
        ];

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_SkipsPackagesFromDifferentTree()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                @"C:\OtherProject\bin\Debug\AppX", IsDevelopmentMode: true)
        ];

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_ExplicitManifestOutsideCurrentDirectory_UnregistersFromManifestTree()
    {
        // A file-based app's generated manifest lives in the SDK's runfile output under
        // %LOCALAPPDATA%\Temp, never beneath the directory the user runs from, so the guard has to
        // be scoped to the manifest the caller named rather than the current directory.
        var layoutRoot = _tempDirectory.CreateSubdirectory("runfile").CreateSubdirectory("bin_debug");
        var manifest = await CreateTestManifestAsync(layoutRoot.FullName);
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                Path.Join(layoutRoot.FullName, "AppX"), IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        // Act — no --force, and the working directory is _tempDirectory, not the layout
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterByFullNameCalls.Any(c => c.PackageFullName == "TestPackage_1.0.0.0_x64__abc123"));
    }

    [TestMethod]
    public async Task UnregisterCommand_ExplicitManifest_StillSkipsPackageRegisteredElsewhere()
    {
        // Scoping to the manifest must not weaken the guard: a package registered from an unrelated
        // tree is still refused even though the manifest itself is outside the working directory.
        var layoutRoot = _tempDirectory.CreateSubdirectory("elsewhere");
        var manifest = await CreateTestManifestAsync(layoutRoot.FullName);
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                @"C:\OtherProject\bin\Debug\AppX", IsDevelopmentMode: true)
        ];

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task UnregisterCommand_WithForce_SkipsLocationCheck()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                @"C:\OtherProject\bin\Debug\AppX", IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName, "--force"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterByFullNameCalls.Any(c => c.PackageFullName == "TestPackage_1.0.0.0_x64__abc123"));
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnregisterCommand_WithForceOnTarget_IsRejectedCoherently(bool json)
    {
        var manifest = await CreateTestManifestAsync();
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var arguments = new List<string>
        {
            "unregister",
            "--manifest",
            manifest.FullName,
            "--on",
            "sandbox",
            "--force",
        };
        if (json)
        {
            arguments.Add("--json");
        }

        var originalError = Console.Error;
        using var capturedError = new StringWriter();
        Console.SetError(capturedError);
        int exitCode;
        try
        {
            exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, [.. arguments]);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.AreEqual(TargetOutput.InvalidCommandLineExitCode, exitCode);
        var error = capturedError.ToString().Trim();
        StringAssert.Contains(error, "--force");
        if (json)
        {
            var root = System.Text.Json.JsonDocument.Parse(error).RootElement;
            Assert.AreEqual(
                ExecutionTargetErrorCodes.TargetInvalidArguments,
                root.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [TestMethod]
    public async Task UnregisterCommand_WithJson_ReturnsJsonOutput()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                _tempDirectory.FullName, IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName, "--json"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("TestPackage_1.0.0.0_x64__abc123"));
    }

    [TestMethod]
    public async Task UnregisterCommand_NoManifest_ReturnsError()
    {
        // Arrange — empty temp directory with no manifest
        var emptyDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "empty"));
        emptyDir.Create();
        var command = GetRequiredService<UnregisterCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        // Assert
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "missing input path")]
    [DataRow(true, DisplayName = "missing --manifest path")]
    public async Task UnregisterCommand_MissingPath_WithJson_EmitsJsonErrorNotHelp(bool viaManifest)
    {
        // AcceptExistingOnly() enforces existence during PARSING, before the handler runs, so a missing
        // path printed the plain-text help page and bypassed --json entirely. A mistyped or already-
        // deleted path is exactly the case cleanup automation has to parse.
        var missing = Path.Join(_tempDirectory.FullName, "definitely-missing", "app.cs");
        var command = GetRequiredService<UnregisterCommand>();
        string[] args = viaManifest
            ? ["--manifest", missing, "--json"]
            : [missing, "--json"];

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        Assert.AreEqual(1, exitCode);
        var output = TestAnsiConsole.Output.Trim();
        Assert.IsTrue(output.StartsWith('{'), $"stdout must be the JSON error object, not help text; got: {output}");

        // Strict parsing, the way a caller would.
        var error = System.Text.Json.JsonDocument.Parse(output).RootElement.GetProperty("Error").GetString();
        StringAssert.Contains(error, "does not exist");
    }

    [TestMethod]
    public async Task UnregisterCommand_NoManifest_WithJson_EmitsJsonError()
    {
        // No manifest in the current directory + --json should emit a structured JSON error
        // (rather than a plain log line) and still fail.
        var command = GetRequiredService<UnregisterCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);

        Assert.AreEqual(1, exitCode);
        var output = TestAnsiConsole.Output.Trim();
        var root = System.Text.Json.JsonDocument.Parse(output).RootElement;
        Assert.IsTrue(root.TryGetProperty("Error", out var error), "JSON output should carry an Error property");
        StringAssert.Contains(error.GetString(), "No manifest found");
    }

    [TestMethod]
    public async Task UnregisterCommand_LongErrorAtDefaultWidth_StillEmitsParseableJson()
    {
        // The payload has to survive the default ~80-column console. Rendering it through
        // ansiConsole.WriteLine word-wraps it and injects raw CR/LF INSIDE the string values, which strict
        // parsers reject with "0x0D is invalid within a JSON string" — so scripts and the npm wrapper
        // cannot read a documented machine-readable error. This message is well over 80 characters, so it
        // reproduces without any artificial setup. (The document itself is pretty-printed, so the check is
        // that no wrapping lands inside a VALUE, not that the output is one line.)
        var command = GetRequiredService<UnregisterCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--prune", "--property", "A=1", "--json"]);

        Assert.AreEqual(1, exitCode);
        var output = TestAnsiConsole.Output.Trim();

        // Strict parsing, the way a caller would. This is what fails on a wrapped payload.
        var error = System.Text.Json.JsonDocument.Parse(output).RootElement.GetProperty("Error").GetString();

        Assert.IsNotNull(error);
        Assert.IsGreaterThan(80, error.Length, "The message must exceed the console width for this to be meaningful");
        Assert.IsFalse(
            error.Contains('\n', StringComparison.Ordinal) || error.Contains('\r', StringComparison.Ordinal),
            "The error value must not carry wrapping newlines");
        StringAssert.Contains(error, "--prune sweeps by registration state");
        StringAssert.Contains(error, "--output-appx-directory.", "The tail of the message must survive intact");
    }

    #region Single-file apps

    private FileInfo CreateSingleFile(string name = "counter.cs")
    {
        var path = Path.Join(_tempDirectory.FullName, name);
        File.WriteAllText(path, "Console.WriteLine(\"hi\");");
        return new FileInfo(path);
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_UnregistersInferredIdentity()
    {
        // `winapp unregister counter.cs` must remove what `winapp run counter.cs` registered, without a
        // manifest path: the generated manifest lives in the SDK's temp output, which the user never sees.
        var singleFile = CreateSingleFile();
        var buildRoot = _tempDirectory.CreateSubdirectory("runfile").CreateSubdirectory("counter-abc123");
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, buildRoot.FullName);
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                Path.Join(buildRoot.FullName, "bin", "debug_win-x64", "AppX"), IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeProjectRunService.ResolveSingleFileIdentityCalls.Count);
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterByFullNameCalls.Any(c => c.PackageFullName == "counter_1.0.0.0_x64__abc"));
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_SkipsPackageRegisteredFromAnotherFile()
    {
        // Two counter.cs files in different folders share the default identity, so the guard has to
        // confirm the registration came from THIS file's build root before removing it.
        var singleFile = CreateSingleFile();
        var buildRoot = _tempDirectory.CreateSubdirectory("runfile").CreateSubdirectory("counter-mine");
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, buildRoot.FullName);
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                @"C:\Temp\dotnet\runfile\counter-theirs\bin\debug\AppX", IsDevelopmentMode: true)
        ];

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_WithForce_RemovesRegardlessOfLocation()
    {
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, @"C:\Temp\runfile\counter-mine");
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                @"C:\Temp\dotnet\runfile\counter-theirs\bin\debug\AppX", IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--force"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterByFullNameCalls.Any(c => c.PackageFullName == "counter_1.0.0.0_x64__abc"));
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_WithForce_RemovesDespiteUnresolvedBuildRoot()
    {
        // --force is the documented way to remove a package whose ownership cannot be verified.
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, BuildRootDirectory: null);
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                @"C:\Temp\dotnet\runfile\counter-abc\bin\debug\AppX", IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--force"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterByFullNameCalls.Any(c => c.PackageFullName == "counter_1.0.0.0_x64__abc"));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task UnregisterCommand_SingleFile_Unpackaged_ReportsNothingToRemove()
    {
        // An unpackaged app never registered, so "no dev-registered package found" would read as a
        // failure to find something that ought to exist.
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Unpackaged, BuildRootDirectory: null);

        var (exitCode, ambientOutput) = await InvokeWithAmbientConsoleCaptureAsync(command, [singleFile.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
        StringAssert.Contains(ambientOutput, "unpackaged app");
    }

    [TestMethod]
    public async Task UnregisterCommand_NonCsFileInput_IsRejectedWithGuidance()
    {
        var notAnApp = new FileInfo(Path.Join(_tempDirectory.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(notAnApp.FullName, TestManifestContent, TestContext.CancellationToken);
        var command = GetRequiredService<UnregisterCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [notAnApp.FullName]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--manifest");
        Assert.AreEqual(0, _fakeProjectRunService.ResolveSingleFileIdentityCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_EvaluationFailure_ReportsError()
    {
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentityThrows =
            new ProjectRunException("Could not evaluate 'counter.cs' to determine its package identity.");

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_InputAndManifestTogether_IsRejected()
    {
        // The two name a package different ways and can resolve to DIFFERENT packages, so silently
        // preferring one would remove a registration (and its app data) the user did not ask for.
        var singleFile = CreateSingleFile();
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--manifest", manifest.FullName]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.ResolveSingleFileIdentityCalls.Count);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_SiblingDirectorySharingAPrefix_IsSkipped()
    {
        // 'C:\apps\counter-old' string-starts-with 'C:\apps\counter' but is a different tree. A plain
        // prefix check would remove that package and delete its app data. Both paths sit outside the
        // working directory so only the resolved build root is under test.
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, @"C:\apps\counter");
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                @"C:\apps\counter-old\AppX", IsDevelopmentMode: true)
        ];

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count,
            "A sibling directory that merely shares a string prefix is a different tree");
    }

    [TestMethod]
    public async Task UnregisterCommand_RemovesOnlyTheVettedPackage_NotEverySameNamedOne()
    {
        // The per-package checks are meaningless if removal is name-wide: a same-named package this loop
        // deliberately skipped would be deleted anyway, along with its application data.
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__theirs", "TestPackage", "1.0.0.0",
                @"C:\OtherProject\bin\Debug\AppX", IsDevelopmentMode: true),
            new DevPackageInfo("TestPackage_1.0.0.0_x64__mine", "TestPackage", "1.0.0.0",
                Path.Join(_tempDirectory.FullName, "bin", "Debug", "AppX"), IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        Assert.AreEqual(0, exitCode);
        var removed = _fakePackageRegistrationService.UnregisterByFullNameCalls
            .Select(c => c.PackageFullName)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.AreEqual(1, removed.Count, "Only the vetted package may be removed");
        Assert.AreEqual("TestPackage_1.0.0.0_x64__mine", removed[0]);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count,
            "The name-wide overload would also remove the out-of-tree package");
    }

    [TestMethod]
    public async Task UnregisterCommand_WindowsRefusesRemoval_ReportsSkippedNotUnregistered()
    {
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                _tempDirectory.FullName, IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterByFullNameResult = false;

        await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName, "--json"]);

        var root = System.Text.Json.JsonDocument.Parse(TestAnsiConsole.Output.Trim()).RootElement;
        Assert.IsFalse(root.TryGetProperty("Unregistered", out var u) && u.ValueKind != System.Text.Json.JsonValueKind.Null,
            "A refused removal must not be reported as unregistered");
        Assert.IsTrue(root.TryGetProperty("Skipped", out var s) && s.ValueKind != System.Text.Json.JsonValueKind.Null);
    }

    [TestMethod]
    public async Task UnregisterCommand_WindowsRefusesRemoval_ReturnsNonZero()
    {
        // A package the user explicitly named, that Windows then refused to remove, must not report
        // success — automation would carry on believing the registration is gone.
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                _tempDirectory.FullName, IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterByFullNameResult = false;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task UnregisterCommand_SafetySkip_IsNotAFailure()
    {
        // The out-of-tree guard doing its job is the command working as intended, not an error — it
        // already tells the user to pass --force.
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                @"C:\OtherProject\bin\Debug\AppX", IsDevelopmentMode: true)
        ];

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_UnresolvedBuildRoot_IsSkippedNotRemoved()
    {
        // Identity alone is not proof of ownership: an explicit WinAppPackageName or an authored
        // manifest's Identity/@Name is used verbatim, so two apps under different roots can deliberately
        // share one. With no build root to compare against, removal would let one file delete the other's
        // registration and app data.
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, BuildRootDirectory: null);
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                @"C:\Temp\dotnet\runfile\counter-abc\bin\debug\AppX", IsDevelopmentMode: true)
        ];

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_UnknownInstallLocation_IsSkippedNotRemoved()
    {
        // The package's files were deleted, so Windows cannot report where it came from. That makes
        // ownership unverifiable, not verified — --prune is the supported way to clear these.
        var singleFile = CreateSingleFile();
        var buildRoot = _tempDirectory.CreateSubdirectory("runfile").CreateSubdirectory("counter-mine");
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, buildRoot.FullName);
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0", null, IsDevelopmentMode: true)
        ];

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_UnknownInstallLocation_WithForce_IsRemoved()
    {
        // --force is the documented escape hatch for exactly this case.
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, BuildRootDirectory: null);
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0", null, IsDevelopmentMode: true)
        ];

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--force"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterByFullNameCalls.Any(c => c.PackageFullName == "counter_1.0.0.0_x64__abc"));
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_ForwardsPropertiesToIdentityResolution()
    {
        // `run counter.cs -p WinAppPackageName=X` registers X, because a command-line property overrides
        // the file's own directives. Without forwarding, unregister would resolve a different identity
        // and leave X registered with nothing able to name it.
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("com.contoso.alt", ProjectPackaging.Packaged, BuildRootDirectory: null);

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "-p", "WinAppPackageName=com.contoso.alt"]);

        var forwarded = _fakeProjectRunService.ResolveSingleFileIdentityInputs.Single().Properties;
        Assert.AreEqual(1, forwarded.Count);
        Assert.AreEqual("WinAppPackageName=com.contoso.alt", forwarded[0]);
    }

    [TestMethod]
    public async Task UnregisterCommand_PropertyWithoutSingleFileInput_IsRejected()
    {
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName, "-p", "WinAppPackageName=x"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_MalformedProperty_IsRejected()
    {
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "-p", "NoEqualsSign"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.ResolveSingleFileIdentityCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_ExplicitManifest_TrustsTheCurrentDirectoryToo()
    {
        // `run . --manifest C:\shared\custom.appxmanifest` copies that manifest into the INPUT's AppX
        // layout, so the registration lives under the project — not under the manifest's own folder.
        // Trusting only the manifest directory would refuse to clean up its own registrations.
        var sharedDir = _tempDirectory.CreateSubdirectory("shared");
        var manifest = await CreateTestManifestAsync(sharedDir.FullName);
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            // _tempDirectory is the working directory for these tests.
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                Path.Join(_tempDirectory.FullName, "bin", "Debug", "AppX"), IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterByFullNameCalls.Any(c => c.PackageFullName == "TestPackage_1.0.0.0_x64__abc123"));
    }

    [TestMethod]
    public async Task UnregisterCommand_OutputAppXDirectory_IsAcceptedAsAnOwnershipRoot()
    {
        // `run --output-appx-directory X` relocates the layout, and nothing on the package records which
        // run option produced it — so the caller has to be able to name it here.
        var layout = _tempDirectory.CreateSubdirectory("layouts").CreateSubdirectory("counter");
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, @"C:\Temp\runfile\counter-abc");
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                layout.FullName, IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [singleFile.FullName, "--output-appx-directory", layout.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterByFullNameCalls.Any(c => c.PackageFullName == "counter_1.0.0.0_x64__abc"));
    }

    [TestMethod]
    public async Task UnregisterCommand_OutputAppXDirectory_StillRejectsAnUnrelatedTree()
    {
        // Naming a layout widens trust to that directory only — it is not a back-door --force.
        var layout = _tempDirectory.CreateSubdirectory("named-layout");
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, @"C:\Temp\runfile\counter-abc");
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                @"C:\SomewhereElse\AppX", IsDevelopmentMode: true)
        ];

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [singleFile.FullName, "--output-appx-directory", layout.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_ForwardsConfigurationToIdentityResolution()
    {
        // A Directory.Build.props beside the .cs can set WinAppPackageName conditionally on
        // $(Configuration), so `run -c Release` and `unregister` must evaluate the same one.
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, BuildRootDirectory: null);

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "-c", "Release"]);

        Assert.AreEqual("Release", _fakeProjectRunService.ResolveSingleFileIdentityInputs.Single().Configuration);
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_DefaultsToDebugConfiguration()
    {
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, BuildRootDirectory: null);

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        Assert.AreEqual("Debug", _fakeProjectRunService.ResolveSingleFileIdentityInputs.Single().Configuration);
    }

    [TestMethod]
    public async Task UnregisterCommand_ConfigurationWithoutSingleFileInput_IsRejected()
    {
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName, "-c", "Release"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_ForwardsArchitectureToIdentityResolution()
    {
        // A Directory.Build.props can key WinAppPackageName off $(RuntimeIdentifier), so the RID decision
        // has to match the run's or unregister resolves a different package.
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, BuildRootDirectory: null);

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--arch", "arm64"]);

        var inputs = _fakeProjectRunService.ResolveSingleFileIdentityInputs.Single();
        Assert.AreEqual("arm64", inputs.Architecture);
        Assert.IsTrue(inputs.ArchitectureIsExplicit);
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_RuntimeOverridesArch()
    {
        // Same precedence run uses: --runtime's architecture beats --arch.
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, BuildRootDirectory: null);

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--arch", "x64", "-r", "win-arm64"]);

        Assert.AreEqual("arm64", _fakeProjectRunService.ResolveSingleFileIdentityInputs.Single().Architecture);
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_NoArchitectureSwitch_IsNotExplicit()
    {
        // Not explicit means the app's own RuntimeIdentifier wins, exactly as in a plain run.
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, BuildRootDirectory: null);

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        Assert.IsFalse(_fakeProjectRunService.ResolveSingleFileIdentityInputs.Single().ArchitectureIsExplicit);
    }

    [TestMethod]
    public async Task UnregisterCommand_InvalidRuntime_IsRejected()
    {
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "-r", "linux-x64"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.ResolveSingleFileIdentityCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_ValuelessProperty_WithJson_EmitsJsonError()
    {
        // System.CommandLine's own arity error prints plain-text help, which would corrupt the
        // machine-readable contract a --json caller depends on.
        var command = GetRequiredService<UnregisterCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json", "-p"]);

        Assert.AreEqual(1, exitCode);
        var root = System.Text.Json.JsonDocument.Parse(TestAnsiConsole.Output.Trim()).RootElement;
        Assert.IsTrue(root.TryGetProperty("Error", out var error));
        StringAssert.Contains(error.GetString(), "without a value");
    }

    #endregion

    #region Prune

    [TestMethod]
    public async Task UnregisterCommand_Prune_WithForce_RemovesEveryOrphanByFullName()
    {
        // Orphans are removed by FULL name, so a same-named package still installed from a live
        // location is untouched.
        var command = GetRequiredService<UnregisterCommand>();
        _fakePackageRegistrationService.FakeOrphanedDevPackages =
        [
            new DevPackageInfo("dead.one_1.0.0.0_x64__abc", "dead.one", "1.0.0.0", null, IsDevelopmentMode: true),
            new DevPackageInfo("dead.two_1.0.0.0_x64__abc", "dead.two", "1.0.0.0", @"C:\gone", IsDevelopmentMode: true)
        ];

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--prune", "--force"]);

        Assert.AreEqual(0, exitCode);
        var removed = _fakePackageRegistrationService.UnregisterByFullNameCalls
            .Select(c => c.PackageFullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.AreEqual(2, removed.Count);
        Assert.AreEqual("dead.one_1.0.0.0_x64__abc", removed[0]);
        Assert.AreEqual("dead.two_1.0.0.0_x64__abc", removed[1]);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count,
            "Prune must not fall back to identity-name removal, which could catch a live package");
    }

    [TestMethod]
    public async Task UnregisterCommand_Prune_NothingOrphaned_SaysSoAndRemovesNothing()
    {
        var command = GetRequiredService<UnregisterCommand>();
        _fakePackageRegistrationService.FakeOrphanedDevPackages = [];

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--prune", "--force"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_Prune_NonInteractiveWithoutForce_RefusesInsteadOfAssumingConsent()
    {
        // Removing packages without being able to ask is exactly the case that needs an explicit opt-in.
        var command = GetRequiredService<UnregisterCommand>();
        _fakePackageRegistrationService.FakeOrphanedDevPackages =
        [
            new DevPackageInfo("dead.one_1.0.0.0_x64__abc", "dead.one", "1.0.0.0", null, IsDevelopmentMode: true)
        ];

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--prune", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count);
        var root = System.Text.Json.JsonDocument.Parse(TestAnsiConsole.Output.Trim()).RootElement;
        Assert.IsTrue(root.TryGetProperty("Error", out var error));
        StringAssert.Contains(error.GetString(), "--force");
    }

    [TestMethod]
    public async Task UnregisterCommand_Prune_OneFailure_StillAttemptsTheRestAndReportsFailure()
    {
        var command = GetRequiredService<UnregisterCommand>();
        _fakePackageRegistrationService.FakeOrphanedDevPackages =
        [
            new DevPackageInfo("dead.one_1.0.0.0_x64__abc", "dead.one", "1.0.0.0", null, IsDevelopmentMode: true),
            new DevPackageInfo("dead.two_1.0.0.0_x64__abc", "dead.two", "1.0.0.0", null, IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.UnregisterByFullNameThrows = new InvalidOperationException("access denied");

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--prune", "--force"]);

        // Nonzero: a script must not carry on as though the stale registrations were removed.
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(1, _fakePackageRegistrationService.FindOrphanedDevPackagesCallCount);
    }

    [TestMethod]
    public async Task UnregisterCommand_Prune_WindowsRefusesRemoval_DoesNotClaimSuccess()
    {
        // Windows reports a refused removal as error text rather than an exception, so ignoring the
        // result would hand cleanup automation a false confirmation.
        var command = GetRequiredService<UnregisterCommand>();
        _fakePackageRegistrationService.FakeOrphanedDevPackages =
        [
            new DevPackageInfo("dead.one_1.0.0.0_x64__abc", "dead.one", "1.0.0.0", null, IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterByFullNameResult = false;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--prune", "--force", "--json"]);

        Assert.AreEqual(1, exitCode);
        var root = System.Text.Json.JsonDocument.Parse(TestAnsiConsole.Output.Trim()).RootElement;
        Assert.IsFalse(root.TryGetProperty("Unregistered", out var u) && u.ValueKind != System.Text.Json.JsonValueKind.Null,
            "A refused removal must not be reported as unregistered");
    }

    [TestMethod]
    public async Task UnregisterCommand_Prune_WithInput_IsRejected()
    {
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--prune", "--force"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.FindOrphanedDevPackagesCallCount);
        Assert.AreEqual(0, _fakeProjectRunService.ResolveSingleFileIdentityCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_Prune_WithManifest_IsRejected()
    {
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName, "--prune", "--force"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.FindOrphanedDevPackagesCallCount);
    }

    [TestMethod]
    public async Task UnregisterCommand_Prune_PreservesApplicationData()
    {
        // LocalState lives in %LOCALAPPDATA%\Packages, not the install location, so missing install
        // files are no evidence the data is unwanted — and for a file-based app the identity is stable,
        // so re-running restores the app and finds its settings again.
        var command = GetRequiredService<UnregisterCommand>();
        _fakePackageRegistrationService.FakeOrphanedDevPackages =
        [
            new DevPackageInfo("dead.one_1.0.0.0_x64__abc", "dead.one", "1.0.0.0", null, IsDevelopmentMode: true)
        ];

        await ParseAndInvokeWithCaptureAsync(command, ["--prune", "--force"]);

        var call = _fakePackageRegistrationService.UnregisterByFullNameCalls.Single();
        Assert.IsTrue(call.PreserveAppData, "A bulk sweep must not delete data for packages the user never named");
    }

    [TestMethod]
    public async Task UnregisterCommand_ExplicitRemoval_StillDeletesApplicationData()
    {
        // The contrast with prune: naming one package explicitly IS a deliberate act, so its data goes.
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                _tempDirectory.FullName, IsDevelopmentMode: true)
        ];

        await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // The fake returns the same list for both the name and the `.debug` lookup, so assert on the
        // flag every call carried rather than on a single call.
        var calls = _fakePackageRegistrationService.UnregisterByFullNameCalls;
        Assert.IsGreaterThan(0, calls.Count);
        Assert.IsTrue(calls.All(c => !c.PreserveAppData));
    }

    #endregion
}
