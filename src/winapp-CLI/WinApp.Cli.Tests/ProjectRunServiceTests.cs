// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class ProjectRunServiceTests
{
    private DirectoryInfo _tempDir = null!;
    private ProjectRunService _service = null!;
    private FakeCsWinRTMetadataShimService _shim = null!;

    private const string ExecutableCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
            <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string LibraryCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Library</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string TestProjectCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <IsTestProject>true</IsTestProject>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    // No inline OutputType: a static parse treats this as non-executable, but an MSBuild evaluation
    // resolves OutputType from an import (SDK/props). Used by the M5 disambiguation tests.
    private const string NoInlineOutputTypeCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    // Inline OutputType=Exe with no inline IsTestProject: a static parse treats this as a runnable
    // executable, but an MSBuild evaluation can reveal IsTestProject=true (set by the test SDK).
    private const string InlineExeNoTestFlagCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    // Multi-targeted executable: <TargetFrameworks> (plural). With no --framework, project mode must pin
    // the FIRST declared TFM (H1) so build/evaluate/provision agree; without pinning the evaluate pass
    // hits the empty cross-targeting outer node and throws after a successful build.
    private const string MultiTargetedExeCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
            <TargetFrameworks>net10.0-windows10.0.26100.0;net8.0-windows10.0.26100.0</TargetFrameworks>
          </PropertyGroup>
        </Project>
        """;

    // Multi-targeted executable whose TargetFrameworks come from an import / are conditional, so NO inline
    // <TargetFramework(s)> is visible to a static scan (DotNetService.IsMultiTargeted returns false). Project
    // mode must fall back to an MSBuild --getProperty:TargetFrameworks evaluate to discover and pin the first
    // TFM (H1 / C16); without it the build targets all TFMs and the evaluate reads the empty outer node.
    private const string ImportedMultiTargetExeCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <Import Project="Directory.Build.props" />
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
          </PropertyGroup>
        </Project>
        """;

    // Multi-targeted executable whose <TargetFrameworks> is declared per-Configuration in CONDITIONAL
    // property groups. A naive first-textual-match static read pins the Debug group's first TFM
    // (net8) even for a Release run, so project mode must instead fall back to the MSBuild
    // --getProperty:TargetFrameworks evaluate (which honors Configuration) to pin the right group's first
    // TFM (C24).
    private const string ConditionalMultiTargetExeCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
          </PropertyGroup>
          <PropertyGroup Condition="'$(Configuration)'=='Debug'">
            <TargetFrameworks>net8.0-windows10.0.26100.0;net10.0-windows10.0.26100.0</TargetFrameworks>
          </PropertyGroup>
          <PropertyGroup Condition="'$(Configuration)'=='Release'">
            <TargetFrameworks>net10.0-windows10.0.26100.0;net8.0-windows10.0.26100.0</TargetFrameworks>
          </PropertyGroup>
        </Project>
        """;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Join(Path.GetTempPath(), $"ProjectRunServiceTests_{Guid.NewGuid():N}"));
        _tempDir.Create();
        var fakeDotnet = new FakeDotNetService();
        _shim = new FakeCsWinRTMetadataShimService();
        _service = new ProjectRunService(fakeDotnet, NewDetection(fakeDotnet), _shim, new TestConsole(), NullLogger<ProjectRunService>.Instance);
    }

    // Project classification is owned by ProjectDetectionService; build a real one over the same fake
    // dotnet so evaluated OutputType/IsTestProject resolution behaves exactly as the test configured.
    private static ProjectDetectionService NewDetection(FakeDotNetService dotnet)
        => new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, dotnet);

    [TestCleanup]
    public void Cleanup()
    {
        try { _tempDir.Delete(true); } catch { /* ignore */ }
    }

    private FileInfo WriteFile(string name, string content)
    {
        var path = Path.Combine(_tempDir.FullName, name);
        File.WriteAllText(path, content);
        return new FileInfo(path);
    }

    // Writes a file at a (possibly nested) path relative to _tempDir, creating intermediate dirs.
    private FileInfo WriteFileAt(string relativePath, string content)
    {
        var path = Path.Combine(_tempDir.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return new FileInfo(path);
    }

    // Minimal classic .sln listing the given project paths (relative to the solution dir, backslashes).
    private static string SlnListing(params string[] relativeProjectPaths)
    {
        var entries = relativeProjectPaths.Select((p, i) =>
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"P{i}\", \"{p}\", \"{{{Guid.NewGuid():D}}}\"" +
            Environment.NewLine + "EndProject");
        return "Microsoft Visual Studio Solution File, Format Version 12.00" + Environment.NewLine +
            string.Join(Environment.NewLine, entries);
    }

    // Minimal XML .slnx listing the given project paths (relative to the solution dir, forward slashes).
    private static string SlnxListing(params string[] relativeProjectPaths) =>
        "<Solution>" + Environment.NewLine +
        string.Join(Environment.NewLine, relativeProjectPaths.Select(p => $"  <Project Path=\"{p}\" />")) +
        Environment.NewLine + "</Solution>";

    #region BuildBuildPassArguments (streamed build pass, Change #1)

    [TestMethod]
    public void BuildBuildPassArguments_WithCsWinRTMetadataFolder_InjectsProperty()
    {
        // SHIM: a resolved ref-pack winmd folder is injected as -p:CsWinRTWindowsMetadata so cswinrt
        // can build without a registered Windows SDK.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);
        var folder = @"C:\cache\microsoft.windows.sdk.net.ref\10.0.26100.57\winmd";

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal", folder);

        StringAssert.Contains(args, $"-p:CsWinRTWindowsMetadata={folder}");
    }

    [TestMethod]
    public void BuildBuildPassArguments_NoCsWinRTMetadataFolder_OmitsProperty()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal", csWinRTMetadataFolder: null);

        Assert.IsFalse(args.Contains("CsWinRTWindowsMetadata"), "no metadata property should be injected when the shim resolved nothing");
    }

    [TestMethod]
    [DataRow("PackageCertificatePassword", "hunter2")]
    [DataRow("NuGetApiKey", "abc123")]
    [DataRow("MySecretToken", "xyz")]
    [DataRow("SqlConnectionString", "Server=.;Pwd=p")]
    public void RedactSecretsForDisplay_MasksSecretPropertyValue(string name, string value)
    {
        var line = $"build App.csproj -p:{name}={value} -c Debug";

        var redacted = ProjectRunService.RedactSecretsForDisplay(line);

        StringAssert.Contains(redacted, $"-p:{name}=***");
        Assert.IsFalse(redacted.Contains(value), $"the secret value should not survive display: {redacted}");
    }

    [TestMethod]
    public void RedactSecretsForDisplay_LeavesNonSecretPropertiesUntouched()
    {
        var line = "build App.csproj -p:Platform=x64 -p:OutputPath=bin -c Debug";

        Assert.AreEqual(line, ProjectRunService.RedactSecretsForDisplay(line));
    }

    [TestMethod]
    public void RedactSecretsForDisplay_RedactsSignedFeedUrlInNonSecretProperty()
    {
        var line = "restore App.csproj -p:RestoreSources=https://feed.example/v3/index.json?sig=COMMAND_SECRET";

        var redacted = ProjectRunService.RedactSecretsForDisplay(line);

        Assert.IsFalse(redacted.Contains("COMMAND_SECRET", StringComparison.Ordinal));
        StringAssert.Contains(redacted, "https://feed.example/v3/index.json?<redacted>");
    }

    [TestMethod]
    public void RedactSecretsForDisplay_RedactsOnlySecretSegmentOfPackedProperty()
    {
        var line = "build -p:A=1;Password=s3cr3t;B=2";

        var redacted = ProjectRunService.RedactSecretsForDisplay(line);

        StringAssert.Contains(redacted, "A=1");
        StringAssert.Contains(redacted, "Password=***");
        StringAssert.Contains(redacted, "B=2");
        Assert.IsFalse(redacted.Contains("s3cr3t"));
    }

    [TestMethod]
    public void RedactSecretsForDisplay_RedactsSecretSegmentOfCommaPackedProperty()
    {
        var line = "build -p:A=1,SigningPassword=s3cr3t,B=2";

        var redacted = ProjectRunService.RedactSecretsForDisplay(line);

        StringAssert.Contains(redacted, "A=1,SigningPassword=***,B=2");
        Assert.IsFalse(redacted.Contains("s3cr3t"));
    }

    [TestMethod]
    public void RedactSecretsForDisplay_MasksMalformedSecretContinuation()
    {
        var line = "build -p:PackageCertificatePassword=p@ss,w0rd,B=2";

        var redacted = ProjectRunService.RedactSecretsForDisplay(line);

        Assert.IsFalse(redacted.Contains("p@ss"), $"secret prefix should be masked: {redacted}");
        Assert.IsFalse(redacted.Contains("w0rd"), $"secret continuation should be masked: {redacted}");
        StringAssert.Contains(redacted, "PackageCertificatePassword=***,***,B=2");
    }

    [TestMethod]
    public void RedactSecretsForDisplay_MasksQuotedSecretWithSpaces()
    {
        var line = "build \"-p:PackageCertificatePassword=pass word\" -c Debug";

        var redacted = ProjectRunService.RedactSecretsForDisplay(line);

        Assert.IsFalse(redacted.Contains("pass word"), $"quoted secret should be masked: {redacted}");
        StringAssert.Contains(redacted, "PackageCertificatePassword=***");
    }

    [TestMethod]
    public void RedactSecretsForDisplay_MasksSecretValueContainingQuote()
    {
        // A '"' in the value forces EscapeArgument to quote+escape the whole token; the old regex stopped
        // at the escaped quote and leaked the tail. Build the line exactly as the CLI would display it.
        var line = WindowsCommandLine.JoinArguments(
            ["build", "App.csproj", "-p:PackageCertificatePassword=ab\"cd\"ef", "-c", "Debug"])!;

        var redacted = ProjectRunService.RedactSecretsForDisplay(line);

        Assert.IsFalse(redacted.Contains("ab"), $"no fragment of the secret should survive: {redacted}");
        Assert.IsFalse(redacted.Contains("cd"), $"no fragment of the secret should survive: {redacted}");
        Assert.IsFalse(redacted.Contains("ef"), $"no fragment of the secret should survive: {redacted}");
        StringAssert.Contains(redacted, "PackageCertificatePassword=***");
    }

    [TestMethod]
    public void RedactSecretsForDisplay_MasksSecretValueStartingWithQuote()
    {
        var line = WindowsCommandLine.JoinArguments(["build", "-p:NuGetApiKey=\"quoted\"", "-c", "Debug"])!;

        var redacted = ProjectRunService.RedactSecretsForDisplay(line);

        Assert.IsFalse(redacted.Contains("quoted"), $"secret should be masked: {redacted}");
        StringAssert.Contains(redacted, "NuGetApiKey=***");
    }

    [TestMethod]
    public void RunCommandIsLaunchable_RootedExistingFile_ReturnsTrue()
    {
        // An apphost RunCommand is a rooted path validated with File.Exists; use this assembly as a stand-in.
        var existing = typeof(ProjectRunService).Assembly.Location;

        Assert.IsTrue(ProjectRunService.RunCommandIsLaunchable(existing));
    }

    [TestMethod]
    public void RunCommandIsLaunchable_RootedMissingFile_ReturnsFalse()
    {
        var missing = Path.Combine(_tempDir.FullName, "does-not-exist.exe");

        Assert.IsFalse(ProjectRunService.RunCommandIsLaunchable(missing));
    }

    [TestMethod]
    public void RunCommandIsLaunchable_BareCommandOnPath_ReturnsTrue()
    {
        // UseAppHost=false makes RunCommand a bare command name (e.g. dotnet) resolved via PATH; cmd is
        // always present on Windows PATH via PATHEXT.
        Assert.IsTrue(ProjectRunService.RunCommandIsLaunchable("cmd"));
    }

    [TestMethod]
    public void RunCommandIsLaunchable_BareCommandNotOnPath_ReturnsFalse()
    {
        Assert.IsFalse(ProjectRunService.RunCommandIsLaunchable("winapp-nonexistent-launcher-xyz"));
    }

    [TestMethod]
    public void BuildEvaluateArguments_WithCsWinRTMetadataFolder_InjectsProperty()
    {
        // The evaluate pass must be fed the same inputs as the build pass, so the shim folder flows here too.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);
        var folder = @"C:\cache\microsoft.windows.sdk.net.ref\10.0.26100.57\winmd";

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options, folder);

        StringAssert.Contains(args, $"-p:CsWinRTWindowsMetadata={folder}");
    }

    [TestMethod]
    public void BuildBuildPassArguments_Default_UsesBuildAndRid_NoGetProperty()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.StartsWith(args, "build ");
        StringAssert.Contains(args, "-c Debug");
        StringAssert.Contains(args, "-r win-x64");
        StringAssert.Contains(args, "-v minimal");
        // With no resolved Platform (options.Platform is null), the arg builder conveys arch via the RID
        // alone — it must NOT emit a Platform derived from --arch (injection is decided upstream by
        // ResolvePlatformInjection and only for projects whose <Platforms> closure supports the arch).
        Assert.IsFalse(args.Contains("-p:Platform="), "no resolved Platform → arg builder emits none");
        Assert.IsFalse(args.Contains("EnableDynamicPlatformResolution"), "project mode must not inject EDPR");
        // The build pass must NOT request properties: --getProperty SUPPRESSES MSBuild's console log,
        // which is exactly the streamed output we want the user to see (Change #1). Nor does it need
        // an explicit -t:Build (Build is the default target when no --getProperty is present).
        Assert.IsFalse(args.Contains("--getProperty"), "build pass must not request properties");
        Assert.IsFalse(args.Contains("-t:Build"), "build pass does not need an explicit -t:Build");
    }

    [TestMethod]
    public void BuildBuildPassArguments_Arm64_UsesArmRid_NoForcedPlatform()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Release", "arm64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-c Release");
        StringAssert.Contains(args, "-r win-arm64");
        Assert.IsFalse(args.Contains("-p:Platform="), "no resolved Platform → arch is conveyed by the RID alone");
    }

    [TestMethod]
    public void BuildBuildPassArguments_Verbosity_ForwardedAsDashV()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "normal");

        StringAssert.Contains(args, "-v normal");
    }

    [TestMethod]
    public void BuildBuildPassArguments_RedirectedDefault_PinsTerminalLoggerOff()
    {
        // Redirected launchers (streaming / --json / --quiet) pin -tl:off so the console logger produces
        // clean append-only output and a future SDK can't reintroduce redraw churn under redirection.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-tl:off", "the redirected build pass must pin the terminal logger off");
    }

    [TestMethod]
    public void BuildBuildPassArguments_NativeTerminal_OmitsAnyTerminalLoggerToken()
    {
        // Native-terminal launcher (inherited stdio on a real TTY): omit every -tl token so dotnet's default
        // -tl:auto resolves to ON and its native logger de-duplicates warnings. Absence is the correct
        // default — we must NOT add -tl:on either.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal", csWinRTMetadataFolder: null, nativeTerminal: true);

        Assert.IsFalse(args.Contains("-tl:"), "the native-terminal build pass must omit any -tl token (default auto → on)");
    }

    [TestMethod]
    public void BuildBuildPassArguments_UserPlatformProperty_ForwardedAsIs()
    {
        // A user-supplied -p:Platform flows through the user -p loop and is respected; ResolvePlatformInjection
        // suppresses its own injection when the user set Platform, so options.Platform stays null here and
        // the only -p:Platform present is the user's.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: ["Platform=ARM64"]);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-p:Platform=ARM64");
        Assert.IsFalse(args.Contains("-p:Platform=x64"), "winapp must not inject a Platform derived from --arch");
    }

    [TestMethod]
    public void BuildBuildPassArguments_Default_DoesNotEnableDynamicPlatformResolution()
    {
        // Historically project mode forced -p:Platform=<arch> and then added EDPR to stop that global
        // Platform from breaking P2P references. RID-only removes the forced Platform, so EDPR is no
        // longer needed AND is actively harmful: with a no-<Platforms> WinUI library, the Platform×EDPR
        // split sends the library's XAML/MRT outputs to bin\Debug\ while the app looks in bin\<arch>\
        // → MSB3030/PRI252. So neither is injected by default.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        Assert.IsFalse(args.Contains("EnableDynamicPlatformResolution"), "project mode must not inject EDPR by default");
    }

    [TestMethod]
    public void BuildBuildPassArguments_UserEnableDynamicPlatformResolution_Forwarded()
    {
        // An explicit user value is respected: it flows through the user -p loop and winapp adds no EDPR
        // of its own.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false,
            Properties: ["EnableDynamicPlatformResolution=false"]);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-p:EnableDynamicPlatformResolution=false");
        Assert.IsFalse(args.Contains("-p:EnableDynamicPlatformResolution=true"),
            "winapp must not append its own EnableDynamicPlatformResolution value");
    }

    [TestMethod]
    public void BuildBuildPassArguments_UserProperties_ForwardedToBuild()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: true, Properties: ["WindowsPackageType=None", "Foo=Bar"]);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-p:WindowsPackageType=None");
        StringAssert.Contains(args, "-p:Foo=Bar");
        StringAssert.Contains(args, "--no-restore");
    }

    [TestMethod]
    public void BuildBuildPassArguments_Framework_ForwardedToBuild()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", "net10.0-windows10.0.26100.0", NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-f net10.0-windows10.0.26100.0");
    }

    #endregion

    #region BuildEvaluateArguments (evaluate-only property pass, Change #1)

    [TestMethod]
    public void BuildEvaluateArguments_UsesMsbuildGetProperty_NotBuild()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        StringAssert.StartsWith(args, "msbuild ");
        // dotnet msbuild rejects -c/-r (MSB1001); the evaluate pass must use -p: equivalents and must
        // not build (no -t:Build) — the build pass already produced the output.
        Assert.IsFalse(args.Contains("-t:Build"), "evaluate pass must not build");
        Assert.IsFalse(args.Contains("-c Debug"), "evaluate pass must not pass -c");
        Assert.IsFalse(args.Contains("-r win-x64"), "evaluate pass must not pass -r");
        StringAssert.Contains(args, "-p:Configuration=Debug");
        StringAssert.Contains(args, "-p:RuntimeIdentifier=win-x64");
        Assert.IsFalse(args.Contains("-p:Platform="), "no resolved Platform → evaluate pass emits none");
        Assert.IsFalse(args.Contains("EnableDynamicPlatformResolution"), "evaluate pass must not inject EDPR");
        StringAssert.Contains(args, "--getProperty:TargetDir");
        StringAssert.Contains(args, "--getProperty:RunCommand");
        StringAssert.Contains(args, "--getProperty:WindowsPackageType");
        StringAssert.Contains(args, "--getProperty:OutputType");
    }

    [TestMethod]
    public void BuildEvaluateArguments_IncludePlatformFalse_OmitsPlatform()
    {
        // F10: the injected -p:Platform moves output to bin\<Platform>\<cfg>\<tfm>\, so the --no-build
        // fallback must be able to drop it to find a plain `dotnet build` / VS layout (bin\<cfg>\<tfm>\).
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "arm64", null, NoBuild: true, NoRestore: false, Properties: [])
        {
            Platform = "ARM64"
        };

        var withPlatform = ProjectRunService.BuildEvaluateArguments(csproj, options);
        StringAssert.Contains(withPlatform, "-p:Platform=ARM64", "default evaluate must keep the resolved Platform");

        var withoutPlatform = ProjectRunService.BuildEvaluateArguments(
            csproj, options, includeRuntimeIdentifier: false, includePlatform: false);

        Assert.IsFalse(withoutPlatform.Contains("-p:Platform=", StringComparison.Ordinal),
            "fallback must be able to drop the injected Platform");
        Assert.IsFalse(withoutPlatform.Contains("-p:RuntimeIdentifier=", StringComparison.Ordinal),
            "fallback must be able to drop the injected RID");
        StringAssert.Contains(withoutPlatform, "-p:Configuration=Debug");
        StringAssert.Contains(withoutPlatform, "--getProperty:TargetDir");
    }

    [TestMethod]
    public void BuildEvaluateArguments_IncludeRuntimeIdentifierFalse_OmitsRid()
    {
        // F10: under --no-build the RID-qualified output may not exist (a prior plain VS/dotnet build
        // produced non-RID output), so the fallback re-evaluates WITHOUT the RID to find that TargetDir.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: true, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options, includeRuntimeIdentifier: false);

        Assert.IsFalse(args.Contains("-p:RuntimeIdentifier=", StringComparison.Ordinal),
            "no-RID fallback must not pin a RuntimeIdentifier");
        // Configuration and the getProperty queries are still present so TargetDir still resolves.
        StringAssert.Contains(args, "-p:Configuration=Debug");
        StringAssert.Contains(args, "--getProperty:TargetDir");
    }

    [TestMethod]
    public void BuildEvaluateArguments_Framework_ForwardedAsProperty()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", "net10.0-windows10.0.26100.0", NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        StringAssert.Contains(args, "-p:TargetFramework=net10.0-windows10.0.26100.0");
    }

    [TestMethod]
    public void BuildEvaluateArguments_DedicatedConfigAndRidWinOverUserProperty()
    {
        // The dedicated Configuration/RID own their values on the evaluate pass. A conflicting user -p is
        // FILTERED OUT (see DedicatedFlagProperties) so the evaluate pass can never resolve a different
        // Configuration/RID than the build pass; the dedicated -p: equivalents remain the sole source.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false,
            Properties: ["Configuration=Release", "RuntimeIdentifier=win-arm64"]);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        Assert.IsFalse(args.Contains("-p:Configuration=Release", StringComparison.Ordinal),
            "conflicting user -p:Configuration must be dropped, not forwarded");
        Assert.IsFalse(args.Contains("-p:RuntimeIdentifier=win-arm64", StringComparison.Ordinal),
            "conflicting user -p:RuntimeIdentifier must be dropped, not forwarded");
        StringAssert.Contains(args, "-p:Configuration=Debug");
        StringAssert.Contains(args, "-p:RuntimeIdentifier=win-x64");
    }

    [TestMethod]
    public void BuildEvaluateArguments_CommaPackedDedicatedProperty_IsDropped()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions(
            "Debug",
            "x64",
            null,
            NoBuild: false,
            NoRestore: false,
            Properties: ["Flavor=Retail,RuntimeIdentifier=win-arm64"]);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        Assert.IsFalse(
            args.Contains("Flavor=Retail", StringComparison.Ordinal),
            "the entire packed token must be dropped when any segment conflicts with a dedicated switch");
        Assert.IsFalse(args.Contains("RuntimeIdentifier=win-arm64", StringComparison.Ordinal));
        StringAssert.Contains(args, "-p:RuntimeIdentifier=win-x64");
    }

    [TestMethod]
    public void ResolveExplicitFramework_CommaPackedProperty_PromotesTargetFramework()
    {
        var framework = ProjectRunService.ResolveExplicitFramework(
            frameworkOption: null,
            ["Flavor=Retail,TargetFramework=net10.0-windows10.0.26100.0"]);

        Assert.AreEqual("net10.0-windows10.0.26100.0", framework);
    }

    [TestMethod]
    public void BuildBuildPassArguments_DedicatedConfigAndRidWinOverUserProperty()
    {
        // Mirror of the evaluate-pass test: a user -p that duplicates a dedicated -c/-r switch is dropped
        // from the build pass too, so `-c Debug -p Configuration=Release` builds Debug (the dedicated
        // switch), keeping the build and evaluate passes in lock-step (Copilot review C1).
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false,
            Properties: ["Configuration=Release", "RuntimeIdentifier=win-arm64", "TargetFramework=net10.0-windows10.0.9999.0"]);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        Assert.IsFalse(args.Contains("-p:Configuration=Release", StringComparison.Ordinal),
            "conflicting user -p:Configuration must be dropped from the build pass");
        Assert.IsFalse(args.Contains("-p:RuntimeIdentifier=win-arm64", StringComparison.Ordinal),
            "conflicting user -p:RuntimeIdentifier must be dropped from the build pass");
        Assert.IsFalse(args.Contains("-p:TargetFramework=net10.0-windows10.0.9999.0", StringComparison.Ordinal),
            "conflicting user -p:TargetFramework must be dropped from the build pass");
        // The dedicated -c/-r switches remain authoritative.
        StringAssert.Contains(args, "-c Debug");
        StringAssert.Contains(args, "-r win-x64");
    }

    [TestMethod]
    public void BuildEvaluateArguments_UserPlatformProperty_ForwardedAsIs()
    {
        // A user -p:Platform flows through and is the only Platform present; the resolver leaves
        // options.Platform null when the user set Platform, so no injected Platform is added.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: ["Platform=ARM64"]);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        StringAssert.Contains(args, "-p:Platform=ARM64");
        Assert.IsFalse(args.Contains("-p:Platform=x64"), "winapp must not inject a Platform derived from --arch");
    }

    [TestMethod]
    public void BuildEvaluateArguments_Default_DoesNotEnableDynamicPlatformResolution()
    {
        // RID-only: the evaluate pass mirrors the build pass. Neither a forced Platform nor EDPR is
        // injected, so the evaluated TargetDir/RunCommand resolve against the same RID-driven output
        // paths as the build (bin\Debug\...\win-<arch>\), keeping both passes consistent.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        Assert.IsFalse(args.Contains("EnableDynamicPlatformResolution"), "evaluate pass must not inject EDPR by default");
    }

    [TestMethod]
    public void BuildEvaluateArguments_UserEnableDynamicPlatformResolution_Forwarded()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false,
            Properties: ["EnableDynamicPlatformResolution=false"]);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        StringAssert.Contains(args, "-p:EnableDynamicPlatformResolution=false");
        Assert.IsFalse(args.Contains("-p:EnableDynamicPlatformResolution=true"),
            "winapp must not append its own EnableDynamicPlatformResolution value");
    }

    [TestMethod]
    public void BuildBuildPassArguments_WithSolution_EmitsSolutionProperties()
    {
        // Solution mode resolves a startup .csproj but hands MSBuild the $(SolutionDir) family so the
        // project builds exactly as it does under `dotnet build <sln>` / VS (the AI-Dev-Gallery class
        // of failure where a bare .csproj build has $(SolutionDir) undefined).
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "src", "App", "App.csproj"));
        var solution = new FileInfo(Path.Combine(_tempDir.FullName, "MyApp.sln"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, $"-p:SolutionDir={_tempDir.FullName}\\");
        StringAssert.Contains(args, $"-p:SolutionPath={solution.FullName}");
        StringAssert.Contains(args, "-p:SolutionName=MyApp");
        StringAssert.Contains(args, "-p:SolutionFileName=MyApp.sln");
        StringAssert.Contains(args, "-p:SolutionExt=.sln");
    }

    [TestMethod]
    public void BuildBuildPassArguments_NoSolution_OmitsSolutionProperties()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        Assert.IsFalse(args.Contains("-p:SolutionDir"), "bare .csproj mode must not define solution properties");
    }

    [TestMethod]
    public void BuildBuildPassArguments_UserSolutionDir_NotOverridden()
    {
        // When the user passes their own -p:SolutionDir, winapp must not re-emit a second (winning,
        // last-wins) SolutionDir that clobbers it — the user's value stays authoritative. The sibling
        // Solution* properties are still defined.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "src", "App", "App.csproj"));
        var solution = new FileInfo(Path.Combine(_tempDir.FullName, "MyApp.sln"));
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false,
            Properties: ["SolutionDir=Z:\\Custom\\"], Solution: solution);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-p:SolutionDir=Z:\\Custom\\");
        Assert.IsFalse(
            args.Contains($"-p:SolutionDir={_tempDir.FullName}\\"),
            "winapp must not add a second SolutionDir that overrides the user's value");
        StringAssert.Contains(args, "-p:SolutionName=MyApp");
    }

    [TestMethod]
    public void BuildBuildPassArguments_SolutionDirWithSpecialChars_IsMsBuildEscaped()
    {
        // M3: a legal NTFS path containing ';' or '%' must be MSBuild-escaped in -p:Name=Value so it is
        // not misread as a property separator (';') or escape sequence ('%'). Order matters: '%'→%25
        // first, then ';'→%3B, so "a;b%c" becomes "a%3Bb%25c".
        var solution = new FileInfo(Path.Combine("C:\\", "a;b%c", "MyApp.sln"));
        var csproj = new FileInfo(Path.Combine("C:\\", "a;b%c", "src", "App", "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-p:SolutionDir=C:\\a%3Bb%25c\\", "SolutionDir must be MSBuild-escaped");
        StringAssert.Contains(args, "-p:SolutionName=MyApp", "SolutionName has no special chars and stays literal");
        Assert.IsFalse(args.Contains("-p:SolutionDir=C:\\a;b%c\\"), "the raw unescaped SolutionDir property value must not be emitted");
    }

    [TestMethod]
    public void BuildEvaluateArguments_WithSolution_EmitsSolutionProperties()
    {
        // The evaluate pass must define the same $(SolutionDir) family as the build pass so
        // TargetDir/RunCommand resolve against identical inputs.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "src", "App", "App.csproj"));
        var solution = new FileInfo(Path.Combine(_tempDir.FullName, "MyApp.slnx"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        StringAssert.Contains(args, $"-p:SolutionDir={_tempDir.FullName}\\");
        StringAssert.Contains(args, "-p:SolutionName=MyApp");
        StringAssert.Contains(args, "-p:SolutionFileName=MyApp.slnx");
        StringAssert.Contains(args, "-p:SolutionExt=.slnx");
    }

    #endregion

    #region BuildFrameworkDiscoveryArguments (effective-TFM discovery evaluate, C16)

    [TestMethod]
    public void BuildFrameworkDiscoveryArguments_QueriesTargetFrameworks_WithoutBuildingOrPinningTfmOrRid()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildFrameworkDiscoveryArguments(csproj, options);

        StringAssert.StartsWith(args, "msbuild ");
        StringAssert.Contains(args, "--getProperty:TargetFrameworks");
        StringAssert.Contains(args, "-p:Configuration=Debug");
        Assert.IsFalse(args.Contains("-t:Build"), "the discovery pass must not build");
        // The whole point is to read the OUTER cross-targeting node, so neither a single TFM nor the RID
        // (which does not select the TFM list) may be pinned.
        Assert.IsFalse(args.Contains("-p:TargetFramework=", StringComparison.Ordinal), "must not pin a single TargetFramework");
        Assert.IsFalse(args.Contains("-r win-", StringComparison.Ordinal), "must not pass -r");
        Assert.IsFalse(args.Contains("-p:RuntimeIdentifier=", StringComparison.Ordinal), "must not pin a RID");
    }

    [TestMethod]
    public void BuildFrameworkDiscoveryArguments_RequestsTwoProperties_ToForceJsonEnvelope()
    {
        // C36: a SINGLE --getProperty makes the SDK emit a raw scalar, so MsBuildPropertyReader treats the
        // whole trimmed stdout (including any evaluation warning / diagnostic preamble) as the value — which
        // is then split into a garbage first TFM and passed to -f. Requesting a SECOND property forces the
        // { "Properties": { ... } } JSON envelope, which the reader parses tolerantly. Guard that the default
        // discovery therefore queries BOTH TargetFramework and TargetFrameworks (two --getProperty tokens).
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildFrameworkDiscoveryArguments(csproj, options);

        StringAssert.Contains(args, "--getProperty:TargetFramework ");
        StringAssert.Contains(args, "--getProperty:TargetFrameworks");
        var count = System.Text.RegularExpressions.Regex.Count(args, "--getProperty:");
        Assert.IsTrue(count >= 2, $"discovery must request >=2 properties to force the JSON envelope, but requested {count}");
    }

    [TestMethod]
    public void BuildFrameworkDiscoveryArguments_WithSolution_EmitsSolutionProperties()
    {
        // A Configuration/property-conditional TargetFrameworks list may depend on $(SolutionDir); the
        // discovery evaluate must define the same Solution* family as the real passes so it resolves the
        // identical list.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "src", "App", "App.csproj"));
        var solution = new FileInfo(Path.Combine(_tempDir.FullName, "MyApp.slnx"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        var args = ProjectRunService.BuildFrameworkDiscoveryArguments(csproj, options);

        StringAssert.Contains(args, $"-p:SolutionDir={_tempDir.FullName}\\");
        StringAssert.Contains(args, "-p:SolutionName=MyApp");
    }

    [TestMethod]
    public void BuildFrameworkDiscoveryArguments_ForwardsUserProperties()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: ["MyFlavor=Retail"]);

        var args = ProjectRunService.BuildFrameworkDiscoveryArguments(csproj, options);

        StringAssert.Contains(args, "-p:MyFlavor=Retail");
    }

    #endregion

    #region MatchProjectSelector (--project resolution, M7)

    [TestMethod]
    public void MatchProjectSelector_RelativePath_ResolvesAgainstBaseDir_NotCwd()
    {
        // The project lives under the input/solution dir (_tempDir), which is NOT the process cwd.
        // A relative `--project src\App\App.csproj` must resolve against baseDir so it still matches;
        // resolving against the cwd (the old behavior) would miss it entirely.
        var projectPath = Path.Combine(_tempDir.FullName, "src", "App", "App.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        var projects = new List<FileInfo> { new(projectPath), new(Path.Combine(_tempDir.FullName, "src", "Other", "Other.csproj")) };

        var match = ProjectRunService.MatchProjectSelector(projects, "src\\App\\App.csproj", _tempDir);

        Assert.IsNotNull(match);
        Assert.AreEqual(projectPath, match!.FullName);
    }

    [TestMethod]
    public void MatchProjectSelector_ByProjectName_Matches()
    {
        var appPath = Path.Combine(_tempDir.FullName, "src", "App", "App.csproj");
        var projects = new List<FileInfo> { new(appPath), new(Path.Combine(_tempDir.FullName, "src", "Other", "Other.csproj")) };

        var byFileName = ProjectRunService.MatchProjectSelector(projects, "App.csproj", _tempDir);
        var byBareName = ProjectRunService.MatchProjectSelector(projects, "App", _tempDir);

        Assert.AreEqual(appPath, byFileName!.FullName);
        Assert.AreEqual(appPath, byBareName!.FullName);
    }

    [TestMethod]
    public void MatchProjectSelector_NoMatch_ReturnsNull()
    {
        var projects = new List<FileInfo> { new(Path.Combine(_tempDir.FullName, "App", "App.csproj")) };

        var match = ProjectRunService.MatchProjectSelector(projects, "DoesNotExist", _tempDir);

        Assert.IsNull(match);
    }

    [TestMethod]
    public void MatchProjectSelector_AmbiguousLeafName_ReturnsNull()
    {
        // Two projects with the same leaf file name: a leaf-name selector is ambiguous, so no single
        // match — the caller then errors listing candidates rather than guessing.
        var projects = new List<FileInfo>
        {
            new(Path.Combine(_tempDir.FullName, "a", "App.csproj")),
            new(Path.Combine(_tempDir.FullName, "b", "App.csproj")),
        };

        var match = ProjectRunService.MatchProjectSelector(projects, "App.csproj", _tempDir);

        Assert.IsNull(match);
    }

    [TestMethod]
    public void MatchProjectSelector_UnsupportedPathFormat_ReturnsNull()
    {
        // C31: --project is user input; a value whose format makes Path.GetFullPath throw (e.g. a colon
        // outside a drive prefix, or wildcard/redirection chars illegal in a filename) must resolve to
        // "no match" (null) so the caller emits the normal selector error listing candidates — never leak
        // a raw NotSupportedException/ArgumentException as an internal error. Such a value can never be a
        // legal project filename, so the name/suffix fallback could not have matched it anyway.
        var projects = new List<FileInfo> { new(Path.Combine(_tempDir.FullName, "App", "App.csproj")) };

        foreach (var bad in new[] { "foo:bar:baz", "in|valid", "a<b>c", "quo\"te" })
        {
            var match = ProjectRunService.MatchProjectSelector(projects, bad, _tempDir);
            Assert.IsNull(match, $"selector '{bad}' should resolve to no match, not throw");
        }
    }
    #endregion

    #region TryParseSdkVersion

    [TestMethod]
    [DataRow("8.0.100", 8, 0, 100)]
    [DataRow("10.0.301", 10, 0, 301)]
    [DataRow("8.0.100-preview.1.23456", 8, 0, 100)]
    [DataRow("9.0.203", 9, 0, 203)]
    public void TryParseSdkVersion_ValidVersions_Parsed(string input, int major, int minor, int patch)
    {
        Assert.IsTrue(ProjectRunService.TryParseSdkVersion(input, out var ma, out var mi, out var pa));
        Assert.AreEqual(major, ma);
        Assert.AreEqual(minor, mi);
        Assert.AreEqual(patch, pa);
    }

    [TestMethod]
    [DataRow("abc")]
    [DataRow("8.0")]
    [DataRow("")]
    public void TryParseSdkVersion_Invalid_ReturnsFalse(string input)
    {
        Assert.IsFalse(ProjectRunService.TryParseSdkVersion(input, out _, out _, out _));
    }

    #endregion

    #region ResolveInput

    [TestMethod]
    public async Task ResolveInput_CsprojFile_ReturnsProjectMode()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(csproj.FullName, resolution.Csproj!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_ExplicitCsproj_MatchingProjectSelector_Honored()
    {
        // M3: --project that names the same explicit .csproj is a harmless no-op, not an error.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None, projectSelector: "App");

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(csproj.FullName, resolution.Csproj!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_ExplicitCsproj_MismatchedProjectSelector_Throws()
    {
        // M3: a --project that disagrees with the explicit .csproj must error rather than be silently dropped.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);

        await Assert.ThrowsExactlyAsync<ProjectRunException>(
            () => _service.ResolveInputAsync(csproj, CancellationToken.None, projectSelector: "Other"));
    }

    [TestMethod]
    public async Task ResolveInput_CsprojFile_OwningSolutionOneLevelUp_AttachesSolution()
    {
        // A bare .csproj input must discover its owning solution (walking up) so $(SolutionDir) is
        // defined for the build — the AI-Dev-Gallery/Richasy class where a project imports shared props
        // via $(SolutionDir). The solution sits above the project and lists it.
        var csproj = WriteFileAt(Path.Combine("src", "App", "App.csproj"), ExecutableCsproj);
        var solution = WriteFile("MyApp.sln", SlnListing(Path.Combine("src", "App", "App.csproj")));

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.IsNotNull(resolution.Solution, "the owning solution must be discovered so $(SolutionDir) is defined");
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_CsprojFile_OwningSlnxListsProject_AttachesSolution()
    {
        // Same discovery for the newer XML .slnx format (forward-slash project paths).
        var csproj = WriteFileAt(Path.Combine("src", "App", "App.csproj"), ExecutableCsproj);
        var solution = WriteFile("MyApp.slnx", SlnxListing("src/App/App.csproj"));

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None);

        Assert.IsNotNull(resolution.Solution);
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_CsprojFile_PrefersSolutionThatListsProject()
    {
        // Two solutions at the same level: the one that actually lists the project wins over the one
        // that doesn't (so we attach the real owner, not an unrelated sibling solution).
        var csproj = WriteFileAt(Path.Combine("src", "App", "App.csproj"), ExecutableCsproj);
        WriteFile("Unrelated.sln", SlnListing(Path.Combine("src", "Other", "Other.csproj")));
        var owner = WriteFile("Owner.sln", SlnListing(Path.Combine("src", "App", "App.csproj")));

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None);

        Assert.IsNotNull(resolution.Solution);
        Assert.AreEqual(owner.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_CsprojFile_MultipleSolutionsNoneList_LeavesSolutionNull()
    {
        // Several solutions at the nearest ancestor, none of which lists the project → we don't guess.
        var csproj = WriteFileAt(Path.Combine("src", "App", "App.csproj"), ExecutableCsproj);
        WriteFile("One.sln", SlnListing(Path.Combine("src", "Other", "Other.csproj")));
        WriteFile("Two.sln", SlnListing(Path.Combine("src", "Another", "Another.csproj")));

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.IsNull(resolution.Solution, "ambiguous solutions that don't list the project must not be guessed");
    }

    [TestMethod]
    public async Task ResolveInput_CsprojFile_SingleSolutionNotListing_AttachesByLocality()
    {
        // Exactly one solution at the nearest ancestor: attach it even when we can't confirm the listing
        // (e.g. an as-yet-empty/opaque solution), matching how a developer opens that lone solution in VS.
        var csproj = WriteFileAt(Path.Combine("src", "App", "App.csproj"), ExecutableCsproj);
        var solution = WriteFile("Lonely.sln", "");

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None);

        Assert.IsNotNull(resolution.Solution);
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_CsprojFile_SingleSolutionConfirmsAbsent_LeavesSolutionNull()
    {
        // C26: exactly one solution at the nearest ancestor, readable and listing OTHER projects but NOT this
        // one → it confirms the project is absent. We must not fabricate ownership by locality (that would
        // inject an unrelated $(SolutionDir)/Solution* set and restore unrelated siblings, changing explicit
        // .csproj semantics); solution context stays null.
        var csproj = WriteFileAt(Path.Combine("src", "App", "App.csproj"), ExecutableCsproj);
        WriteFile("Other.sln", SlnListing(Path.Combine("src", "Other", "Other.csproj")));

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.IsNull(resolution.Solution, "a lone solution that confirms the project is absent must not be attached by locality");
    }

    [TestMethod]
    public async Task ResolveInput_NonCsprojFile_Throws()
    {
        var txt = WriteFile("readme.txt", "hello");

        await Assert.ThrowsExactlyAsync<ProjectRunException>(() => _service.ResolveInputAsync(txt, CancellationToken.None));
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithNoCsproj_ReturnsFolderMode()
    {
        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Folder, resolution.Mode);
        Assert.IsNull(resolution.Csproj);
    }

    [TestMethod]
    public async Task FolderMode_NeverResolvesProject_SoProjectBuildArgsCannotLeak()
    {
        // Folder mode must stay byte-identical: a folder without a top-level .csproj routes to folder
        // mode, which never resolves a project and never invokes the project-mode build. The
        // project-mode build args — including the RID-driven arch conveyance — are emitted ONLY by
        // BuildBuildPassArguments/BuildEvaluateArguments, both of which are unreachable in folder mode.
        // This guards against those args ever leaking into a folder-mode run.
        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Folder, resolution.Mode, "a manifest/output folder must route to folder mode");
        Assert.IsNull(resolution.Csproj, "folder mode must not resolve a project to build (so no project-mode build args)");
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithSingleCsproj_ReturnsProjectMode()
    {
        WriteFile("App.csproj", ExecutableCsproj);

        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithSingleNonRunnableCsproj_ReturnsFolderMode()
    {
        // Copilot review (ProjectRunService.Input.cs:106): a directory whose only top-level project is a
        // non-runnable library (e.g. a lib copied beside build output) must NOT auto-switch to project
        // mode — folder mode is preserved unchanged (G4). Classification static-parses Library → NotRunnable.
        WriteFile("Lib.csproj", LibraryCsproj);

        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Folder, resolution.Mode, "a lone non-runnable library must stay in folder mode");
        Assert.IsNull(resolution.Csproj);
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithSingleTestCsproj_ReturnsProjectMode_LoneTest()
    {
        // The lone-non-runnable folder fallback must retain the lone-test convenience: a directory whose
        // only project is a runnable test project still enters project mode (matching the multi/solution
        // PickRunnableProject behavior).
        WriteFile("App.Tests.csproj", TestProjectCsproj);

        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.Tests.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithLoneProject_ClassifiesWithOwningSolutionContext()
    {
        // M1: a lone project in a directory (no sibling .sln) whose runnability is conditional on
        // $(SolutionDir) must be classified WITH the owning solution's context — the solution is now
        // resolved BEFORE classification, not attached afterward. Here the evaluate reports a runnable
        // Exe only when the SolutionDir token is present; classifying solution-less (the old behavior)
        // would see Library and wrongly fall back to folder mode.
        var solution = WriteFile("App.sln", SlnListing(@"src\App.csproj"));
        var project = WriteFileAt(@"src\App.csproj", NoInlineOutputTypeCsproj);
        var projectDir = project.Directory!;
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.Contains("SolutionDir", StringComparison.OrdinalIgnoreCase)
                    ? (0, EvalJson("Exe"), string.Empty)
                    : (0, EvalJson("Library"), string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(projectDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(project.FullName, resolution.Csproj!.FullName);
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithSingleNonRunnableCsproj_ExplicitProject_ReturnsProjectMode()
    {
        // An explicit --project selector is honored as-is even for a non-runnable project (the user asked
        // for it), matching the multi-.csproj --project path — the runnability gate only applies to
        // auto-selection.
        WriteFile("Lib.csproj", LibraryCsproj);

        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None, projectSelector: "Lib");

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("Lib.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_SingleExecutable_PicksExecutable()
    {
        // With no canned evaluation the classifier falls back to the static parse, which reads the
        // inline OutputType of these fixtures: App=WinExe (executable), Lib=Library (not).
        WriteFile("App.csproj", ExecutableCsproj);
        WriteFile("Lib.csproj", LibraryCsproj);

        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleExecutableCsproj_ThrowsAmbiguity()
    {
        WriteFile("App1.csproj", ExecutableCsproj);
        WriteFile("App2.csproj", ExecutableCsproj);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => _service.ResolveInputAsync(_tempDir, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Multiple .csproj files");
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_ExecutablePlusTestProject_PicksExecutable()
    {
        // A test project (IsTestProject=true) is excluded from the executable set even when its
        // OutputType is Exe, so an app + its test project disambiguates to the app.
        WriteFile("App.csproj", ExecutableCsproj);
        WriteFile("App.Tests.csproj", TestProjectCsproj);

        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_NoExecutable_ThrowsAmbiguity()
    {
        // Multiple projects, none statically executable → we cannot pick one; guide the user to
        // name a project explicitly rather than silently building a non-runnable one.
        WriteFile("Lib1.csproj", LibraryCsproj);
        WriteFile("Lib2.csproj", LibraryCsproj);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => _service.ResolveInputAsync(_tempDir, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Multiple .csproj files");
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_EvaluationDetectsExecutableFromImport_DoesNotSilentlyPickWrongProject()
    {
        // Spec M5: App.csproj gets its OutputType from an import (nothing inline) while Tool.csproj
        // declares OutputType=Exe inline. A STATIC parse sees only Tool as executable and would
        // silently build+run Tool. MSBuild evaluation reveals BOTH are runnable → ambiguity error,
        // so the wrong project is never launched behind the user's back.
        var app = WriteFile("App.csproj", NoInlineOutputTypeCsproj);
        var tool = WriteFile("Tool.csproj", InlineExeNoTestFlagCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("WinExe"), string.Empty)
                : args.Contains(tool.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("Exe"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.ResolveInputAsync(_tempDir, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Multiple .csproj files");
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_EvaluationDetectsTestFromImport_PicksApp()
    {
        // Spec M5: App.Tests.csproj declares OutputType=Exe inline but no inline IsTestProject (the
        // test SDK sets it via an import). A STATIC parse would treat BOTH App and App.Tests as
        // executable → ambiguity. MSBuild evaluation reveals App.Tests is a test project, so the app
        // is correctly and unambiguously selected.
        var app = WriteFile("App.csproj", ExecutableCsproj);
        var tests = WriteFile("App.Tests.csproj", InlineExeNoTestFlagCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.Contains(tests.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("Exe", isTestProject: "true"), string.Empty)
                : args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("WinExe"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_ClassificationThreadsEffectiveConfigurationRidAndUserProperty()
    {
        // Copilot review (RunCommand.cs:390): candidate classification must evaluate under the SAME
        // effective build inputs the subsequent build uses. Capture the classify commands and assert the
        // threaded Configuration/RID/user -p reach every one, so a project whose OutputType/test markers
        // are conditional on them is classified the way it will build (e.g. `run App.sln -c Release`).
        WriteFile("App.csproj", ExecutableCsproj);
        WriteFile("Lib.csproj", ExecutableCsproj);
        var commandArgs = new List<string>();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args => { commandArgs.Add(args); return (0, EvalJson("Library"), string.Empty); },
        };
        var service = NewServiceWith(dotnet, out _);
        var classificationInputs = new ProjectClassificationInputs("Release", "arm64", Framework: null, Properties: ["Foo=Bar"]);

        // Both classify as Library → no runnable app → the ambiguity path; we only assert the args here.
        await Assert.ThrowsExactlyAsync<ProjectRunException>(() =>
            service.ResolveInputAsync(_tempDir, CancellationToken.None, projectSelector: null, classificationInputs));

        Assert.IsTrue(commandArgs.Count > 0, "expected classification to evaluate at least one project");
        Assert.IsTrue(commandArgs.All(a => a.Contains("-p:Configuration=Release", StringComparison.Ordinal)),
            $"classify command missing -p:Configuration=Release: {string.Join(" | ", commandArgs)}");
        Assert.IsTrue(commandArgs.All(a => a.Contains("-p:RuntimeIdentifier=win-arm64", StringComparison.Ordinal)),
            $"classify command missing -p:RuntimeIdentifier=win-arm64: {string.Join(" | ", commandArgs)}");
        Assert.IsTrue(commandArgs.All(a => a.Contains("-p:Foo=Bar", StringComparison.Ordinal)),
            $"forwardable user -p should reach classification: {string.Join(" | ", commandArgs)}");
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_ConditionalOutputType_ClassifiesUnderEffectiveConfiguration()
    {
        // A candidate whose OutputType is conditional on Configuration (e.g.
        // `<OutputType Condition="'$(Configuration)'=='Release'">WinExe</OutputType>`): App is WinExe
        // under Release but a Library otherwise. With the effective -c Release threaded into
        // classification, App is correctly the single runnable app.
        var app = WriteFile("App.csproj", ExecutableCsproj);
        WriteFile("Lib.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                !args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("Library"), string.Empty)
                : args.Contains("-p:Configuration=Release", StringComparison.Ordinal) ? (0, EvalJson("WinExe"), string.Empty)
                : (0, EvalJson("Library"), string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);
        var releaseInputs = new ProjectClassificationInputs("Release", "x64", Framework: null, Properties: []);

        var resolution = await service.ResolveInputAsync(_tempDir, CancellationToken.None, projectSelector: null, releaseInputs);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_ConditionalOutputType_DefaultConfigurationMisses()
    {
        // Negative control for the test above: the same App is only WinExe under Release. With no
        // classification inputs (the prior behavior — MSBuild defaults), it evaluates as a Library, so
        // NO runnable app is found and resolution requires explicit selection. This proves threading the
        // effective Configuration is load-bearing, not incidental.
        var app = WriteFile("App.csproj", ExecutableCsproj);
        WriteFile("Lib.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                !args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("Library"), string.Empty)
                : args.Contains("-p:Configuration=Release", StringComparison.Ordinal) ? (0, EvalJson("WinExe"), string.Empty)
                : (0, EvalJson("Library"), string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() =>
            service.ResolveInputAsync(_tempDir, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Multiple .csproj files");
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_MultiTargetedConditionalOutputType_ClassifiesUnderFirstTfm()
    {
        // Regression (Copilot review, ProjectDetectionService.cs:324): a multi-targeted candidate whose
        // executable OutputType is conditional on $(TargetFramework) (WinExe only for the first TFM,
        // Library on the cross-targeting outer node). With no --framework, classification must pin the SAME
        // effective first TFM the build/evaluate passes use — otherwise App evaluates on the empty outer
        // node, looks non-runnable, and auto-selection fails BEFORE the build. First TFM here is
        // net10.0-windows10.0.26100.0 (static inline <TargetFrameworks>).
        var app = WriteFile("App.csproj", MultiTargetedExeCsproj);
        WriteFile("Lib.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                !args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("Library"), string.Empty)
                : args.Contains("-p:TargetFramework=net10.0-windows10.0.26100.0", StringComparison.Ordinal) ? (0, EvalJson("WinExe"), string.Empty)
                : (0, EvalJson("Library"), string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);
        var inputs = new ProjectClassificationInputs("Debug", "x64", Framework: null, Properties: []);

        var resolution = await service.ResolveInputAsync(_tempDir, CancellationToken.None, projectSelector: null, inputs);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_MultiTargetedConditionalOutputType_NoTfmPinnedMisses()
    {
        // Negative control for the test above: the same App is WinExe only when the classify evaluate pins
        // the first TFM. With no classification inputs (prior behavior — MSBuild defaults, outer node), App
        // reads as a Library, so NO runnable app is found. Proves injecting the effective first TFM into
        // classification is load-bearing, not incidental.
        var app = WriteFile("App.csproj", MultiTargetedExeCsproj);
        WriteFile("Lib.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                !args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("Library"), string.Empty)
                : args.Contains("-p:TargetFramework=net10.0-windows10.0.26100.0", StringComparison.Ordinal) ? (0, EvalJson("WinExe"), string.Empty)
                : (0, EvalJson("Library"), string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() =>
            service.ResolveInputAsync(_tempDir, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Multiple .csproj files");
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_TestContainerCapability_PicksApp()
    {
        // The AI Dev Gallery / WinUI Gallery shape: the test project is itself a packaged WinUI app
        // (WinExe, EnableMsixTooling) that never sets IsTestProject, so OutputType alone can't tell it
        // apart from the real app. The VS `TestContainer` project capability marks it as a test, so the
        // real app is selected without an explicit --project.
        var app = WriteFile("Gallery.csproj", ExecutableCsproj);
        var tests = WriteFile("Gallery.Tests.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.Contains(tests.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("WinExe", capabilities: ["TestContainer"]), string.Empty)
                : args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("WinExe"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("Gallery.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_TestFrameworkPackageReference_PicksApp()
    {
        // Same shape as above but detected via a test-framework PackageReference (MSTest.TestAdapter),
        // since WinUI MSTest projects reference MSTest.* / Microsoft.TestPlatform.TestHost yet omit
        // Microsoft.NET.Test.Sdk (which is what would set IsTestProject).
        var app = WriteFile("Gallery.csproj", ExecutableCsproj);
        var tests = WriteFile("Gallery.Tests.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.Contains(tests.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("WinExe", packageReferences: ["MSTest.TestAdapter", "MSTest.TestFramework"]), string.Empty)
                : args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("WinExe", packageReferences: ["CommunityToolkit.WinUI"]), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("Gallery.csproj", resolution.Csproj!.Name);
    }

    private static string EvalJson(string outputType, string isTestProject = "") =>
        $$"""{ "Properties": { "OutputType": "{{outputType}}", "IsTestProject": "{{isTestProject}}" } }""";

    // Adds an "Items" object alongside "Properties" so the evaluated classifier can see a project's
    // ProjectCapability / PackageReference items — the markers that identify a WinUI MSTest app that is
    // a WinExe with no IsTestProject (AI Dev Gallery / WinUI Gallery test project shape).
    private static string EvalJsonWithItems(
        string outputType,
        string[]? capabilities = null,
        string[]? packageReferences = null,
        string isTestProject = "")
    {
        static string ItemArray(string[]? ids) =>
            "[ " + string.Join(", ", (ids ?? []).Select(id => $$"""{ "Identity": "{{id}}" }""")) + " ]";

        return $$"""
            { "Properties": { "OutputType": "{{outputType}}", "IsTestProject": "{{isTestProject}}" },
              "Items": { "ProjectCapability": {{ItemArray(capabilities)}}, "PackageReference": {{ItemArray(packageReferences)}} } }
            """;
    }

    // Reproduces `dotnet sln <sln> list` output: a "Project(s)" header, a dashed underline, then one
    // project path per line (relative to the solution directory).
    private static string SlnListOutput(params string[] relativePaths) =>
        "Project(s)" + Environment.NewLine + "----------" + Environment.NewLine +
        string.Join(Environment.NewLine, relativePaths);

    private static bool IsSlnListCall(string args) =>
        args.Contains("sln", StringComparison.OrdinalIgnoreCase) && args.Contains("list", StringComparison.OrdinalIgnoreCase);

    [TestMethod]
    public async Task ResolveInput_SolutionFile_SingleExecutable_ReturnsProjectModeWithSolution()
    {
        var solution = WriteFile("MyApp.sln", "");
        var app = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            // sln list → the one project; any eval falls through to the static parse (App=WinExe).
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("App.csproj"), string.Empty) : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(solution, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(app.FullName, resolution.Csproj!.FullName);
        Assert.IsNotNull(resolution.Solution, "solution mode must record the solution so $(SolutionDir) is defined");
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_SlnxFile_ResolvesLocally_WithoutDotnetSlnList()
    {
        // C18: `dotnet sln list` only understands .slnx on SDK 9.0.200+, but our floor is 8.0.100. A .slnx
        // must be enumerated by the local XML parser instead, so a valid .slnx resolves on the 8.0.100 floor.
        var solution = WriteFile("MyApp.slnx", SlnxListing("App.csproj"));
        WriteFile("App.csproj", ExecutableCsproj);
        var slnListCalled = false;
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
            {
                if (IsSlnListCall(args))
                {
                    // A .slnx must never be handed to `dotnet sln list`; fail loudly if it is.
                    slnListCalled = true;
                    return (1, string.Empty, "unexpected 'dotnet sln list' for a .slnx");
                }
                return (0, string.Empty, string.Empty);
            },
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(solution, CancellationToken.None);

        Assert.IsFalse(slnListCalled, "a .slnx must be parsed locally, never via 'dotnet sln list'");
        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(Path.Combine(_tempDir.FullName, "App.csproj"), resolution.Csproj!.FullName);
        Assert.IsNotNull(resolution.Solution, "solution mode must record the .slnx so $(SolutionDir) is defined");
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithSolution_PrefersSolutionOverLooseCsproj()
    {
        WriteFile("MyApp.sln", "");
        WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("App.csproj"), string.Empty) : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.IsNotNull(resolution.Solution, "a solution in the directory must be preferred over loose .csproj files");
    }

    [TestMethod]
    public async Task ResolveInput_MultipleSolutions_ThrowsAmbiguity()
    {
        WriteFile("One.sln", "");
        WriteFile("Two.sln", "");

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => _service.ResolveInputAsync(_tempDir, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Multiple solution files");
    }

    [TestMethod]
    public async Task ResolveInput_Solution_MultipleExecutables_RequiresProjectSelector()
    {
        var solution = WriteFile("MyApp.sln", "");
        var app1 = WriteFile("App1.csproj", ExecutableCsproj);
        var app2 = WriteFile("App2.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("App1.csproj", "App2.csproj"), string.Empty)
                : args.Contains(app1.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("WinExe"), string.Empty)
                : args.Contains(app2.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("WinExe"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.ResolveInputAsync(solution, CancellationToken.None));
        StringAssert.Contains(ex.Message, "--project");
    }

    [TestMethod]
    public async Task ResolveInput_Solution_ProjectSelector_PicksMatch()
    {
        var solution = WriteFile("MyApp.sln", "");
        WriteFile("App1.csproj", ExecutableCsproj);
        var app2 = WriteFile("App2.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            // The selector short-circuits classification, so only the sln list call is needed.
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("App1.csproj", "App2.csproj"), string.Empty) : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(solution, CancellationToken.None, "App2");

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(app2.FullName, resolution.Csproj!.FullName);
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_Solution_CancellationDuringListing_RethrowsInsteadOfReadFailure()
    {
        // C7: a caller-requested cancellation during `dotnet sln list` must surface as
        // OperationCanceledException, not be swallowed and reported as an unreadable solution.
        var solution = WriteFile("MyApp.sln", "");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? throw new OperationCanceledException() : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.ResolveInputAsync(solution, cts.Token));
    }

    [TestMethod]
    public void MatchProjectSelector_RelativeLeaf_Matches()
    {
        var baseDir = _tempDir;
        var app = new FileInfo(Path.Combine(_tempDir.FullName, "src", "App", "App.csproj"));
        var other = new FileInfo(Path.Combine(_tempDir.FullName, "src", "Lib", "Lib.csproj"));

        var match = ProjectRunService.MatchProjectSelector(new[] { app, other }, "App", baseDir);

        Assert.IsNotNull(match);
        Assert.AreEqual(app.FullName, match!.FullName);
    }

    [TestMethod]
    public void MatchProjectSelector_FullyQualifiedWrongPath_DoesNotFallBackToName()
    {
        // A fully qualified selector that points at a location no project occupies must NOT silently
        // match a same-named project elsewhere in the solution.
        var baseDir = _tempDir;
        var app = new FileInfo(Path.Combine(_tempDir.FullName, "src", "App", "App.csproj"));
        var wrong = Path.Combine(_tempDir.FullName, "somewhere", "else", "App.csproj");

        var match = ProjectRunService.MatchProjectSelector(new[] { app }, wrong, baseDir);

        Assert.IsNull(match);
    }

    [TestMethod]
    public void MatchProjectSelector_FullyQualifiedCorrectPath_Matches()
    {
        var baseDir = _tempDir;
        var app = new FileInfo(Path.Combine(_tempDir.FullName, "src", "App", "App.csproj"));

        var match = ProjectRunService.MatchProjectSelector(new[] { app }, app.FullName, baseDir);

        Assert.IsNotNull(match);
        Assert.AreEqual(app.FullName, match!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_Solution_NoCsprojProjects_Throws()
    {
        var solution = WriteFile("Native.sln", "");
        var dotnet = new FakeDotNetService
        {
            // Only a C++ project → filtered out because `winapp run` builds managed app projects.
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("Native.vcxproj"), string.Empty) : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.ResolveInputAsync(solution, CancellationToken.None));
        StringAssert.Contains(ex.Message, "No .csproj projects");
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithSolution_NoCsprojProjects_FallsBackToFolderMode()
    {
        // A directory input whose sole solution lists no runnable C# project (native-only) must degrade
        // to folder mode — the same fallback a lone non-runnable .csproj gets — so an existing
        // folder-mode/build-output target that happens to sit next to such a solution keeps working.
        // (Contrast ResolveInput_Solution_NoCsprojProjects_Throws: an *explicitly* supplied .sln errors.)
        WriteFile("Native.sln", "");
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("Native.vcxproj"), string.Empty) : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Folder, resolution.Mode, "a directory whose discovered solution has no runnable project must fall back to folder mode");
        Assert.IsNull(resolution.Csproj);
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithSolution_OnlyLibraryProjects_FallsBackToFolderMode()
    {
        // Same fallback when the discovered solution lists only a non-runnable library .csproj: the
        // directory input degrades to folder mode rather than erroring.
        WriteFile("Lib.sln", "");
        var lib = WriteFile("Lib.csproj", LibraryCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("Lib.csproj"), string.Empty)
                : args.Contains(lib.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("Library"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Folder, resolution.Mode, "a directory whose discovered solution has only a non-runnable library must fall back to folder mode");
        Assert.IsNull(resolution.Csproj);
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithSolution_SdkUnavailable_Throws()
    {
        // M4: a discovered .sln always surfaces the actionable SDK error — even for a directory input.
        // A solution exists to be built, so masking a missing/too-old SDK as folder mode would hide the
        // real problem. (Folder fallback only covers a solution with no runnable .csproj — see below.)
        WriteFile("App.sln", "");
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.Contains("--version", StringComparison.OrdinalIgnoreCase)
                    ? (1, string.Empty, "SDK missing")
                    : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.ResolveInputAsync(_tempDir, CancellationToken.None));
    }

    [TestMethod]
    public async Task ResolveInput_ExplicitSolution_SdkUnavailable_Throws()
    {
        // Contrast with the directory case: an explicitly supplied .sln keeps the hard, actionable SDK
        // error — its whole purpose is to build.
        var solution = WriteFile("App.sln", "");
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.Contains("--version", StringComparison.OrdinalIgnoreCase)
                    ? (1, string.Empty, "SDK missing")
                    : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.ResolveInputAsync(solution, CancellationToken.None));
    }

    [TestMethod]
    public async Task ResolveInput_Solution_AppPlusTestContainer_PicksApp()
    {
        // The real gallery scenario at the solution level: a solution with the app plus a WinUI MSTest
        // project (WinExe + TestContainer capability, no IsTestProject). The app is auto-selected — no
        // ambiguity error and no --project needed.
        var solution = WriteFile("Gallery.sln", "");
        var app = WriteFile("Gallery.csproj", ExecutableCsproj);
        var tests = WriteFile("Gallery.Tests.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("Gallery.csproj", "Gallery.Tests.csproj"), string.Empty)
                : args.Contains(tests.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("WinExe", capabilities: ["TestContainer"]), string.Empty)
                : args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("WinExe"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(solution, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(app.FullName, resolution.Csproj!.FullName);
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_Solution_OnlyTestProject_RunsIt()
    {
        // A solution whose only runnable project is a test project (no real app) still runs — a
        // tests-only solution is a legitimate `winapp run` target. We note that we're running a test.
        var solution = WriteFile("Tests.sln", "");
        var tests = WriteFile("App.Tests.csproj", ExecutableCsproj);
        var lib = WriteFile("App.csproj", LibraryCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("App.csproj", "App.Tests.csproj"), string.Empty)
                : args.Contains(tests.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("Exe", capabilities: ["TestContainer"]), string.Empty)
                : args.Contains(lib.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("Library"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, LogLevel.Information, out var console);

        var resolution = await service.ResolveInputAsync(solution, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(tests.FullName, resolution.Csproj!.FullName);
        StringAssert.Contains(console.Output, "test project");
    }

    [TestMethod]
    [DataRow(LogLevel.Warning, DisplayName = "--quiet (Warning) suppresses the lone-test note")]
    [DataRow(LogLevel.None, DisplayName = "--json (None) suppresses the lone-test note")]
    public async Task ResolveInput_Solution_OnlyTestProject_NoteSuppressedAboveInformation(LogLevel minLevel)
    {
        // H2: the lone-test courtesy note runs during input resolution, ahead of the command's own
        // output-mode gating, so it must self-gate on the logger level. Under --quiet (Warning) and
        // --json (None) the note must NOT reach the console — for --json a stray write ahead of the
        // envelope corrupts stdout into invalid JSON.
        var solution = WriteFile("Tests.sln", "");
        var tests = WriteFile("App.Tests.csproj", ExecutableCsproj);
        var lib = WriteFile("App.csproj", LibraryCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("App.csproj", "App.Tests.csproj"), string.Empty)
                : args.Contains(tests.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("Exe", capabilities: ["TestContainer"]), string.Empty)
                : args.Contains(lib.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("Library"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, minLevel, out var console);

        var resolution = await service.ResolveInputAsync(solution, CancellationToken.None);

        // The project still resolves to the lone test project — only the human note is gated.
        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(tests.FullName, resolution.Csproj!.FullName);
        Assert.AreEqual(string.Empty, console.Output, "the lone-test note must be suppressed above Information (--quiet / --json).");
    }

    [TestMethod]
    public async Task ResolveInput_Solution_OnlyMultipleTestProjects_RequiresProjectSelector()
    {
        // Several test projects and no app → we can't guess which test host to launch; require --project
        // and say the solution contains only test projects so the message is actionable.
        var solution = WriteFile("Tests.sln", "");
        var t1 = WriteFile("A.Tests.csproj", ExecutableCsproj);
        var t2 = WriteFile("B.Tests.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("A.Tests.csproj", "B.Tests.csproj"), string.Empty)
                : args.Contains(t1.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("Exe", capabilities: ["TestContainer"]), string.Empty)
                : args.Contains(t2.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("Exe", capabilities: ["TestContainer"]), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.ResolveInputAsync(solution, CancellationToken.None));
        StringAssert.Contains(ex.Message, "only test projects");
        StringAssert.Contains(ex.Message, "--project");
    }

    [TestMethod]
    public async Task ResolveInput_Solution_ProjectSelectorPicksTestProject_NoFiltering()
    {
        // An explicit --project selector is always honored, even when it names a test project: the user
        // asked for it, so no test filtering and no evaluation is applied.
        var solution = WriteFile("Gallery.sln", "");
        WriteFile("Gallery.csproj", ExecutableCsproj);
        var tests = WriteFile("Gallery.Tests.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("Gallery.csproj", "Gallery.Tests.csproj"), string.Empty) : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(solution, CancellationToken.None, "Gallery.Tests");

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(tests.FullName, resolution.Csproj!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_Solution_SlnListFails_Throws()
    {
        var solution = WriteFile("Broken.sln", "");
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (1, string.Empty, "MSB1234: invalid solution") : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.ResolveInputAsync(solution, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Broken.sln");
    }

    #endregion

    #region BuildAndResolveAsync (--json banner suppression)

    private static ProjectRunService NewServiceWith(FakeDotNetService dotnet, out TestConsole console)
        => NewServiceWith(dotnet, new FakeCsWinRTMetadataShimService(), out console);

    private static ProjectRunService NewServiceWith(FakeDotNetService dotnet, FakeCsWinRTMetadataShimService shim, out TestConsole console)
    {
        console = new TestConsole();
        return new ProjectRunService(dotnet, NewDetection(dotnet), shim, console, NullLogger<ProjectRunService>.Instance);
    }

    private static ProjectRunService NewServiceWith(FakeDotNetService dotnet, LogLevel minLevel, out TestConsole console)
    {
        console = new TestConsole();
        return new ProjectRunService(dotnet, NewDetection(dotnet), new FakeCsWinRTMetadataShimService(), console, new LevelLogger<ProjectRunService>(minLevel));
    }

    private string PackagedPropertiesJson() =>
        // TargetDir must be non-empty and the packaging must resolve to Packaged (WindowsPackageType=MSIX)
        // so BuildAndResolveAsync succeeds without needing a real apphost .exe on disk.
        $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "MSIX", "OutputType": "WinExe", "WindowsAppSDKSelfContained": "" } }""";

    [TestMethod]
    public async Task BuildAndResolveAsync_ShimResolvesFolder_InjectsMetadataIntoBuildPass()
    {
        // SHIM threading: when the shim resolves a folder (SDK absent), the build pass args must carry
        // -p:CsWinRTWindowsMetadata and the shim must be consulted with the project's target framework.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var folder = @"C:\cache\microsoft.windows.sdk.net.ref\10.0.26100.57\winmd";
        var shim = new FakeCsWinRTMetadataShimService { FolderToReturn = folder };
        var service = NewServiceWith(dotnet, shim, out _);
        var options = new ProjectRunOptions("Debug", "x64", "net10.0-windows10.0.26100.0", NoBuild: false, NoRestore: false, Properties: []);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(1, dotnet.StreamingCalls.Count, "exactly one build pass should have run");
        StringAssert.Contains(dotnet.StreamingCalls[0], $"-p:CsWinRTWindowsMetadata={folder}");
        Assert.AreEqual(1, shim.ResolvedMonikers.Count, "the shim should be consulted once");
        Assert.AreEqual("net10.0-windows10.0.26100.0", shim.ResolvedMonikers[0],
            "the shim must be consulted with the project's target framework moniker");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NoFrameworkFlag_ShimGetsProjectSingleTfm()
    {
        // C32 regression: with NO --framework on a single-targeted project (options.Framework is null),
        // the shim must STILL be consulted with the project's own <TargetFramework> — not null — so on an
        // SDK-less host it prefers the ref pack matching the project's TargetPlatformVersion instead of the
        // highest cached one (e.g. building a 10.0.19041 project against 10.0.26100 metadata). Previously
        // the null options.Framework was passed straight through to the shim.
        var csproj = WriteFile("App.csproj", ExecutableCsproj); // <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
        var folder = @"C:\cache\microsoft.windows.sdk.net.ref\10.0.26100.57\winmd";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var shim = new FakeCsWinRTMetadataShimService { WindowsSdkAbsent = true, FolderToReturn = folder };
        var service = NewServiceWith(dotnet, shim, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.IsTrue(shim.ResolvedMonikers.Count >= 1, "the shim should be consulted");
        Assert.AreEqual("net10.0-windows10.0.26100.0", shim.ResolvedMonikers[0],
            "the shim must receive the project's single TargetFramework even when --framework is omitted");
        Assert.IsFalse(shim.ResolvedMonikers.Contains(null),
            "the shim must never be consulted with a null moniker when the project declares a TargetFramework");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_UserSetMetadata_ShimNotConsulted()
    {
        // The user's explicit CsWinRTWindowsMetadata wins: the shim must not be consulted or injected.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var shim = new FakeCsWinRTMetadataShimService { FolderToReturn = @"C:\should\not\be\used\winmd" };
        var service = NewServiceWith(dotnet, shim, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false,
            Properties: [@"CsWinRTWindowsMetadata=C:\user\choice\winmd"]);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.AreEqual(0, shim.ResolvedMonikers.Count, "the shim must not be consulted when the user set the property");
        Assert.IsFalse(dotnet.StreamingCalls[0].Contains(@"C:\should\not\be\used\winmd"),
            "the shim's value must never override the user's explicit property");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_SdkAbsentShimInitiallyNull_RestoresThenReResolvesAndInjects()
    {
        // C1 (restore ordering): on a clean SDK-less host the ref pack isn't restored yet, so the first
        // shim resolve returns null. We must run an explicit `dotnet restore`, re-resolve (now a folder),
        // inject it into the build, and pass --no-restore to the build so we don't restore twice.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var commandArgs = new List<string>();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args => { commandArgs.Add(args); return (0, PackagedPropertiesJson(), string.Empty); },
        };
        var folder = @"C:\cache\microsoft.windows.sdk.net.ref\10.0.26100.57\winmd";
        var shim = new FakeCsWinRTMetadataShimService { WindowsSdkAbsent = true };
        shim.FolderSequence.Enqueue(null);   // first resolve: ref pack not restored yet
        shim.FolderSequence.Enqueue(folder); // second resolve: after the explicit restore
        var service = NewServiceWith(dotnet, shim, out _);
        var options = new ProjectRunOptions("Debug", "x64", "net10.0-windows10.0.26100.0", NoBuild: false, NoRestore: false, Properties: []);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.IsTrue(commandArgs.Any(a => a.StartsWith("restore ", StringComparison.Ordinal)),
            "an explicit restore pass should run before the build when the shim initially resolves null on an SDK-less host");
        Assert.AreEqual(2, shim.ResolvedMonikers.Count, "the shim should be re-consulted after the restore");
        var buildCall = dotnet.StreamingCalls.Single(a => a.StartsWith("build ", StringComparison.Ordinal));
        StringAssert.Contains(buildCall, $"-p:CsWinRTWindowsMetadata={folder}",
            "the re-resolved folder must be injected into the build");
        StringAssert.Contains(buildCall, "--no-restore",
            "the build pass should skip its own restore since the explicit restore already ran");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_PreShimRestoreFails_ExplainsThatBuildWillRetry()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args => args.StartsWith("restore ", StringComparison.Ordinal)
                ? (1, string.Empty, "simulated restore failure")
                : (0, PackagedPropertiesJson(), string.Empty),
        };
        var shim = new FakeCsWinRTMetadataShimService { WindowsSdkAbsent = true, FolderToReturn = null };
        using var console = new TestConsole();
        var logger = new LevelLogger<ProjectRunService>(LogLevel.Information);
        var service = new ProjectRunService(dotnet, NewDetection(dotnet), shim, console, logger);
        var options = new ProjectRunOptions(
            "Debug", "x64", "net10.0-windows10.0.26100.0",
            NoBuild: false, NoRestore: false, Properties: []);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution, "the best-effort restore must defer the final result to the build");
        Assert.IsTrue(logger.Entries.Any(entry =>
                entry.Level == LogLevel.Warning
                && entry.Message.Contains("continuing with the build, which will retry restore", StringComparison.Ordinal)),
            "the visible restore error must be followed by an explanation that the build will retry");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NoRestore_SkipsPreShimRestore()
    {
        // C1: --no-restore opts out of the pre-shim restore; the shim is consulted once and no restore runs.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var commandArgs = new List<string>();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args => { commandArgs.Add(args); return (0, PackagedPropertiesJson(), string.Empty); },
        };
        var shim = new FakeCsWinRTMetadataShimService { WindowsSdkAbsent = true, FolderToReturn = null };
        var service = NewServiceWith(dotnet, shim, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: true, Properties: []);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(commandArgs.Any(a => a.StartsWith("restore ", StringComparison.Ordinal)),
            "--no-restore must suppress the pre-shim restore");
        Assert.AreEqual(1, shim.ResolvedMonikers.Count, "the shim should be consulted exactly once when restore is suppressed");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_SdkPresent_NoPreShimRestore()
    {
        // C1: when a Windows SDK is registered the shim no-ops; there is nothing to restore-and-retry.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var commandArgs = new List<string>();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args => { commandArgs.Add(args); return (0, PackagedPropertiesJson(), string.Empty); },
        };
        var shim = new FakeCsWinRTMetadataShimService { WindowsSdkAbsent = false, FolderToReturn = null };
        var service = NewServiceWith(dotnet, shim, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(commandArgs.Any(a => a.StartsWith("restore ", StringComparison.Ordinal)),
            "no pre-shim restore should run when a Windows SDK is registered");
        Assert.AreEqual(1, shim.ResolvedMonikers.Count);
    }

    [TestMethod]
    public void BuildRestorePassArguments_IncludesRidConfigurationAndUserPropertiesWithoutNoRestore()
    {
        // C6: the pre-build restore mirrors the build's effective RID + configuration (as
        // -p:Configuration=, since 'dotnet restore' has no -c switch) + user -p, but never adds
        // --no-restore (restoring is the whole point) and leaves dotnet at its default verbosity.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var options = new ProjectRunOptions("Debug", "arm64", "net10.0-windows10.0.26100.0", NoBuild: false, NoRestore: false,
            Properties: ["WindowsPackageType=None"]);

        var args = ProjectRunService.BuildRestorePassArguments(csproj, options);

        StringAssert.StartsWith(args, "restore ");
        StringAssert.Contains(args, "-r win-arm64");
        StringAssert.Contains(args, "-p:Configuration=Debug");
        StringAssert.Contains(args, "-p:WindowsPackageType=None");
        Assert.IsFalse(args.Contains("--no-restore", StringComparison.Ordinal), "restore must not carry --no-restore");
        Assert.IsFalse(args.Contains(" -c ", StringComparison.Ordinal), "restore has no -c switch; configuration flows via -p:Configuration=");
        Assert.IsFalse(args.Contains(" -v ", StringComparison.Ordinal), "default restore must keep dotnet's default minimal verbosity");
    }

    [TestMethod]
    public void BuildRestorePassArguments_QuietVerbosity_AddsQuietSwitch()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildRestorePassArguments(csproj, options, "quiet");

        StringAssert.Contains(args, "-v quiet");
    }

    [TestMethod]
    public void BuildRestorePassArguments_SolutionTarget_OmitsResolvedPlatform()
    {
        var solution = WriteFile("App.slnx", SlnxListing("App.csproj", "Server/Server.csproj"));
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [],
            Solution: solution, Platform: "x64");

        var args = ProjectRunService.BuildRestorePassArguments(solution, options);

        Assert.IsFalse(args.Contains("-p:Platform=", StringComparison.OrdinalIgnoreCase),
            "a solution restore must not request a solution configuration that a configuration-free .slnx does not declare");
        StringAssert.Contains(args, "-p:Configuration=Debug");
    }

    [TestMethod]
    public void BuildRestorePassArguments_SolutionTarget_OmitsUserPlatform()
    {
        var solution = WriteFile("App.slnx", SlnxListing("App.csproj", "Server/Server.csproj"));
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false,
            Properties: ["Platform=x64", "WindowsPackageType=None"], Solution: solution);

        var args = ProjectRunService.BuildRestorePassArguments(solution, options);

        Assert.IsFalse(args.Contains("-p:Platform=", StringComparison.OrdinalIgnoreCase),
            "a user Platform property must not request an undeclared solution configuration");
        StringAssert.Contains(args, "-p:WindowsPackageType=None",
            "other user properties must continue to flow to solution restore");
    }

    [TestMethod]
    public void BuildRestorePassArguments_DropsDedicatedFlagPropertiesSoRidWins()
    {
        // C6: a conflicting user -p RuntimeIdentifier/Configuration/TargetFramework must NOT reach the
        // restore command — otherwise (MSBuild last-wins) it would restore a different RID/config graph
        // than the subsequent --no-restore build consumes, leaving no matching target in the assets file.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var options = new ProjectRunOptions("Debug", "x64", "net10.0-windows10.0.26100.0", NoBuild: false, NoRestore: false,
            Properties: ["RuntimeIdentifier=win-arm64", "Configuration=Release", "TargetFramework=net8.0", "WindowsPackageType=None"]);

        var args = ProjectRunService.BuildRestorePassArguments(csproj, options);

        StringAssert.Contains(args, "-r win-x64");
        StringAssert.Contains(args, "-p:Configuration=Debug");
        StringAssert.Contains(args, "-p:WindowsPackageType=None");
        Assert.IsFalse(args.Contains("win-arm64", StringComparison.Ordinal), "conflicting user -p:RuntimeIdentifier must be dropped so -r wins");
        Assert.IsFalse(args.Contains("Configuration=Release", StringComparison.Ordinal), "conflicting user -p:Configuration must be dropped");
        Assert.IsFalse(args.Contains("net8.0", StringComparison.Ordinal), "conflicting user -p:TargetFramework must be dropped");
    }

    #region ResolvePlatformInjection (conditional -p:Platform for AnyCPU-rejecting WindowsAppSDK targets)

    // A single-project WinUI app declaring <Platforms> that includes the arch (real-world: IconExtractor's
    // unpackaged self-contained app). Older WindowsAppSDK hard-rejects the default Platform=AnyCPU, so
    // winapp must inject the explicit Platform.
    private const string PlatformAwareExeCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
            <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
            <Platforms>x86;x64;ARM64</Platforms>
            <WindowsPackageType>None</WindowsPackageType>
            <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
          </PropertyGroup>
        </Project>
        """;

    private static ProjectRunOptions PlatformOptions(string arch, params string[] properties) =>
        new("Debug", arch, null, NoBuild: false, NoRestore: false, Properties: properties);

    #region RID-split detection (APPX1101 guard)

    // MSBuild builds a project once per distinct global-property set. An edge carrying
    // GlobalPropertiesToRemove="RuntimeIdentifier" drops the RID for that subtree, so a project reachable
    // both directly (RID inherited) and through such an edge is built into two output directories. A
    // packaged app harvests both copies into the MSIX payload and fails with APPX1101.

    private const string SharedLibCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
            <Platforms>AnyCPU;x64;ARM64</Platforms>
          </PropertyGroup>
        </Project>
        """;

    private FileInfo WriteRidSplitGraph(bool stripRidOnMiddleEdge)
    {
        WriteFile("Shared.csproj", SharedLibCsproj);
        var strip = stripRidOnMiddleEdge ? " GlobalPropertiesToRemove=\"RuntimeIdentifier\"" : string.Empty;
        WriteFile("Middle.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
                <Platforms>AnyCPU;x64;ARM64</Platforms>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="Shared.csproj"{strip} />
              </ItemGroup>
            </Project>
            """);

        // The app reaches Shared BOTH directly (RID inherited) and via Middle.
        return WriteFile("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
                <Platforms>x86;x64;ARM64</Platforms>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="Shared.csproj" />
                <ProjectReference Include="Middle.csproj" />
              </ItemGroup>
            </Project>
            """);
    }

    [TestMethod]
    public void RidSplit_SharedProjectReachedWithAndWithoutRid_OmitsRuntimeIdentifier()
    {
        var app = WriteRidSplitGraph(stripRidOnMiddleEdge: true);

        var resolved = ProjectRunService.ResolvePlatformInjection(app, PlatformOptions("arm64"));

        Assert.IsTrue(resolved.OmitRuntimeIdentifier, "a RID split with an effective Platform must drop the RID");
        Assert.AreEqual("ARM64", resolved.Platform, "Platform must still convey the architecture");

        // Every pass must agree, or the evaluate would resolve a TargetDir the build never wrote.
        Assert.IsFalse(ProjectRunService.BuildBuildPassArguments(app, resolved, "minimal").Contains("-r win-arm64", StringComparison.Ordinal));
        Assert.IsFalse(ProjectRunService.BuildRestorePassArguments(app, resolved).Contains("-r win-arm64", StringComparison.Ordinal));
        Assert.IsFalse(ProjectRunService.BuildEvaluateArguments(app, resolved).Contains("-p:RuntimeIdentifier=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RidSplit_NoStrippingEdge_KeepsRuntimeIdentifier()
    {
        var app = WriteRidSplitGraph(stripRidOnMiddleEdge: false);

        var resolved = ProjectRunService.ResolvePlatformInjection(app, PlatformOptions("arm64"));

        Assert.IsFalse(resolved.OmitRuntimeIdentifier, "without a split the RID must be preserved");
        StringAssert.Contains(ProjectRunService.BuildBuildPassArguments(app, resolved, "minimal"), "-r win-arm64");
    }

    [TestMethod]
    public void RidSplit_WithoutEffectivePlatform_KeepsRuntimeIdentifier()
    {
        // Same split, but a referenced project declares no <Platforms>, so Platform injection is off.
        // The RID is then the ONLY thing conveying the architecture and must never be dropped.
        WriteFile("Shared.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0-windows10.0.19041.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        WriteFile("Middle.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0-windows10.0.19041.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="Shared.csproj" GlobalPropertiesToRemove="RuntimeIdentifier" />
              </ItemGroup>
            </Project>
            """);
        var app = WriteFile("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
                <Platforms>x86;x64;ARM64</Platforms>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="Shared.csproj" />
                <ProjectReference Include="Middle.csproj" />
              </ItemGroup>
            </Project>
            """);

        var resolved = ProjectRunService.ResolvePlatformInjection(app, PlatformOptions("arm64"));

        Assert.IsNull(resolved.Platform, "closure lacks <Platforms>, so Platform must not be injected");
        Assert.IsFalse(resolved.OmitRuntimeIdentifier, "with no Platform the RID is the only arch carrier");
    }

    [TestMethod]
    public void RidSplit_UserSuppliedPlatform_StillOmitsRuntimeIdentifier()
    {
        // The reported repro passed -p Platform=arm64 explicitly; that path must be handled too.
        var app = WriteRidSplitGraph(stripRidOnMiddleEdge: true);

        var resolved = ProjectRunService.ResolvePlatformInjection(app, PlatformOptions("arm64", "Platform=arm64"));

        Assert.IsTrue(resolved.OmitRuntimeIdentifier);
        Assert.IsNull(resolved.Platform, "a user-supplied Platform is forwarded as-is, not re-injected");
    }

    [TestMethod]
    public void RidSplit_UndefinePropertiesSpelling_IsDetected()
    {
        WriteFile("Shared.csproj", SharedLibCsproj);
        WriteFile("Middle.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
                <Platforms>AnyCPU;x64;ARM64</Platforms>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="Shared.csproj" UndefineProperties="PublishAot;RuntimeIdentifier" />
              </ItemGroup>
            </Project>
            """);
        var app = WriteFile("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
                <Platforms>x86;x64;ARM64</Platforms>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="Shared.csproj" />
                <ProjectReference Include="Middle.csproj" />
              </ItemGroup>
            </Project>
            """);

        var resolved = ProjectRunService.ResolvePlatformInjection(app, PlatformOptions("arm64"));

        Assert.IsTrue(resolved.OmitRuntimeIdentifier, "UndefineProperties is an equivalent spelling of GlobalPropertiesToRemove");
    }

    #endregion

    [TestMethod]
    public void ResolvePlatformInjection_SingleProjectDeclaresArch_InjectsDeclaredToken()
    {
        var csproj = WriteFile("App.csproj", PlatformAwareExeCsproj);

        var resolved = ProjectRunService.ResolvePlatformInjection(csproj, PlatformOptions("arm64"));

        // Preserves the exact declared casing ("ARM64", not the canonical "arm64") so it matches the
        // solution configuration the project defines.
        Assert.AreEqual("ARM64", resolved.Platform);
    }

    [TestMethod]
    public void ResolvePlatformInjection_InjectedPlatformFlowsIntoBuildAndEvaluatePasses()
    {
        var csproj = WriteFile("App.csproj", PlatformAwareExeCsproj);

        var resolved = ProjectRunService.ResolvePlatformInjection(csproj, PlatformOptions("arm64"));
        var buildArgs = ProjectRunService.BuildBuildPassArguments(csproj, resolved, "minimal");
        var evaluateArgs = ProjectRunService.BuildEvaluateArguments(csproj, resolved);
        var restoreArgs = ProjectRunService.BuildRestorePassArguments(csproj, resolved);

        StringAssert.Contains(buildArgs, "-p:Platform=ARM64");
        StringAssert.Contains(evaluateArgs, "-p:Platform=ARM64");
        StringAssert.Contains(restoreArgs, "-p:Platform=ARM64");
    }

    [TestMethod]
    public void ResolvePlatformInjection_NoPlatformsDeclared_DoesNotInject()
    {
        // The default no-<Platforms> project builds as AnyCPU today; injecting a Platform is exactly what
        // the RID-only default avoids, so leave it alone (ExecutableCsproj declares no <Platforms>).
        var csproj = WriteFile("App.csproj", ExecutableCsproj);

        var resolved = ProjectRunService.ResolvePlatformInjection(csproj, PlatformOptions("arm64"));

        Assert.IsNull(resolved.Platform);
    }

    [TestMethod]
    public void ResolvePlatformInjection_PlatformsMissingArch_DoesNotInject()
    {
        // The project declares <Platforms> but not the requested arch — winapp cannot inject a Platform
        // the project doesn't define, so fall back to RID-only.
        var csproj = WriteFile("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
                <Platforms>x86;x64</Platforms>
              </PropertyGroup>
            </Project>
            """);

        var resolved = ProjectRunService.ResolvePlatformInjection(csproj, PlatformOptions("arm64"));

        Assert.IsNull(resolved.Platform);
    }

    [TestMethod]
    public void ResolvePlatformInjection_UserSuppliedPlatform_IsRespected()
    {
        // A user -p:Platform is authoritative and suppresses winapp's own injection (it flows through as-is).
        var csproj = WriteFile("App.csproj", PlatformAwareExeCsproj);

        var resolved = ProjectRunService.ResolvePlatformInjection(csproj, PlatformOptions("arm64", "Platform=x64"));

        Assert.IsNull(resolved.Platform);
    }

    [TestMethod]
    public void ResolvePlatformInjection_MultiProjectAllDeclareArch_Injects()
    {
        // Real-world: WINUI3_BOOKMS (app + Core lib) both declare <Platforms> including arm64, so the whole
        // closure supports the arch and injection is safe.
        var app = WriteFileAt("app\\App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
                <Platforms>x86;x64;arm64</Platforms>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        WriteFileAt("lib\\Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Platforms>AnyCPU;x64;x86;arm64</Platforms>
              </PropertyGroup>
            </Project>
            """);

        var resolved = ProjectRunService.ResolvePlatformInjection(app, PlatformOptions("arm64"));

        Assert.AreEqual("arm64", resolved.Platform);
    }

    [TestMethod]
    public void ResolvePlatformInjection_MultiProjectReferenceLacksPlatforms_DoesNotInject()
    {
        // The multi-project guard: a referenced library with NO <Platforms> (implicit AnyCPU) is the exact
        // case a global -p:Platform desyncs → MSB3030/PRI252. Skip injection to preserve the invariant.
        var app = WriteFileAt("app\\App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
                <Platforms>x86;x64;arm64</Platforms>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        WriteFileAt("lib\\Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var resolved = ProjectRunService.ResolvePlatformInjection(app, PlatformOptions("arm64"));

        Assert.IsNull(resolved.Platform);
    }

    [TestMethod]
    public void ResolvePlatformInjection_UnresolvableReferenceInclude_DoesNotInject()
    {
        // An MSBuild-expression Include can't be statically verified, so the guard cannot prove the closure
        // is clean — fall back to RID-only rather than force a Platform onto an unknown project.
        var app = WriteFileAt("app\\App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
                <Platforms>x86;x64;arm64</Platforms>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="$(LibDir)\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);

        var resolved = ProjectRunService.ResolvePlatformInjection(app, PlatformOptions("arm64"));

        Assert.IsNull(resolved.Platform);
    }

    [TestMethod]
    public void ResolvePlatformInjection_AnalyzerReferenceLacksPlatforms_StillInjects()
    {
        // Real-world: files-community/Files references its Roslyn source generator as an analyzer
        // (OutputItemType="Analyzer", ReferenceOutputAssembly="false"). That generator is netstandard2.0
        // with no <Platforms>, but it emits no arch-specific output or PRI, so it must NOT veto injection
        // for the app whose runtime references all declare the arch.
        var app = WriteFileAt("app\\App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
                <Platforms>x86;x64;arm64</Platforms>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\lib\Lib.csproj" />
                <ProjectReference Include="..\gen\Gen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
              </ItemGroup>
            </Project>
            """);
        WriteFileAt("lib\\Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
                <Platforms>x86;x64;arm64</Platforms>
              </PropertyGroup>
            </Project>
            """);
        WriteFileAt("gen\\Gen.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var resolved = ProjectRunService.ResolvePlatformInjection(app, PlatformOptions("arm64"));

        Assert.AreEqual("arm64", resolved.Platform);
    }

    [TestMethod]
    public void ResolvePlatformInjection_AnalyzerReferenceViaChildMetadata_IsExcluded()
    {
        // The build-only marker may be authored as a child element instead of an attribute; both forms must
        // exclude the reference from the closure so a no-<Platforms> generator doesn't suppress injection.
        var app = WriteFileAt("app\\App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
                <Platforms>x86;x64;arm64</Platforms>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\gen\Gen.csproj">
                  <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
                  <OutputItemType>Analyzer</OutputItemType>
                </ProjectReference>
              </ItemGroup>
            </Project>
            """);
        WriteFileAt("gen\\Gen.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var resolved = ProjectRunService.ResolvePlatformInjection(app, PlatformOptions("arm64"));

        Assert.AreEqual("arm64", resolved.Platform);
    }

    #endregion

    [TestMethod]
    public async Task IsDefinitivelyUnpackagedAsync_WindowsPackageTypeNone_ReturnsTrue()
    {
        // Issue #676: an explicit WindowsPackageType=None is the one definitive unpackaged signal, so
        // the pre-build fast-fail probe reports true.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, UnpackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var result = await service.IsDefinitivelyUnpackagedAsync(csproj, options, CancellationToken.None);

        Assert.IsTrue(result);
        Assert.AreEqual(0, dotnet.StreamingCalls.Count, "the probe must never build — it only evaluates");
    }

    [TestMethod]
    public async Task IsDefinitivelyUnpackagedAsync_PackagedType_ReturnsFalse()
    {
        // WindowsPackageType=MSIX → packaged → not definitively unpackaged.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var result = await service.IsDefinitivelyUnpackagedAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task IsDefinitivelyUnpackagedAsync_EmptyWindowsPackageType_ReturnsFalseSoRecipeFallbackStaysAuthoritative()
    {
        // An unset WindowsPackageType is INDETERMINATE pre-build — a packaged app that declares
        // identity via an emitted recipe also evaluates empty here. Reporting unpackaged would
        // misclassify it, so the probe returns false and defers to the authoritative post-build gate.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var emptyJson = $$"""{ "Properties": { "TargetDir": "", "RunCommand": "", "WindowsPackageType": "", "OutputType": "WinExe", "WindowsAppSDKSelfContained": "" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, emptyJson, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var result = await service.IsDefinitivelyUnpackagedAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task IsDefinitivelyUnpackagedAsync_EvaluationFails_ReturnsFalse()
    {
        // A failed evaluation is treated as indeterminate (never throws), so the normal build path
        // still runs and surfaces the real error.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (1, string.Empty, "MSB1009: Project file does not exist.") };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var result = await service.IsDefinitivelyUnpackagedAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task IsDefinitivelyUnpackagedAsync_CancellationDuringEvaluation_Rethrows()
    {
        // C9: a caller-requested cancellation during the evaluate probe must surface as
        // OperationCanceledException, not be swallowed and reported as an indeterminate (false) result.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => throw new OperationCanceledException() };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.IsDefinitivelyUnpackagedAsync(csproj, options, cts.Token));
    }

    [TestMethod]
    public async Task IsDefinitivelyUnpackagedAsync_NonCancellationProcessException_ReturnsFalse()
    {
        // A transient process-launch failure (e.g. Win32Exception) is indeterminate — the probe must
        // not crash the run before the authoritative post-build gate, so it returns false.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => throw new System.ComponentModel.Win32Exception("dotnet spawn failed") };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var result = await service.IsDefinitivelyUnpackagedAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(result);
    }

    private string UnpackagedPropertiesJson() =>
        $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "None", "OutputType": "WinExe", "WindowsAppSDKSelfContained": "" } }""";

    [TestMethod]
    public async Task BuildAndResolveAsync_JsonMode_DoesNotPrintBuildBannerToConsole()
    {
        // Spec H2: in --json mode stdout must be pure JSON, so the human-readable "Building…" banner
        // must not be written to the (stdout) console.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: true);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution, "the canned packaged build should resolve successfully");
        Assert.IsFalse(console.Output.Contains("Building", StringComparison.OrdinalIgnoreCase),
            "--json mode must not print the build banner to stdout");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_JsonMode_BuildFailureDiagnosticsGoToStderrNotStdout()
    {
        // Spec H2: on a failed build in --json mode, dotnet's captured diagnostics must be routed to
        // stderr so the (stdout) console stays free of non-JSON noise.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        const string diag = "error NETSDK9999: totally broken build";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (1, diag, "MSB1234: also broken") };
        var service = NewServiceWith(dotnet, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: true);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNull(outcome.Resolution, "a failed build should not resolve");
        Assert.AreEqual(1, outcome.ExitCode, "the dotnet exit code should propagate");
        Assert.IsFalse(console.Output.Contains(diag, StringComparison.OrdinalIgnoreCase),
            "--json mode must not write build diagnostics to stdout");
        Assert.IsFalse(console.Output.Contains("Building", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NonJsonMode_PrintsBuildBanner()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        // Information enabled models the default (non-quiet) run where the human banner is shown.
        var service = NewServiceWith(dotnet, LogLevel.Information, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(console.Output, "Building",
            "non-json mode should print the human-readable build banner");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_QuietMode_SuppressesBuildBanner()
    {
        // M8: --quiet (Information suppressed) must keep stdout clean like --json — no "Building" banner
        // written to the console; build output is routed to stderr instead.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Warning, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution, "the canned packaged build should still resolve under --quiet");
        Assert.IsFalse(console.Output.Contains("Building", StringComparison.OrdinalIgnoreCase),
            "--quiet must not print the build banner to stdout");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_WindowsPackageTypeNone_ResolvesUnpackaged()
    {
        // Spec §7.1: WindowsPackageType=None => unpackaged; RunCommand is the launchable apphost .exe.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var exe = WriteFile("App.exe", "stub"); // must exist for the unpackaged launch path
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "{{exe.FullName.Replace("\\", "\\\\")}}", "WindowsPackageType": "None", "OutputType": "WinExe", "WindowsAppSDKSelfContained": "false" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(ProjectPackaging.Unpackaged, outcome.Resolution!.Packaging);
        Assert.AreEqual(exe.FullName, outcome.Resolution.RunCommand);
        Assert.IsFalse(outcome.Resolution.SelfContained);
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_EmptyPackageTypeWithMsixTooling_ResolvesPackaged()
    {
        // --no-build evaluate-only path: MSIX targets don't run so WindowsPackageType is empty;
        // fall back to EnableMsixTooling=true => packaged.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "", "EnableMsixTooling": "true", "OutputType": "WinExe" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: true, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(ProjectPackaging.Packaged, outcome.Resolution!.Packaging);
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_EmptyPackageTypeWithWinAppRunSupport_ResolvesPackaged()
    {
        // The Microsoft.Windows.SDK.BuildTools.WinApp integration activates run support
        // (_WinAppRunSupportActive=true) for an executable Windows app that ships an appxmanifest.xml
        // but sets no WindowsPackageType (e.g. samples/dotnet-app). WindowsPackageType and
        // EnableMsixTooling are both empty, so the app must still resolve Packaged off the run-support
        // signal — otherwise it launches without identity and Package.Current fails.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "", "EnableMsixTooling": "", "_WinAppRunSupportActive": "true", "OutputType": "Exe" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: true, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(ProjectPackaging.Packaged, outcome.Resolution!.Packaging);
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_HappyPath_CarriesResolvedArchitecture()
    {
        // The resolved architecture must flow onto the resolution so the correct-arch runtime is
        // installed for the packaged/unpackaged launch.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "arm64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual("arm64", outcome.Resolution!.Architecture);
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NonExecutableOutputType_Throws()
    {
        // Guardrail: a non-runnable project (OutputType=Library) must fail fast, never launch.
        var csproj = WriteFile("Lib.csproj", LibraryCsproj);
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "None", "OutputType": "Library" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.BuildAndResolveAsync(csproj, options, CancellationToken.None));
        StringAssert.Contains(ex.Message, "OutputType");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_EmptyTargetDir_Throws()
    {
        // Guardrail: an empty TargetDir means we have nowhere to register/launch from (the M4 surface
        // — a braced build preamble that broke parsing used to reach here with an empty dict).
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var json = """{ "Properties": { "TargetDir": "", "RunCommand": "", "WindowsPackageType": "MSIX", "OutputType": "WinExe" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.BuildAndResolveAsync(csproj, options, CancellationToken.None));
        StringAssert.Contains(ex.Message, "TargetDir");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_UnpackagedMissingRunCommand_Throws()
    {
        // Guardrail: an unpackaged app with no launchable .exe (empty/absent RunCommand) must error.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "None", "OutputType": "WinExe" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.BuildAndResolveAsync(csproj, options, CancellationToken.None));
        StringAssert.Contains(ex.Message, "unpackaged");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NoBuildUnpackagedMissingExe_HintsToRemoveNoBuild()
    {
        // With --no-build the guardrail should point the user at removing --no-build so the exe exists.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "{{Path.Combine(_tempDir.FullName, "missing.exe").Replace("\\", "\\\\")}}", "WindowsPackageType": "None", "OutputType": "WinExe" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: true, NoRestore: false, Properties: [], Json: false);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.BuildAndResolveAsync(csproj, options, CancellationToken.None));
        StringAssert.Contains(ex.Message, "--no-build");
    }

    #endregion

    #region ComputeSolutionRestorePlan (ISSUE-1: build-dependency sibling restore, NETSDK1004 parity)

    [TestMethod]
    public void ComputeSolutionRestorePlan_SlnxListedSibling_IncludedTargetExcludedAllManaged()
    {
        // The out-of-process server (Files.App.Server class) is a first-class <Project> in the .slnx even
        // though it's only a <BuildDependency> — not a ProjectReference — of the target. It must land in
        // the restore set; the target itself must not.
        var target = WriteFileAt(@"src\App\App.csproj", ExecutableCsproj);
        var solution = WriteFile("App.slnx", SlnxListing("src/App/App.csproj", "src/Server/Server.csproj"));

        var (allManaged, siblings) = ProjectRunService.ComputeSolutionRestorePlan(solution, target);

        Assert.IsTrue(allManaged, "every listed project is a managed .csproj");
        Assert.AreEqual(1, siblings.Count);
        Assert.AreEqual(Path.Combine(_tempDir.FullName, "src", "Server", "Server.csproj"), siblings[0].FullName);
        Assert.IsFalse(siblings.Any(s => string.Equals(s.FullName, target.FullName, StringComparison.OrdinalIgnoreCase)),
            "the target must never be listed as its own sibling");
    }

    [TestMethod]
    public void ComputeSolutionRestorePlan_SlnxBuildDependencyElement_NotDoubleCountedButListedSiblingIncluded()
    {
        // A nested <BuildDependency Project="..."/> element must NOT be treated as a project-list entry
        // (its element name is BuildDependency, not Project). The sibling still appears because it is ALSO
        // listed as a top-level <Project Path=...>, and it must be de-duplicated to a single entry.
        var target = WriteFileAt(@"src\App\App.csproj", ExecutableCsproj);
        var slnx =
            "<Solution>" + Environment.NewLine +
            "  <Project Path=\"src/App/App.csproj\">" + Environment.NewLine +
            "    <BuildDependency Project=\"src/Server/Server.csproj\" />" + Environment.NewLine +
            "  </Project>" + Environment.NewLine +
            "  <Project Path=\"src/Server/Server.csproj\" />" + Environment.NewLine +
            "</Solution>";
        var solution = WriteFile("App.slnx", slnx);

        var (allManaged, siblings) = ProjectRunService.ComputeSolutionRestorePlan(solution, target);

        Assert.IsTrue(allManaged);
        Assert.AreEqual(1, siblings.Count, "the BuildDependency element must not add a second Server entry");
        Assert.AreEqual(Path.Combine(_tempDir.FullName, "src", "Server", "Server.csproj"), siblings[0].FullName);
    }

    [TestMethod]
    public void ComputeSolutionRestorePlan_ClassicSlnListedSibling_Included()
    {
        var target = WriteFileAt(@"src\App\App.csproj", ExecutableCsproj);
        var solution = WriteFile("App.sln", SlnListing(@"src\App\App.csproj", @"src\Server\Server.csproj"));

        var (allManaged, siblings) = ProjectRunService.ComputeSolutionRestorePlan(solution, target);

        Assert.IsTrue(allManaged);
        Assert.AreEqual(1, siblings.Count);
        Assert.AreEqual(Path.Combine(_tempDir.FullName, "src", "Server", "Server.csproj"), siblings[0].FullName);
    }

    [TestMethod]
    public void ComputeSolutionRestorePlan_NativeSibling_ExcludedAndNotAllManaged()
    {
        // A native .vcxproj can't be `dotnet restore`d on a VS-less box, so it's excluded from the set and
        // flips AllManaged to false (the caller then restores managed siblings individually).
        var target = WriteFileAt(@"src\App\App.csproj", ExecutableCsproj);
        var solution = WriteFile("App.slnx",
            SlnxListing("src/App/App.csproj", "src/Managed/Managed.csproj", "src/Native/Native.vcxproj"));

        var (allManaged, siblings) = ProjectRunService.ComputeSolutionRestorePlan(solution, target);

        Assert.IsFalse(allManaged, "a native .vcxproj must flip AllManaged to false");
        Assert.AreEqual(1, siblings.Count, "only the managed sibling is restorable");
        Assert.AreEqual(Path.Combine(_tempDir.FullName, "src", "Managed", "Managed.csproj"), siblings[0].FullName);
        Assert.IsFalse(siblings.Any(s => s.FullName.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase)),
            "the native project must be excluded from the restore set");
    }

    [TestMethod]
    public void ComputeSolutionRestorePlan_OnlyTarget_EmptySiblingsAllManaged()
    {
        var target = WriteFileAt(@"src\App\App.csproj", ExecutableCsproj);
        var solution = WriteFile("App.slnx", SlnxListing("src/App/App.csproj"));

        var (allManaged, siblings) = ProjectRunService.ComputeSolutionRestorePlan(solution, target);

        Assert.IsTrue(allManaged);
        Assert.AreEqual(0, siblings.Count, "a solution that lists only the target has no extra siblings to restore");
    }

    [TestMethod]
    public void ComputeSolutionRestorePlan_VbprojAndFsprojSiblings_TreatedAsManaged()
    {
        // .vbproj/.fsproj are dotnet-restorable managed types too, so they stay in the set and keep
        // AllManaged true.
        var target = WriteFileAt(@"src\App\App.csproj", ExecutableCsproj);
        var solution = WriteFile("App.slnx",
            SlnxListing("src/App/App.csproj", "src/Vb/Vb.vbproj", "src/Fs/Fs.fsproj"));

        var (allManaged, siblings) = ProjectRunService.ComputeSolutionRestorePlan(solution, target);

        Assert.IsTrue(allManaged);
        Assert.AreEqual(2, siblings.Count);
        CollectionAssert.AreEquivalent(
            new[]
            {
                Path.Combine(_tempDir.FullName, "src", "Vb", "Vb.vbproj"),
                Path.Combine(_tempDir.FullName, "src", "Fs", "Fs.fsproj"),
            },
            siblings.Select(s => s.FullName).ToList());
    }

    [TestMethod]
    public void ComputeSolutionRestorePlan_ClassicSlnSolutionFolder_Ignored()
    {
        // A classic .sln solution-folder entry has a "path" equal to its name (no ...proj extension). It
        // must not be counted as a project — otherwise it would spuriously flip AllManaged.
        var target = WriteFileAt(@"src\App\App.csproj", ExecutableCsproj);
        var solution = WriteFile("App.sln", SlnListing(@"src\App\App.csproj", "Solution Items", @"src\Server\Server.csproj"));

        var (allManaged, siblings) = ProjectRunService.ComputeSolutionRestorePlan(solution, target);

        Assert.IsTrue(allManaged, "the solution-folder entry is not a project and must not flip AllManaged");
        Assert.AreEqual(1, siblings.Count);
        Assert.AreEqual(Path.Combine(_tempDir.FullName, "src", "Server", "Server.csproj"), siblings[0].FullName);
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_SolutionAllManaged_RestoresWholeSolutionThenBuildsNoRestore()
    {
        // ISSUE-1: when the owning solution is all-managed, one `dotnet restore <sln>` restores the target
        // and every build-dependency sibling before the build, and the build pass skips its own restore.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var solution = WriteFile("App.slnx", SlnxListing("App.csproj", "Server/Server.csproj"));
        var longRestoreLine = "RESTORE-PROGRESS-" + new string('X', 120);
        var restoreOutput = $"{longRestoreLine}{Environment.NewLine}{Environment.NewLine}AFTER-BLANK";
        var commandArgs = new List<string>();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = a =>
            {
                commandArgs.Add(a);
                return a.StartsWith("restore ", StringComparison.Ordinal)
                    ? (0, restoreOutput, string.Empty)
                    : (0, PackagedPropertiesJson(), string.Empty);
            },
        };
        var service = NewServiceWith(dotnet, LogLevel.Information, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.IsTrue(commandArgs.Any(a => a.StartsWith($"restore {solution.FullName}", StringComparison.Ordinal)),
            "the whole solution should be restored up front for build-dependency parity");
        StringAssert.Contains(console.Output, "Restoring App.slnx dependencies",
            "the restore phase should be announced before dotnet starts");
        StringAssert.Contains(console.Output, longRestoreLine,
            "restore output should stream live without Spectre wrapping the subprocess line");
        StringAssert.Contains(
            console.Output.ReplaceLineEndings("\n"),
            $"{longRestoreLine}\n\nAFTER-BLANK",
            "the streaming fake should preserve genuine blank subprocess lines without inventing CRLF blanks");
        StringAssert.Contains(dotnet.StreamingCalls.Single(a => a.StartsWith("build ", StringComparison.Ordinal)), "--no-restore",
            "the build pass should skip its own restore since the solution restore already covered the target");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_PlatformSpecificSolutionRestore_LetsProjectBuildRestoreAgain()
    {
        var csproj = WriteFile("App.csproj", PlatformAwareExeCsproj);
        var solution = WriteFile("App.slnx", SlnxListing("App.csproj", "Server/Server.csproj"));
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty),
        };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        var solutionRestore = dotnet.StreamingCalls.Single(
            args => args.StartsWith($"restore {solution.FullName}", StringComparison.Ordinal));
        Assert.IsFalse(solutionRestore.Contains("-p:Platform=", StringComparison.Ordinal),
            "solution restore must omit Platform to avoid MSB4126");
        var build = dotnet.StreamingCalls.Single(
            args => args.StartsWith($"build {csproj.FullName}", StringComparison.Ordinal));
        StringAssert.Contains(build, "-p:Platform=x64",
            "the selected project build must retain its resolved Platform");
        Assert.IsFalse(build.Contains("--no-restore", StringComparison.Ordinal),
            "the project must restore again because the platform-less solution restore did not cover platform-conditioned assets");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_UserPlatformSolutionRestore_LetsProjectBuildRestoreAgain()
    {
        var csproj = WriteFile("App.csproj", PlatformAwareExeCsproj);
        var solution = WriteFile("App.slnx", SlnxListing("App.csproj", "Server/Server.csproj"));
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty),
        };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false,
            Properties: ["Platform=x64"], Solution: solution);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        var solutionRestore = dotnet.StreamingCalls.Single(
            args => args.StartsWith($"restore {solution.FullName}", StringComparison.Ordinal));
        Assert.IsFalse(solutionRestore.Contains("-p:Platform=", StringComparison.Ordinal),
            "solution restore must omit the user Platform to avoid MSB4126");
        var build = dotnet.StreamingCalls.Single(
            args => args.StartsWith($"build {csproj.FullName}", StringComparison.Ordinal));
        StringAssert.Contains(build, "-p:Platform=x64",
            "the selected project build must retain the user-supplied Platform");
        Assert.IsFalse(build.Contains("--no-restore", StringComparison.Ordinal),
            "the project must restore again because the solution restore did not cover platform-conditioned assets");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_SolutionRestoreInRealTerminal_StreamsRedactedOutput()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var solution = WriteFile("App.slnx", SlnxListing("App.csproj", "Server/Server.csproj"));
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty),
            RunDotnetStreamingHandler = (_, onOut, _) =>
            {
                onOut?.Invoke("NU1301 https://feed.example/v3/index.json?sig=RESTORE_SECRET");
                return 0;
            },
        };
        var service = NewServiceWith(dotnet, LogLevel.Information, out var console);
        service.NativeTerminalGateOverrideForTests = () => true;
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.IsTrue(
            dotnet.StreamingCalls.Any(a => a.StartsWith($"restore {solution.FullName}", StringComparison.Ordinal)),
            "interactive restore must stream through winapp so output can be redacted");
        Assert.IsFalse(
            dotnet.InheritedCalls.Any(a => a.StartsWith("restore ", StringComparison.Ordinal)),
            "restore output must never bypass winapp's redaction through inherited stdio");
        StringAssert.Contains(console.Output, "Restoring App.slnx dependencies");
        StringAssert.Contains(console.Output, "https://feed.example/v3/index.json?<redacted>");
        Assert.IsFalse(console.Output.Contains("RESTORE_SECRET", StringComparison.Ordinal));
    }

    [TestMethod]
    [DoNotParallelize] // redirects the process-wide Console.Error
    public async Task RunRestoreCommandAsync_Json_KeepsStdoutClean_RoutesInvocationAndOutputToStderr()
    {
        var solution = WriteFile("App.slnx", SlnxListing("App.csproj", "Server/Server.csproj"));
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false,
            Properties: ["NuGetApiKey=top-secret"], Json: true, Solution: solution);
        var args = ProjectRunService.BuildRestorePassArguments(solution, options);
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, onOut, onErr) =>
            {
                onOut?.Invoke("RESTORE-STDOUT https://feed.example/v3/index.json?sig=STDOUT_SECRET");
                onErr?.Invoke("RESTORE-STDERR https://feed.example/v3/index.json?sig=STDERR_SECRET");
                return 0;
            },
        };
        var service = NewServiceWith(dotnet, LogLevel.None, out var console);

        using var stderr = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(stderr);
        int exit;
        try
        {
            exit = await service.RunRestoreCommandAsync(
                args, "Restoring App.slnx dependencies...", options, _tempDir, CancellationToken.None);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.AreEqual(0, exit);
        Assert.AreEqual(string.Empty, console.Output, "--json must keep stdout clean");
        StringAssert.Contains(stderr.ToString(), "dotnet restore", "--json must route the invocation to stderr");
        StringAssert.Contains(stderr.ToString(), "NuGetApiKey=***", "displayed restore commands must redact secrets");
        Assert.IsFalse(stderr.ToString().Contains("top-secret"), "the secret must not be displayed");
        StringAssert.Contains(stderr.ToString(), "RESTORE-STDOUT", "--json must route child stdout to stderr");
        StringAssert.Contains(stderr.ToString(), "RESTORE-STDERR", "--json must route child stderr to stderr");
        Assert.IsFalse(stderr.ToString().Contains("STDOUT_SECRET"), "restore stdout must redact feed credentials");
        Assert.IsFalse(stderr.ToString().Contains("STDERR_SECRET"), "restore stderr must redact feed credentials");
    }

    [TestMethod]
    [DoNotParallelize] // redirects the process-wide Console.Error
    public async Task RunRestoreCommandAsync_Quiet_KeepsStdoutClean_RoutesQuietOutputToStderr_SuppressesInvocation()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);
        var args = ProjectRunService.BuildRestorePassArguments(csproj, options, "quiet");
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, onOut, onErr) =>
            {
                onOut?.Invoke("QUIET-RESTORE-STDOUT");
                onErr?.Invoke("QUIET-RESTORE-STDERR");
                return 0;
            },
        };
        var service = NewServiceWith(dotnet, LogLevel.Warning, out var console);

        using var stderr = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(stderr);
        int exit;
        try
        {
            exit = await service.RunRestoreCommandAsync(
                args, "Restoring App.csproj dependencies...", options, _tempDir, CancellationToken.None);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.AreEqual(0, exit);
        Assert.AreEqual(string.Empty, console.Output, "--quiet must keep stdout clean");
        Assert.IsFalse(stderr.ToString().Contains("dotnet restore"), "--quiet must suppress the invocation");
        StringAssert.Contains(stderr.ToString(), "QUIET-RESTORE-STDOUT", "--quiet must route child stdout to stderr");
        StringAssert.Contains(stderr.ToString(), "QUIET-RESTORE-STDERR", "--quiet must route child stderr to stderr");
        StringAssert.Contains(dotnet.StreamingCalls.Single(), "-v quiet",
            "--quiet must run dotnet restore at quiet verbosity");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_SolutionRestore_QuietUsesQuietVerbosity()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var solution = WriteFile("App.slnx", SlnxListing("App.csproj", "Server/Server.csproj"));
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty),
        };
        var service = NewServiceWith(dotnet, LogLevel.Warning, out _);
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        var restoreArgs = dotnet.StreamingCalls.Single(
            args => args.StartsWith($"restore {solution.FullName}", StringComparison.Ordinal));
        StringAssert.Contains(restoreArgs, "-v quiet",
            "--quiet must apply quiet verbosity to the restore created by the project-run pipeline");
    }

    [TestMethod]
    [DoNotParallelize] // redirects the process-wide Console.Error
    public async Task BuildAndResolveAsync_SolutionRestore_JsonPreservesDefaultVerbosity()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var solution = WriteFile("App.slnx", SlnxListing("App.csproj", "Server/Server.csproj"));
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty),
        };
        var service = NewServiceWith(dotnet, LogLevel.None, out _);
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [],
            Json: true, Solution: solution);

        using var stderr = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(stderr);
        try
        {
            var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);
            Assert.IsNotNull(outcome.Resolution);
        }
        finally
        {
            Console.SetError(originalError);
        }

        var restoreArgs = dotnet.StreamingCalls.Single(
            args => args.StartsWith($"restore {solution.FullName}", StringComparison.Ordinal));
        Assert.IsFalse(restoreArgs.Contains(" -v ", StringComparison.Ordinal),
            "--json must preserve dotnet's default restore verbosity in the project-run pipeline");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_SolutionWithNativeSibling_RestoresManagedSiblingNotVcxproj()
    {
        // ISSUE-1: with a native sibling present, `dotnet restore <sln>` would fail on a VS-less box, so
        // the managed sibling is restored individually and the .vcxproj is never handed to dotnet restore.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var solution = WriteFile("App.slnx",
            SlnxListing("App.csproj", "Managed/Managed.csproj", "Native/Native.vcxproj"));
        var managedSibling = Path.Combine(_tempDir.FullName, "Managed", "Managed.csproj");
        var commandArgs = new List<string>();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = a => { commandArgs.Add(a); return (0, PackagedPropertiesJson(), string.Empty); },
        };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsTrue(commandArgs.Any(a => a.StartsWith("restore ", StringComparison.Ordinal) && a.Contains(managedSibling)),
            "the managed sibling must be restored individually when a native project is present");
        Assert.IsFalse(commandArgs.Any(a => a.Contains("Native.vcxproj", StringComparison.OrdinalIgnoreCase)),
            "a native .vcxproj must never be handed to dotnet restore");
        Assert.IsFalse(commandArgs.Any(a => a.StartsWith($"restore {solution.FullName}", StringComparison.Ordinal)),
            "the whole-solution restore must not run (the solution must not be the restore target) when a native project is present");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_WholeSolutionRestoreFails_FallsBackToPerSiblingRestore()
    {
        // C25: an all-managed solution restores as a whole first, but if that whole-solution restore FAILS
        // the managed siblings must still be restored individually (the NETSDK1004 case this pre-step exists
        // to prevent) rather than silently deferring to the target-only build restore.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var solution = WriteFile("App.slnx", SlnxListing("App.csproj", "Server/Server.csproj"));
        var serverSibling = Path.Combine(_tempDir.FullName, "Server", "Server.csproj");
        var commandArgs = new List<string>();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = a =>
            {
                commandArgs.Add(a);
                // Fail only the whole-solution restore; everything else (per-sibling restore, evaluate) succeeds.
                if (a.StartsWith($"restore {solution.FullName}", StringComparison.Ordinal))
                {
                    return (1, string.Empty, "simulated whole-solution restore failure");
                }

                return (0, PackagedPropertiesJson(), string.Empty);
            },
        };
        using var console = new TestConsole();
        var logger = new LevelLogger<ProjectRunService>(LogLevel.Information);
        var service = new ProjectRunService(
            dotnet, NewDetection(dotnet), new FakeCsWinRTMetadataShimService(), console, logger);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsTrue(commandArgs.Any(a => a.StartsWith($"restore {solution.FullName}", StringComparison.Ordinal)),
            "the all-managed whole-solution restore must be attempted first");
        Assert.IsTrue(commandArgs.Any(a => a.StartsWith("restore ", StringComparison.Ordinal) && a.Contains(serverSibling)),
            "after the whole-solution restore fails, the managed sibling must be restored individually (NETSDK1004 guard)");
        Assert.IsTrue(logger.Entries.Any(entry =>
                entry.Level == LogLevel.Warning
                && entry.Message.Contains("retrying managed dependencies individually", StringComparison.Ordinal)),
            "the visible solution restore error must explain that winapp is falling back");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_SiblingRestoreFails_ExplainsThatBuildContinues()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var solution = WriteFile(
            "App.slnx",
            SlnxListing("App.csproj", "Managed/Managed.csproj", "Native/Native.vcxproj"));
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.StartsWith("restore ", StringComparison.Ordinal)
                    ? (1, string.Empty, "simulated sibling restore failure")
                    : (0, PackagedPropertiesJson(), string.Empty),
        };
        using var console = new TestConsole();
        var logger = new LevelLogger<ProjectRunService>(LogLevel.Information);
        var service = new ProjectRunService(
            dotnet, NewDetection(dotnet), new FakeCsWinRTMetadataShimService(), console, logger);
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution, "the best-effort sibling restore must defer the final result to the build");
        Assert.IsTrue(logger.Entries.Any(entry =>
                entry.Level == LogLevel.Warning
                && entry.Message.Contains("continuing with the build", StringComparison.Ordinal)),
            "the visible sibling restore error must explain that the build continues");
    }

    [TestMethod]
    [DoNotParallelize] // redirects the process-wide Console.Error
    public async Task BuildAndResolveAsync_QuietSiblingRestoreFails_WritesExplanationToStderr()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var solution = WriteFile(
            "App.slnx",
            SlnxListing("App.csproj", "Managed/Managed.csproj", "Native/Native.vcxproj"));
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.StartsWith("restore ", StringComparison.Ordinal)
                    ? (1, string.Empty, "simulated sibling restore failure")
                    : (0, PackagedPropertiesJson(), string.Empty),
        };
        using var console = new TestConsole();
        var logger = new LevelLogger<ProjectRunService>(LogLevel.Warning);
        var service = new ProjectRunService(
            dotnet, NewDetection(dotnet), new FakeCsWinRTMetadataShimService(), console, logger);
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        using var stderr = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(stderr);
        ProjectBuildOutcome outcome;
        try
        {
            outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(string.Empty, console.Output, "--quiet must keep stdout clean");
        StringAssert.Contains(stderr.ToString(), "continuing with the build",
            "the best-effort restore failure explanation must remain visible on stderr");
        Assert.IsFalse(logger.Entries.Any(entry => entry.Level == LogLevel.Warning),
            "quiet restore fallback warnings must bypass the stdout-routed logger");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_SolutionListsOnlyTarget_NoSiblingRestore()
    {
        // Negative control: a solution that lists only the target adds no extra restore — behaviour is
        // identical to a bare-csproj run.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var solution = WriteFile("App.slnx", SlnxListing("App.csproj"));
        var commandArgs = new List<string>();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = a => { commandArgs.Add(a); return (0, PackagedPropertiesJson(), string.Empty); },
        };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(commandArgs.Any(a => a.StartsWith("restore ", StringComparison.Ordinal)),
            "a solution with no extra siblings must not trigger a sibling restore");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NoRestore_SkipsSolutionSiblingRestore()
    {
        // Negative control: --no-restore opts out of the up-front solution-sibling restore entirely.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var solution = WriteFile("App.slnx", SlnxListing("App.csproj", "Server/Server.csproj"));
        var commandArgs = new List<string>();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = a => { commandArgs.Add(a); return (0, PackagedPropertiesJson(), string.Empty); },
        };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: true, Properties: [], Solution: solution);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(commandArgs.Any(a => a.StartsWith("restore ", StringComparison.Ordinal)),
            "--no-restore must suppress the solution-sibling restore");
    }

    #endregion

    #region Two-pass build + verbosity + spinner (Change #1 / #4)

    [TestMethod]
    public async Task BuildAndResolveAsync_TwoPass_StreamsBuildThenEvaluatesProperties()
    {
        // Change #1: the build must run as TWO dotnet invocations — a streamed `dotnet build` (no
        // --getProperty, which would suppress the console log) followed by an evaluate-only
        // `dotnet msbuild --getProperty` that returns the resolved paths.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        string? evalArgs = null;
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = a => { evalArgs = a; return (0, PackagedPropertiesJson(), string.Empty); },
        };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution, "the canned packaged build should resolve");
        Assert.AreEqual(1, dotnet.StreamingCalls.Count, "the build pass should stream exactly once");
        StringAssert.StartsWith(dotnet.StreamingCalls[0], "build ");
        Assert.IsFalse(dotnet.StreamingCalls[0].Contains("--getProperty"),
            "the streamed build pass must not request properties");
        Assert.IsNotNull(evalArgs, "the evaluate pass must run");
        StringAssert.StartsWith(evalArgs!, "msbuild ");
        StringAssert.Contains(evalArgs!, "--getProperty:TargetDir");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_VerboseLogger_MapsToMinimalDotnetVerbosity()
    {
        // Change #1 (UX): --verbose (ILogger Debug) keeps dotnet at -v minimal on purpose — --verbose
        // already streams the build live and unlocks winapp's own traces, so -v normal would just bury
        // those under the MSBuild flood. Only --trace cranks dotnet up (covered below).
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Debug, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(dotnet.StreamingCalls[0], "-v minimal");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_TraceLogger_MapsToNormalDotnetVerbosity()
    {
        // Change #1 (UX): --trace (ILogger Trace) is the only level that cranks dotnet up to -v normal
        // for deep build diagnosis.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Trace, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(dotnet.StreamingCalls[0], "-v normal");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_DefaultLogger_MapsToMinimalDotnetVerbosity()
    {
        // Change #1: an ordinary (Information) run keeps dotnet tidy with -v minimal.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(dotnet.StreamingCalls[0], "-v minimal");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_QuietLogger_MapsToQuietDotnetVerbosity()
    {
        // Change #1: --quiet (Information suppressed) keeps dotnet quiet too.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Warning, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(dotnet.StreamingCalls[0], "-v quiet");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NoBuild_SkipsBuildPass_EvaluatesOnly()
    {
        // Change #1: --no-build must skip the streamed build pass and only evaluate properties.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: true, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(0, dotnet.StreamingCalls.Count, "--no-build must not run the streamed build pass");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_BuildFailure_ShortCircuitsBeforePostBuildEvaluate()
    {
        // A failed build pass must propagate its exit code and skip the post-build evaluate. The
        // publish-profile preflight still evaluates once before the build.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var evaluationCount = 0;
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, _, _) => 7,
            RunDotnetCommandHandler = _ =>
            {
                evaluationCount++;
                return (0, PackagedPropertiesJson(), string.Empty);
            },
        };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNull(outcome.Resolution, "a failed build must not resolve");
        Assert.AreEqual(7, outcome.ExitCode, "the build exit code must propagate");
        Assert.AreEqual(1, evaluationCount, "a failed build must skip the post-build evaluate");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_MultiTargetedNoFramework_PinsFirstTfmIntoBuild()
    {
        // H1: a plural-<TargetFrameworks> project with no --framework must pin the FIRST declared TFM
        // so the build pass (and the subsequent evaluate pass) target one inner build, not the empty
        // cross-targeting outer node.
        var csproj = WriteFile("App.csproj", MultiTargetedExeCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution, "a multi-targeted project should resolve after pinning a TFM");
        StringAssert.Contains(dotnet.StreamingCalls[0], "-f net10.0-windows10.0.26100.0", "the first declared TFM must be pinned into the build pass");
        Assert.IsFalse(dotnet.StreamingCalls[0].Contains("net8.0-windows10.0.26100.0"), "only the first TFM should be pinned");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_SingleTargetedNoFramework_DoesNotInjectFramework()
    {
        // H1 no-op: a single-<TargetFramework> project already resolves one TFM in both passes, so no
        // -f should be injected (the build stays exactly as before this fix).
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(dotnet.StreamingCalls[0].Contains("-f "), "a single-targeted project must not have a framework injected");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_MultiTargetedExplicitFramework_HonorsUserChoice()
    {
        // H1 must never override an explicit --framework on a multi-targeted project.
        var csproj = WriteFile("App.csproj", MultiTargetedExeCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", "net8.0-windows10.0.26100.0", NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(dotnet.StreamingCalls[0], "-f net8.0-windows10.0.26100.0", "an explicit --framework must be honored, not replaced by the first TFM");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_SingleTargetedUserTargetFrameworkProperty_IsHonoredNotDropped()
    {
        // A bare -p:TargetFramework=X (no --framework) must NOT be silently dropped by the dedicated-flag
        // filter: it is promoted to the effective framework and re-emitted via -f so the user's TFM choice
        // actually builds/runs. Without the promotion the build would fall back to the project's default TFM.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: ["TargetFramework=net8.0-windows10.0.26100.0"], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(dotnet.StreamingCalls[0], "-f net8.0-windows10.0.26100.0", "a bare -p:TargetFramework must be promoted and honored, not dropped");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_MultiTargetedUserTargetFrameworkProperty_BeatsFirstTfmDefault()
    {
        // On a multi-targeted project an explicit -p:TargetFramework=X (no --framework) is a deliberate
        // request and must win over the first-TFM auto-pin default — the user gets net8, not net10.
        var csproj = WriteFile("App.csproj", MultiTargetedExeCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: ["TargetFramework=net8.0-windows10.0.26100.0"], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(dotnet.StreamingCalls[0], "-f net8.0-windows10.0.26100.0", "an explicit -p:TargetFramework must beat the multi-targeted first-TFM default");
        Assert.IsFalse(dotnet.StreamingCalls[0].Contains("-f net10.0-windows10.0.26100.0"), "the auto-pinned first TFM must not override the user's explicit -p:TargetFramework");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_ImportedTargetFrameworks_DiscoversAndPinsFirstTfm()
    {
        // C16: a project whose <TargetFrameworks> come from an import (Directory.Build.props) is invisible
        // to the static scan, so IsMultiTargeted returns false. Project mode must fall back to an MSBuild
        // --getProperty:TargetFrameworks evaluate to discover the list and pin the FIRST TFM into the build.
        var csproj = WriteFile("App.csproj", ImportedMultiTargetExeCsproj);
        var discoveryQueried = false;
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
            {
                if (args.Contains("--getProperty:TargetFrameworks", StringComparison.Ordinal))
                {
                    discoveryQueried = true;
                    // C36: discovery requests two properties, so the SDK emits the JSON envelope (a diagnostic
                    // preamble can't corrupt the value). TargetFramework is empty on the outer node.
                    return (0, """{ "Properties": { "TargetFramework": "", "TargetFrameworks": "net10.0-windows10.0.26100.0;net8.0-windows10.0.26100.0" } }""", string.Empty);
                }

                return (0, PackagedPropertiesJson(), string.Empty);
            },
        };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsTrue(discoveryQueried, "the effective TargetFrameworks must be discovered via an MSBuild evaluate");
        Assert.IsNotNull(outcome.Resolution, "the project should resolve after pinning the discovered first TFM");
        StringAssert.Contains(dotnet.StreamingCalls[0], "-f net10.0-windows10.0.26100.0", "the first discovered TFM must be pinned into the build pass");
        Assert.IsFalse(dotnet.StreamingCalls[0].Contains("net8.0-windows10.0.26100.0"), "only the first discovered TFM should be pinned");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_InlineMultiTargeted_UsesStaticPath_NoDiscoveryEvaluate()
    {
        // C16 fast-path: an inline concrete <TargetFrameworks> is pinned by the cheap static scan, so NO
        // --getProperty:TargetFrameworks discovery evaluate should run (that MSBuild round-trip is only for
        // the imported/conditional case). Guards against regressing the common case into an extra evaluate.
        var csproj = WriteFile("App.csproj", MultiTargetedExeCsproj);
        var commandArgs = new List<string>();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args => { commandArgs.Add(args); return (0, PackagedPropertiesJson(), string.Empty); },
        };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(
            commandArgs.Any(a => a.Contains("--getProperty:TargetFrameworks", StringComparison.Ordinal)),
            "an inline multi-targeted project must be pinned statically, without a discovery evaluate");
        StringAssert.Contains(dotnet.StreamingCalls[0], "-f net10.0-windows10.0.26100.0", "the first inline TFM must still be pinned into the build pass");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_ConditionalMultiTargeted_UsesEvaluate_NotStaticGroup()
    {
        // C24: <TargetFrameworks> is declared in per-Configuration conditional groups. The static first-match
        // read would pin the Debug group's first TFM (net8) even for a Release run; project mode must instead
        // discover the effective list via an MSBuild evaluate (which honors -p:Configuration=Release) and pin
        // net10 — the Release group's first — NOT net8.
        var csproj = WriteFile("App.csproj", ConditionalMultiTargetExeCsproj);
        var discoveryQueried = false;
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
            {
                if (args.Contains("--getProperty:TargetFrameworks", StringComparison.Ordinal))
                {
                    discoveryQueried = true;
                    StringAssert.Contains(args, "-p:Configuration=Release", "the discovery evaluate must run under the run's Configuration");
                    // C36: two-property discovery → JSON envelope. The Release-conditional list (net10 first).
                    return (0, """{ "Properties": { "TargetFramework": "", "TargetFrameworks": "net10.0-windows10.0.26100.0;net8.0-windows10.0.26100.0" } }""", string.Empty);
                }

                return (0, PackagedPropertiesJson(), string.Empty);
            },
        };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Release", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsTrue(discoveryQueried, "a conditional <TargetFrameworks> must be resolved via the MSBuild evaluate, not the static group");
        StringAssert.Contains(dotnet.StreamingCalls[0], "-f net10.0-windows10.0.26100.0", "the Release group's first TFM must be pinned");
        Assert.IsFalse(dotnet.StreamingCalls[0].Contains("-f net8.0-windows10.0.26100.0"), "the Debug group's first TFM (net8) must NOT be pinned for a Release run");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NonJsonNonSpinner_StreamsBuildLinesLive()
    {
        // Change #1: in a non-json, non-spinner terminal the streamed build output must be visible.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var longBuildLine = "MSBUILD-LINE-" + new string('X', 120);
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, onOut, _) => { onOut?.Invoke(longBuildLine); return 0; },
            RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty),
        };
        var service = NewServiceWith(dotnet, LogLevel.Information, out var console);
        service.NativeTerminalGateOverrideForTests = () => false; // pin the non-TTY streaming path
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(console.Output, "Building", "the plain build banner should be shown");
        StringAssert.Contains(console.Output, longBuildLine,
            "streamed build output should remain visible without Spectre wrapping the subprocess line");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_JsonMode_StreamedBuildLinesNotOnStdout()
    {
        // Change #1: under --json the streamed build output must go to stderr, never stdout,
        // so the final stdout stays pure JSON.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, onOut, onErr) => { onOut?.Invoke("STDOUT-POISON"); onErr?.Invoke("STDERR-POISON"); return 0; },
            RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty),
        };
        var service = NewServiceWith(dotnet, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: true);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.IsFalse(console.Output.Contains("STDOUT-POISON"), "--json must not write build output to stdout");
        Assert.IsFalse(console.Output.Contains("STDERR-POISON"), "--json must not write build stderr to stdout");
    }

    [TestMethod]
    public async Task RunBuildPassAsync_InfoEnabledNonTty_StreamsHeaderInvocationWarningAndBuiltToStdout()
    {
        // Non-TTY info path (agent / CI / redirected / piped --verbose — ShouldUseLiveSpinner == false):
        // winapp prints the header, the dim `dotnet build …` invocation, streams build output live
        // (warnings included, on SUCCESS), then the persistent `✓ Built` line — all on stdout. Nothing is
        // hidden behind a spinner. (On a real TTY dotnet's terminal logger owns that output instead — see
        // RunBuildPassAsync_RealTerminal_* below.)
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, onOut, _) => { onOut?.Invoke("warning CS1998: this async method lacks await"); return 0; },
        };
        var console = new TestConsole();
        var service = new ProjectRunService(dotnet, NewDetection(dotnet), new FakeCsWinRTMetadataShimService(), console, new LevelLogger<ProjectRunService>(LogLevel.Information))
        {
            NativeTerminalGateOverrideForTests = () => false, // pin the non-TTY streaming path deterministically
        };
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var exit = await service.RunBuildPassAsync(csproj, options, _tempDir, csWinRTMetadataFolder: null, CancellationToken.None);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(console.Output, "Building", "the build header should be shown");
        StringAssert.Contains(console.Output, "dotnet build", "the exact dotnet invocation must be surfaced");
        StringAssert.Contains(console.Output, "warning CS1998", "success-path warnings must stay visible (not swallowed)");
        StringAssert.Contains(console.Output, "Built", "a persistent Built line should follow a successful build");
        Assert.AreEqual(1, dotnet.StreamingCalls.Count, "the non-TTY path must use the redirected streaming launcher");
        Assert.AreEqual(0, dotnet.InheritedCalls.Count, "the non-TTY path must NOT use the inherited-stdio launcher");
        StringAssert.Contains(dotnet.StreamingCalls[0], "-tl:off", "the redirected streaming path must pin -tl:off");
    }

    [TestMethod]
    public async Task RunBuildPassAsync_RealTerminal_UsesInheritedLauncherWithoutTlOffAndPrintsHeaderAndBuilt()
    {
        // Real interactive terminal (ShouldUseLiveSpinner == true, forced via the test seam): winapp prints
        // the header + dim invocation, then hands the console to dotnet with the INHERITED-stdio launcher so
        // its native terminal logger renders the live build. The build args must OMIT -tl:off (dotnet's
        // default -tl:auto → ON on a real TTY, which de-dupes warnings). winapp does not itself stream the
        // build lines here — dotnet owns that output — but still frames it with the header and `✓ Built`.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            // Inherited launcher: dotnet writes straight to the inherited console; winapp gets no callbacks.
            RunDotnetInheritedHandler = _ => 0,
            // If the streaming launcher were wrongly chosen this line would leak onto stdout and fail below.
            RunDotnetStreamingHandler = (_, onOut, _) => { onOut?.Invoke("STREAMING-LAUNCHER-WRONGLY-USED"); return 0; },
        };
        var console = new TestConsole();
        var service = new ProjectRunService(dotnet, NewDetection(dotnet), new FakeCsWinRTMetadataShimService(), console, new LevelLogger<ProjectRunService>(LogLevel.Information))
        {
            NativeTerminalGateOverrideForTests = () => true, // force the real-TTY / native-terminal branch
        };
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: true, Properties: [], Json: false);

        var exit = await service.RunBuildPassAsync(csproj, options, _tempDir, csWinRTMetadataFolder: null, CancellationToken.None);

        Assert.AreEqual(0, exit);
        Assert.AreEqual(1, dotnet.InheritedCalls.Count, "the real-TTY path must use the inherited-stdio launcher");
        Assert.AreEqual(0, dotnet.StreamingCalls.Count, "the real-TTY path must NOT use the redirected streaming launcher");
        Assert.IsFalse(dotnet.InheritedCalls[0].Contains("-tl:"), "the native-terminal build args must omit any -tl token (default auto → on)");
        StringAssert.Contains(console.Output, "Building", "the build header should still be shown above dotnet's output");
        StringAssert.Contains(console.Output, "dotnet build", "the exact dotnet invocation must still be surfaced");
        StringAssert.Contains(console.Output, "Built", "a persistent Built line should still follow a successful build");
        Assert.IsFalse(console.Output.Contains("STREAMING-LAUNCHER-WRONGLY-USED"), "the streaming launcher must not be used on a real TTY");
    }

    [TestMethod]
    public async Task RunBuildPassAsync_RealTerminalWithRestore_StreamsAndRedactsOutput()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, onOut, _) =>
            {
                onOut?.Invoke("NU1301 https://feed.example/v3/index.json?sig=BUILD_RESTORE_SECRET");
                return 0;
            },
        };
        var console = new TestConsole();
        var service = new ProjectRunService(dotnet, NewDetection(dotnet), new FakeCsWinRTMetadataShimService(), console, new LevelLogger<ProjectRunService>(LogLevel.Information))
        {
            NativeTerminalGateOverrideForTests = () => true,
        };
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var exit = await service.RunBuildPassAsync(
            csproj, options, _tempDir, csWinRTMetadataFolder: null, CancellationToken.None);

        Assert.AreEqual(0, exit);
        Assert.AreEqual(0, dotnet.InheritedCalls.Count,
            "a build that may restore must not bypass feed credential redaction");
        Assert.AreEqual(1, dotnet.StreamingCalls.Count);
        StringAssert.Contains(dotnet.StreamingCalls[0], "-tl:off");
        StringAssert.Contains(console.Output, "https://feed.example/v3/index.json?<redacted>");
        Assert.IsFalse(console.Output.Contains("BUILD_RESTORE_SECRET", StringComparison.Ordinal));
    }

    [TestMethod]
    [DoNotParallelize] // redirects the process-wide Console.Error
    public async Task RunBuildPassAsync_Json_KeepsStdoutClean_RoutesInvocationAndOutputToStderr()
    {
        // --json: stdout must stay pure JSON, so the invocation AND streamed build output go to stderr.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, onOut, onErr) =>
            {
                onOut?.Invoke("STDOUT-POISON https://feed.example/v3/index.json?sig=STDOUT_SECRET");
                onErr?.Invoke("STDERR-POISON https://feed.example/v3/index.json?sig=STDERR_SECRET");
                return 0;
            },
        };
        var console = new TestConsole();
        var service = new ProjectRunService(dotnet, NewDetection(dotnet), new FakeCsWinRTMetadataShimService(), console, new LevelLogger<ProjectRunService>(LogLevel.Information));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: true);

        var stderr = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(stderr);
        int exit;
        try
        {
            exit = await service.RunBuildPassAsync(csproj, options, _tempDir, csWinRTMetadataFolder: null, CancellationToken.None);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.AreEqual(0, exit);
        Assert.IsFalse(console.Output.Contains("STDOUT-POISON"), "--json must not write build output to stdout");
        Assert.IsFalse(console.Output.Contains("dotnet build"), "--json must not write the invocation to stdout");
        StringAssert.Contains(stderr.ToString(), "dotnet build", "--json must route the invocation to stderr");
        StringAssert.Contains(stderr.ToString(), "STDOUT-POISON", "--json must route build output to stderr");
        StringAssert.Contains(stderr.ToString(), "STDERR-POISON", "--json must route build stderr to stderr");
        Assert.IsFalse(stderr.ToString().Contains("STDOUT_SECRET", StringComparison.Ordinal));
        Assert.IsFalse(stderr.ToString().Contains("STDERR_SECRET", StringComparison.Ordinal));
    }

    [TestMethod]
    [DoNotParallelize] // redirects the process-wide Console.Error
    public async Task RunBuildPassAsync_Quiet_KeepsStdoutClean_RoutesOutputToStderr_SuppressesInvocation()
    {
        // --quiet (Information suppressed): stdout stays clean; build output still goes to stderr, but the
        // informational dotnet invocation is suppressed (unlike --json, which keeps it discoverable).
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, onOut, _) =>
            {
                onOut?.Invoke("QUIET-BUILD-LINE https://feed.example/v3/index.json?sig=QUIET_SECRET");
                return 0;
            },
        };
        var console = new TestConsole();
        var service = new ProjectRunService(dotnet, NewDetection(dotnet), new FakeCsWinRTMetadataShimService(), console, new LevelLogger<ProjectRunService>(LogLevel.Warning));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var stderr = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(stderr);
        int exit;
        try
        {
            exit = await service.RunBuildPassAsync(csproj, options, _tempDir, csWinRTMetadataFolder: null, CancellationToken.None);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.AreEqual(0, exit);
        Assert.IsFalse(console.Output.Contains("QUIET-BUILD-LINE"), "--quiet must not write build output to stdout");
        Assert.IsFalse(console.Output.Contains("dotnet build"), "--quiet must not write the invocation to stdout");
        Assert.IsFalse(stderr.ToString().Contains("dotnet build"), "--quiet must suppress the informational invocation");
        StringAssert.Contains(stderr.ToString(), "QUIET-BUILD-LINE", "--quiet must route build output to stderr");
        StringAssert.Contains(stderr.ToString(), "https://feed.example/v3/index.json?<redacted>");
        Assert.IsFalse(stderr.ToString().Contains("QUIET_SECRET", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RunBuildPassAsync_Failure_SurfacesDiagnosticsViaLiveStreamAndSkipsBuilt()
    {
        // On failure the diagnostics surface via the live stream (no capture buffer), a non-zero exit
        // propagates, and the ✓ Built line is NOT printed.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, _, onErr) => { onErr?.Invoke("error CS9999: the real failure"); return 1; },
        };
        var console = new TestConsole();
        var service = new ProjectRunService(dotnet, NewDetection(dotnet), new FakeCsWinRTMetadataShimService(), console, new LevelLogger<ProjectRunService>(LogLevel.Information))
        {
            NativeTerminalGateOverrideForTests = () => false, // pin the non-TTY streaming path (dotnet doesn't own output)
        };
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var exit = await service.RunBuildPassAsync(csproj, options, _tempDir, csWinRTMetadataFolder: null, CancellationToken.None);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(console.Output, "error CS9999: the real failure",
            "the live stream must reveal build diagnostics on failure");
        Assert.IsFalse(console.Output.Contains("Built"),
            "a failed build must not print the persistent Built line");
    }

    #endregion

    #region CheckSdkAsync

    [TestMethod]
    public async Task CheckSdkAsync_DotnetNotOnPath_ReturnsNotFoundError()
    {
        // Process.Start throws when dotnet is not on PATH → surfaced as an actionable install hint.
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => throw new System.ComponentModel.Win32Exception("not found") };
        var service = NewServiceWith(dotnet, out _);

        var error = await service.CheckSdkAsync(_tempDir, CancellationToken.None);

        Assert.IsNotNull(error);
        StringAssert.Contains(error!, "not found");
    }

    [TestMethod]
    public async Task CheckSdkAsync_NonZeroExit_ReturnsError()
    {
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (1, string.Empty, "boom") };
        var service = NewServiceWith(dotnet, out _);

        var error = await service.CheckSdkAsync(_tempDir, CancellationToken.None);

        Assert.IsNotNull(error);
        StringAssert.Contains(error!, "Could not determine");
    }

    [TestMethod]
    public async Task CheckSdkAsync_TooOldVersion_ReturnsError()
    {
        // 8.0.99 < 8.0.100 (the first SDK with --getProperty) → too old.
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, "8.0.99", string.Empty) };
        var service = NewServiceWith(dotnet, out _);

        var error = await service.CheckSdkAsync(_tempDir, CancellationToken.None);

        Assert.IsNotNull(error);
        StringAssert.Contains(error!, "too old");
    }

    [TestMethod]
    public async Task CheckSdkAsync_CapableVersion_ReturnsNull()
    {
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, "8.0.100", string.Empty) };
        var service = NewServiceWith(dotnet, out _);

        Assert.IsNull(await service.CheckSdkAsync(_tempDir, CancellationToken.None));
    }

    [TestMethod]
    public async Task CheckSdkAsync_NewerVersion_ReturnsNull()
    {
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, "10.0.301", string.Empty) };
        var service = NewServiceWith(dotnet, out _);

        Assert.IsNull(await service.CheckSdkAsync(_tempDir, CancellationToken.None));
    }

    [TestMethod]
    public async Task CheckSdkAsync_UnparseableVersion_ReturnsNull()
    {
        // Present but unparseable → assume a modern SDK; the build surfaces a real error if not.
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, "not-a-version", string.Empty) };
        var service = NewServiceWith(dotnet, out _);

        Assert.IsNull(await service.CheckSdkAsync(_tempDir, CancellationToken.None));
    }

    [TestMethod]
    public async Task CheckSdkAsync_CancellationDuringProbe_Rethrows()
    {
        // C8: a caller-requested cancellation during the `dotnet --version` probe must surface as
        // OperationCanceledException, not be swallowed and reported as a missing SDK.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => throw new OperationCanceledException() };
        var service = NewServiceWith(dotnet, out _);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.CheckSdkAsync(_tempDir, cts.Token));
    }

    #endregion
}
