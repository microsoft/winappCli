// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class MsixServiceTests
{
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"MsixTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            _tempDir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Creates a minimal AppxManifest.xml with Package root element containing
    /// the specified InProcessServer and ProxyStub entries.
    /// </summary>
    private FileInfo CreateAppxManifest(
        string filename,
        (string dllName, string[] activatableClasses)[]? inProcessServers = null,
        (string dllName, string classId, (string interfaceId, string name)[] interfaces)[]? proxyStubs = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version='1.0' encoding='utf-8'?>");
        sb.AppendLine("<Package xmlns='http://schemas.microsoft.com/appx/manifest/foundation/windows10'>");
        sb.AppendLine("  <Extensions>");

        if (inProcessServers != null)
        {
            foreach (var (dllName, classes) in inProcessServers)
            {
                sb.AppendLine("    <Extension Category='windows.activatableClass.inProcessServer'>");
                sb.AppendLine("      <InProcessServer>");
                sb.AppendLine($"        <Path>{dllName}</Path>");
                foreach (var cls in classes)
                {
                    sb.AppendLine($"        <ActivatableClass ActivatableClassId='{cls}' ThreadingModel='both'/>");
                }
                sb.AppendLine("      </InProcessServer>");
                sb.AppendLine("    </Extension>");
            }
        }

        if (proxyStubs != null)
        {
            foreach (var (dllName, classId, interfaces) in proxyStubs)
            {
                sb.AppendLine($"    <Extension Category='windows.activatableClass.proxyStub'>");
                sb.AppendLine($"      <ProxyStub ClassId='{classId}'>");
                sb.AppendLine($"        <Path>{dllName}</Path>");
                foreach (var (interfaceId, name) in interfaces)
                {
                    sb.AppendLine($"        <Interface InterfaceId='{interfaceId}' Name='{name}'/>");
                }
                sb.AppendLine("      </ProxyStub>");
                sb.AppendLine("    </Extension>");
            }
        }

        sb.AppendLine("  </Extensions>");
        sb.AppendLine("</Package>");

        var path = Path.Combine(_tempDir.FullName, filename);
        File.WriteAllText(path, sb.ToString());
        return new FileInfo(path);
    }

    /// <summary>
    /// Creates a minimal package.appxfragment with Fragment root element containing
    /// the specified InProcessServer entries.
    /// </summary>
    private FileInfo CreateAppxFragment(
        string filename,
        (string dllName, string[] activatableClasses)[]? inProcessServers = null,
        (string dllName, string classId, (string interfaceId, string name)[] interfaces)[]? proxyStubs = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version='1.0' encoding='utf-8'?>");
        sb.AppendLine("<Fragment xmlns='http://schemas.microsoft.com/appx/manifest/foundation/windows10'>");
        sb.AppendLine("  <Extensions>");

        if (inProcessServers != null)
        {
            foreach (var (dllName, classes) in inProcessServers)
            {
                sb.AppendLine("    <Extension Category='windows.activatableClass.inProcessServer'>");
                sb.AppendLine("      <InProcessServer>");
                sb.AppendLine($"        <Path>{dllName}</Path>");
                foreach (var cls in classes)
                {
                    sb.AppendLine($"        <ActivatableClass ActivatableClassId='{cls}' ThreadingModel='both'/>");
                }
                sb.AppendLine("      </InProcessServer>");
                sb.AppendLine("    </Extension>");
            }
        }

        if (proxyStubs != null)
        {
            foreach (var (dllName, classId, interfaces) in proxyStubs)
            {
                sb.AppendLine($"    <Extension Category='windows.activatableClass.proxyStub'>");
                sb.AppendLine($"      <ProxyStub ClassId='{classId}'>");
                sb.AppendLine($"        <Path>{dllName}</Path>");
                foreach (var (interfaceId, name) in interfaces)
                {
                    sb.AppendLine($"        <Interface InterfaceId='{interfaceId}' Name='{name}'/>");
                }
                sb.AppendLine("      </ProxyStub>");
                sb.AppendLine("    </Extension>");
            }
        }

        sb.AppendLine("  </Extensions>");
        sb.AppendLine("</Fragment>");

        var path = Path.Combine(_tempDir.FullName, filename);
        File.WriteAllText(path, sb.ToString());
        return new FileInfo(path);
    }

    #region AppendAppManifestFromAppx: Package manifest tests

    [TestMethod]
    public void AppendAppManifestFromAppx_PackageManifest_GeneratesInProcessServerEntries()
    {
        // Arrange
        var manifest = CreateAppxManifest("AppxManifest.xml",
            inProcessServers:
            [
                ("Microsoft.WindowsAppRuntime.dll",
                [
                    "Microsoft.Windows.AppLifecycle.ActivationRegistrationManager",
                    "Microsoft.Windows.AppLifecycle.AppInstance"
                ]),
                ("Microsoft.Windows.ApplicationModel.DynamicDependency.dll",
                [
                    "Microsoft.Windows.ApplicationModel.DynamicDependency.PackageDependency"
                ])
            ]);

        var sb = new StringBuilder();
        var dllFiles = new List<string> { "Microsoft.WindowsAppRuntime.dll", "SomeOther.dll" };

        // Act
        MsixService.AppendAppManifestFromAppx(sb, redirectDlls: false, inDllFiles: dllFiles, inAppxManifests: [manifest]);
        var result = sb.ToString();

        // Assert
        Assert.Contains("Microsoft.WindowsAppRuntime.dll", result);
        Assert.Contains("Microsoft.Windows.AppLifecycle.ActivationRegistrationManager", result);
        Assert.Contains("Microsoft.Windows.AppLifecycle.AppInstance", result);
        Assert.Contains("Microsoft.Windows.ApplicationModel.DynamicDependency.PackageDependency", result);
        Assert.Contains("<winrtv1:activatableClass", result);
        Assert.Contains("threadingModel='both'", result);
    }

    [TestMethod]
    public void AppendAppManifestFromAppx_PackageManifest_GeneratesProxyStubEntries()
    {
        // Arrange
        var manifest = CreateAppxManifest("AppxManifest.xml",
            proxyStubs:
            [
                ("Microsoft.WindowsAppRuntime.dll",
                "A5C66C11-43A1-4277-9CF3-6E7C0E101F86",
                [
                    ("50BBD3E4-2B46-4BC3-B1D3-F809AAE78943", "IAppActivationArguments"),
                    ("29C78C83-23A4-4FE1-A5CB-0E9753FA72E0", "IAppActivationArguments2")
                ])
            ]);

        var sb = new StringBuilder();

        // Act
        MsixService.AppendAppManifestFromAppx(sb, redirectDlls: false, inDllFiles: [], inAppxManifests: [manifest]);
        var result = sb.ToString();

        // Assert
        Assert.Contains("<asmv3:comClass clsid='{A5C66C11-43A1-4277-9CF3-6E7C0E101F86}'/>", result);
        Assert.Contains("<asmv3:comInterfaceProxyStub name='IAppActivationArguments' iid='{50BBD3E4-2B46-4BC3-B1D3-F809AAE78943}'/>", result);
        Assert.Contains("<asmv3:comInterfaceProxyStub name='IAppActivationArguments2' iid='{29C78C83-23A4-4FE1-A5CB-0E9753FA72E0}'/>", result);
    }

    [TestMethod]
    public void AppendAppManifestFromAppx_ExcludesBlacklistedProxyStubs()
    {
        // Arrange: PushNotificationsLongRunningTask and Widgets are excluded
        var manifest = CreateAppxManifest("AppxManifest.xml",
            proxyStubs:
            [
                ("PushNotificationsLongRunningTask.ProxyStub.dll",
                "AAAA0000-0000-0000-0000-000000000001",
                [("BBBB0000-0000-0000-0000-000000000001", "ISomePushInterface")]),
                ("Microsoft.Windows.Widgets.dll",
                "AAAA0000-0000-0000-0000-000000000002",
                [("BBBB0000-0000-0000-0000-000000000002", "ISomeWidgetInterface")]),
                ("Legit.ProxyStub.dll",
                "CCCC0000-0000-0000-0000-000000000003",
                [("DDDD0000-0000-0000-0000-000000000003", "ILegitInterface")])
            ]);

        var sb = new StringBuilder();

        // Act
        MsixService.AppendAppManifestFromAppx(sb, redirectDlls: false, inDllFiles: [], inAppxManifests: [manifest]);
        var result = sb.ToString();

        // Assert: excluded DLLs should not appear
        Assert.DoesNotContain("PushNotificationsLongRunningTask", result);
        Assert.DoesNotContain("Microsoft.Windows.Widgets.dll", result);

        // Assert: legit proxy stub should appear
        Assert.Contains("Legit.ProxyStub.dll", result);
        Assert.Contains("ILegitInterface", result);
    }

    #endregion

    #region AppendAppManifestFromAppx: Fragment manifest tests

    [TestMethod]
    public void AppendAppManifestFromAppx_FragmentManifest_GeneratesInProcessServerEntries()
    {
        // Arrange
        var fragment = CreateAppxFragment("package.appxfragment",
            inProcessServers:
            [
                ("Microsoft.Windows.AI.Search.Experimental.dll",
                [
                    "Microsoft.Windows.AI.Search.Experimental.SemanticSearchManager"
                ])
            ]);

        var sb = new StringBuilder();

        // Act
        MsixService.AppendAppManifestFromAppx(sb, redirectDlls: false, inDllFiles: [], inAppxManifests: [fragment]);
        var result = sb.ToString();

        // Assert
        Assert.Contains("Microsoft.Windows.AI.Search.Experimental.dll", result);
        Assert.Contains("Microsoft.Windows.AI.Search.Experimental.SemanticSearchManager", result);
        Assert.Contains("<winrtv1:activatableClass", result);
    }

    #endregion

    #region AppendAppManifestFromAppx: Mixed manifest tests (Package + Fragment)

    [TestMethod]
    public void AppendAppManifestFromAppx_MixedManifests_ProcessesBothTypesInSingleCall()
    {
        // Arrange: main AppxManifest.xml (Package) + fragment (Fragment)
        var mainManifest = CreateAppxManifest("AppxManifest.xml",
            inProcessServers:
            [
                ("Microsoft.WindowsAppRuntime.dll",
                [
                    "Microsoft.Windows.AppLifecycle.AppInstance",
                    "Microsoft.Windows.AppNotifications.AppNotificationManager"
                ])
            ],
            proxyStubs:
            [
                ("Microsoft.WindowsAppRuntime.dll",
                "A5C66C11-43A1-4277-9CF3-6E7C0E101F86",
                [("50BBD3E4-2B46-4BC3-B1D3-F809AAE78943", "IAppActivationArguments")])
            ]);

        var fragment = CreateAppxFragment("package.appxfragment",
            inProcessServers:
            [
                ("Microsoft.Windows.AI.Video.dll",
                [
                    "Microsoft.Windows.AI.Video.VideoSuperResolution"
                ])
            ]);

        var sb = new StringBuilder();
        var dllFiles = new List<string>
        {
            "Microsoft.WindowsAppRuntime.dll",
            "Microsoft.Windows.AI.Video.dll"
        };

        // Act: single call with both Package and Fragment manifests
        MsixService.AppendAppManifestFromAppx(sb, redirectDlls: false, inDllFiles: dllFiles, inAppxManifests: [mainManifest, fragment]);
        var result = sb.ToString();

        // Assert: entries from main Package manifest
        Assert.Contains("Microsoft.Windows.AppLifecycle.AppInstance", result);
        Assert.Contains("Microsoft.Windows.AppNotifications.AppNotificationManager", result);
        Assert.Contains("<asmv3:comClass clsid='{A5C66C11-43A1-4277-9CF3-6E7C0E101F86}'/>", result);
        Assert.Contains("IAppActivationArguments", result);

        // Assert: entries from Fragment
        Assert.Contains("Microsoft.Windows.AI.Video.dll", result);
        Assert.Contains("Microsoft.Windows.AI.Video.VideoSuperResolution", result);
    }

    [TestMethod]
    public void AppendAppManifestFromAppx_MixedManifests_AllEntriesWrappedInFileElements()
    {
        // Arrange
        var mainManifest = CreateAppxManifest("AppxManifest.xml",
            inProcessServers:
            [
                ("RuntimeDll.dll", ["Namespace.ClassA"])
            ]);

        var fragment = CreateAppxFragment("fragment.appxfragment",
            inProcessServers:
            [
                ("FragmentDll.dll", ["Namespace.ClassB"])
            ]);

        var sb = new StringBuilder();

        // Act
        MsixService.AppendAppManifestFromAppx(sb, redirectDlls: false, inDllFiles: [], inAppxManifests: [mainManifest, fragment]);
        var result = sb.ToString();

        // Assert: each DLL should be wrapped in asmv3:file
        Assert.Contains("<asmv3:file name='RuntimeDll.dll'>", result);
        Assert.Contains("<asmv3:file name='FragmentDll.dll'>", result);

        // Count file open and close tags — should match
        var openCount = CountOccurrences(result, "<asmv3:file name=");
        var closeCount = CountOccurrences(result, "</asmv3:file>");
        Assert.AreEqual(openCount, closeCount, "Every <asmv3:file> should have a matching </asmv3:file>");
    }

    [TestMethod]
    public void AppendAppManifestFromAppx_EmptyManifest_ProducesNoOutput()
    {
        // Arrange: manifest with no InProcessServer or ProxyStub entries
        var manifest = CreateAppxManifest("empty.xml");
        var sb = new StringBuilder();

        // Act
        MsixService.AppendAppManifestFromAppx(sb, redirectDlls: false, inDllFiles: [], inAppxManifests: [manifest]);

        // Assert
        Assert.AreEqual(0, sb.Length, "Empty manifest should produce no output");
    }

    [TestMethod]
    public void AppendAppManifestFromAppx_NoManifests_ProducesNoOutput()
    {
        // Arrange
        var sb = new StringBuilder();

        // Act
        MsixService.AppendAppManifestFromAppx(sb, redirectDlls: false, inDllFiles: [], inAppxManifests: []);

        // Assert
        Assert.AreEqual(0, sb.Length, "No manifests should produce no output");
    }

    #endregion

    #region AppendAppManifestFromAppx: redirectDlls tests

    [TestMethod]
    public void AppendAppManifestFromAppx_WithRedirectDlls_AddsLoadFromAttribute()
    {
        // Arrange
        var manifest = CreateAppxManifest("AppxManifest.xml",
            inProcessServers:
            [
                ("Some.Runtime.dll", ["Some.Runtime.SomeClass"])
            ]);

        var sb = new StringBuilder();

        // Act
        MsixService.AppendAppManifestFromAppx(sb, redirectDlls: true, inDllFiles: ["Some.Runtime.dll", "Leftover.dll"], inAppxManifests: [manifest]);
        var result = sb.ToString();

        // Assert: InProcessServer DLL should have loadFrom
        Assert.Contains("loadFrom='%MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY%Some.Runtime.dll'", result);

        // Assert: leftover DLLs (not in any InProcessServer or ProxyStub) should also be listed for Package manifests
        Assert.Contains("Leftover.dll", result);
    }

    [TestMethod]
    public void AppendAppManifestFromAppx_WithRedirectDlls_FragmentDoesNotAddLeftoverDlls()
    {
        // Arrange: Fragment manifest — leftover DLLs should NOT be added
        var fragment = CreateAppxFragment("fragment.appxfragment",
            inProcessServers:
            [
                ("Fragment.dll", ["Fragment.SomeClass"])
            ]);

        var sb = new StringBuilder();

        // Act
        MsixService.AppendAppManifestFromAppx(sb, redirectDlls: true, inDllFiles: ["Fragment.dll", "ShouldNotAppear.dll"], inAppxManifests: [fragment]);
        var result = sb.ToString();

        // Assert: Fragment DLL should be present
        Assert.Contains("Fragment.dll", result);

        // Assert: leftover DLL should NOT appear (fragments don't add leftover DLLs)
        Assert.DoesNotContain("ShouldNotAppear.dll", result);
    }

    #endregion

    #region End-to-end: full manifest generation

    [TestMethod]
    public void EndToEnd_FullManifest_CombinesPackageFragmentAndProxyStubs()
    {
        // Arrange: simulate WinAppSDK layout with main manifest + fragment
        var mainManifest = CreateAppxManifest("AppxManifest.xml",
            inProcessServers:
            [
                ("Microsoft.WindowsAppRuntime.dll",
                [
                    "Microsoft.Windows.AppLifecycle.ActivationRegistrationManager",
                    "Microsoft.Windows.AppLifecycle.AppInstance",
                    "Microsoft.Windows.AppNotifications.AppNotificationManager"
                ]),
                ("Microsoft.Windows.ApplicationModel.DynamicDependency.dll",
                [
                    "Microsoft.Windows.ApplicationModel.DynamicDependency.PackageDependency"
                ])
            ],
            proxyStubs:
            [
                ("Microsoft.WindowsAppRuntime.dll",
                "A5C66C11-43A1-4277-9CF3-6E7C0E101F86",
                [
                    ("50BBD3E4-2B46-4BC3-B1D3-F809AAE78943", "IAppActivationArguments"),
                    ("29C78C83-23A4-4FE1-A5CB-0E9753FA72E0", "IAppActivationArguments2")
                ])
            ]);

        var fragment1 = CreateAppxFragment("fragment1.appxfragment",
            inProcessServers:
            [
                ("Microsoft.Windows.AI.Search.Experimental.dll",
                ["Microsoft.Windows.AI.Search.Experimental.SemanticSearchManager"])
            ]);

        var fragment2 = CreateAppxFragment("fragment2.appxfragment",
            inProcessServers:
            [
                ("Microsoft.Windows.AI.Video.dll",
                ["Microsoft.Windows.AI.Video.VideoSuperResolution"])
            ]);

        // Build full SxS manifest
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version='1.0' encoding='utf-8' standalone='yes'?>");
        sb.AppendLine("<assembly manifestVersion='1.0'");
        sb.AppendLine("    xmlns:asmv3='urn:schemas-microsoft-com:asm.v3'");
        sb.AppendLine("    xmlns:winrtv1='urn:schemas-microsoft-com:winrt.v1'");
        sb.AppendLine("    xmlns='urn:schemas-microsoft-com:asm.v1'>");

        var allDllFiles = new List<string>
        {
            "Microsoft.WindowsAppRuntime.dll",
            "Microsoft.Windows.ApplicationModel.DynamicDependency.dll",
            "Microsoft.Windows.AI.Search.Experimental.dll",
            "Microsoft.Windows.AI.Video.dll"
        };

        // Single pass — all manifests together
        MsixService.AppendAppManifestFromAppx(
            sb,
            redirectDlls: false,
            inDllFiles: allDllFiles,
            inAppxManifests: [mainManifest, fragment1, fragment2]);

        sb.AppendLine("</assembly>");

        var manifest = sb.ToString();

        // Assert: XML header and wrapper
        Assert.Contains("<?xml version='1.0'", manifest);
        Assert.Contains("<assembly manifestVersion='1.0'", manifest);
        Assert.Contains("</assembly>", manifest);

        // Assert: InProcessServer entries from main package
        Assert.Contains("Microsoft.Windows.AppLifecycle.ActivationRegistrationManager", manifest);
        Assert.Contains("Microsoft.Windows.AppLifecycle.AppInstance", manifest);
        Assert.Contains("Microsoft.Windows.AppNotifications.AppNotificationManager", manifest);
        Assert.Contains("Microsoft.Windows.ApplicationModel.DynamicDependency.PackageDependency", manifest);

        // Assert: ProxyStub entries from main package
        Assert.Contains("<asmv3:comClass clsid='{A5C66C11-43A1-4277-9CF3-6E7C0E101F86}'/>", manifest);
        Assert.Contains("IAppActivationArguments", manifest);
        Assert.Contains("IAppActivationArguments2", manifest);
        Assert.Contains("<asmv3:comInterfaceProxyStub", manifest);

        // Assert: InProcessServer entries from fragments
        Assert.Contains("Microsoft.Windows.AI.Search.Experimental.dll", manifest);
        Assert.Contains("Microsoft.Windows.AI.Search.Experimental.SemanticSearchManager", manifest);
        Assert.Contains("Microsoft.Windows.AI.Video.dll", manifest);
        Assert.Contains("Microsoft.Windows.AI.Video.VideoSuperResolution", manifest);

        // Assert: all asmv3:file elements are properly closed
        var openCount = CountOccurrences(manifest, "<asmv3:file name=");
        var closeCount = CountOccurrences(manifest, "</asmv3:file>");
        Assert.AreEqual(openCount, closeCount, $"Mismatched file elements: {openCount} opens vs {closeCount} closes");
        Assert.IsGreaterThanOrEqualTo(openCount, 5, $"Expected at least 5 file elements (2 InProcessServer DLLs + 2 fragment DLLs + 1 ProxyStub DLL), got {openCount}");
    }

    #endregion

    #region AddBuildMetadata Tests

    [TestMethod]
    public void AddBuildMetadata_CreatesSection_WhenNoneExists()
    {
        var manifest = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package
  xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
  IgnorableNamespaces=""uap"">
  <Identity Name=""TestApp"" Version=""1.0.0.0"" />
</Package>";

        var result = MsixService.AddBuildMetadata(manifest);

        Assert.Contains("xmlns:build=", result, "Should add build namespace");
        Assert.Contains("build:Metadata", result, "Should create build:Metadata section");
        Assert.Contains(@"Name=""Microsoft.WinAppCli""", result, "Should add WinAppCli item");
        Assert.Contains("Version=", result, "Should include version");
        // build should be in IgnorableNamespaces
        Assert.Contains("IgnorableNamespaces=\"uap build\"", result,
            "Should add 'build' to IgnorableNamespaces");
    }

    [TestMethod]
    public void AddBuildMetadata_AddsNamespace_WhenBuildNamespaceMissing()
    {
        var manifest = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package
  xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
  IgnorableNamespaces=""uap rescap"">
  <Identity Name=""TestApp"" Version=""1.0.0.0"" />
</Package>";

        var result = MsixService.AddBuildMetadata(manifest);

        Assert.Contains("xmlns:build=\"http://schemas.microsoft.com/developer/appx/2015/build\"", result);
        Assert.Contains("IgnorableNamespaces=\"uap rescap build\"", result,
            "Should append 'build' to existing IgnorableNamespaces");
    }

    [TestMethod]
    public void AddBuildMetadata_PreservesExistingBuildNamespace()
    {
        var manifest = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package
  xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
  xmlns:build=""http://schemas.microsoft.com/developer/appx/2015/build""
  IgnorableNamespaces=""uap build"">
  <Identity Name=""TestApp"" Version=""1.0.0.0"" />
</Package>";

        var result = MsixService.AddBuildMetadata(manifest);

        // Should not duplicate the namespace
        Assert.AreEqual(1, CountOccurrences(result, "xmlns:build="),
            "Should not duplicate build namespace");
        Assert.AreEqual(1, CountOccurrences(result, "<build:Metadata>"),
            "Should create exactly one build:Metadata section");
    }

    [TestMethod]
    public void AddBuildMetadata_AppendsToExistingSection()
    {
        var manifest = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package
  xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
  xmlns:build=""http://schemas.microsoft.com/developer/appx/2015/build""
  IgnorableNamespaces=""uap build"">
  <Identity Name=""TestApp"" Version=""1.0.0.0"" />
  <build:Metadata>
    <build:Item Name=""SomeOtherTool"" Version=""2.0.0"" />
  </build:Metadata>
</Package>";

        var result = MsixService.AddBuildMetadata(manifest);

        Assert.Contains(@"Name=""SomeOtherTool""", result, "Should preserve existing items");
        Assert.Contains(@"Name=""Microsoft.WinAppCli""", result, "Should add WinAppCli item");
        Assert.AreEqual(1, CountOccurrences(result, "<build:Metadata>"),
            "Should not duplicate build:Metadata section");
    }

    [TestMethod]
    public void AddBuildMetadata_UpdatesExistingWinAppCliEntry()
    {
        var manifest = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package
  xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
  xmlns:build=""http://schemas.microsoft.com/developer/appx/2015/build""
  IgnorableNamespaces=""uap build"">
  <Identity Name=""TestApp"" Version=""1.0.0.0"" />
  <build:Metadata>
    <build:Item Name=""Microsoft.WinAppCli"" Version=""0.0.1"" />
  </build:Metadata>
</Package>";

        var result = MsixService.AddBuildMetadata(manifest);

        // Should have exactly one WinAppCli entry (updated, not duplicated)
        Assert.AreEqual(1, CountOccurrences(result, @"Name=""Microsoft.WinAppCli"""),
            "Should not duplicate WinAppCli entry");
        // Old version should be gone
        Assert.DoesNotContain(@"Version=""0.0.1""", result,
            "Should replace old version");
    }

    [TestMethod]
    public void AddBuildMetadata_IsIdempotent()
    {
        var manifest = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package
  xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
  IgnorableNamespaces=""uap"">
  <Identity Name=""TestApp"" Version=""1.0.0.0"" />
</Package>";

        var first = MsixService.AddBuildMetadata(manifest);
        var second = MsixService.AddBuildMetadata(first);

        Assert.AreEqual(first, second, "Calling AddBuildMetadata twice should produce the same result");
    }

    [TestMethod]
    public void AddBuildMetadata_CreatesIgnorableNamespaces_WhenAttributeMissing()
    {
        // Minimal manifest with no IgnorableNamespaces attribute at all
        // (matches the test manifest in SignCommandWithMismatchedMsixPublishers)
        var manifest = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""TestApp"" Version=""1.0.0.0"" />
</Package>";

        var result = MsixService.AddBuildMetadata(manifest);

        Assert.Contains("xmlns:build=", result, "Should add build namespace");
        Assert.Contains("IgnorableNamespaces=\"build\"", result,
            "Should create IgnorableNamespaces attribute with 'build' when none existed");
        Assert.Contains("<build:Metadata>", result, "Should create build:Metadata section");
        Assert.Contains(@"Name=""Microsoft.WinAppCli""", result, "Should add WinAppCli item");
    }

    #endregion

    #region FindManifestInDirectory Tests

    [TestMethod]
    public void FindManifestInDirectory_FindsAppxManifestXml()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir.FullName, "appxmanifest.xml"), "<Package/>");

        // Act
        var result = MsixService.FindManifestInDirectory(_tempDir);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("appxmanifest.xml", result.Name);
    }

    [TestMethod]
    public void FindManifestInDirectory_FindsPackageAppxManifest()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir.FullName, "Package.appxmanifest"), "<Package/>");

        // Act
        var result = MsixService.FindManifestInDirectory(_tempDir);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("Package.appxmanifest", result.Name);
    }

    [TestMethod]
    public void FindManifestInDirectory_PrefersPackageAppxManifest_WhenBothExist()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir.FullName, "appxmanifest.xml"), "<Package/>");
        File.WriteAllText(Path.Combine(_tempDir.FullName, "Package.appxmanifest"), "<Package/>");

        // Act
        var result = MsixService.FindManifestInDirectory(_tempDir);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("Package.appxmanifest", result.Name);
    }

    [TestMethod]
    public void FindManifestInDirectory_ReturnsNull_WhenNoManifest()
    {
        // Arrange — empty directory

        // Act
        var result = MsixService.FindManifestInDirectory(_tempDir);

        // Assert
        Assert.IsNull(result);
    }

    #endregion

    #region DetectPeArchitecture Tests

    [TestMethod]
    public void DetectPeArchitecture_ReturnsNull_ForNonExistentFile()
    {
        // Act
        var result = PeHelper.DetectPeArchitecture(Path.Combine(_tempDir.FullName, "nonexistent.exe"));

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DetectPeArchitecture_ReturnsNull_ForNonPeFile()
    {
        // Arrange — create a file that's not a PE
        var path = Path.Combine(_tempDir.FullName, "notape.exe");
        File.WriteAllText(path, "This is not a PE file");

        // Act
        var result = PeHelper.DetectPeArchitecture(path);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DetectPeArchitecture_ReturnsNull_ForTruncatedFile()
    {
        // Arrange — create a very small file
        var path = Path.Combine(_tempDir.FullName, "tiny.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A]); // Just MZ header, nothing else

        // Act
        var result = PeHelper.DetectPeArchitecture(path);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    [DataRow((ushort)0x014C, "x86", DisplayName = "x86 (IMAGE_FILE_MACHINE_I386)")]
    [DataRow((ushort)0x8664, "x64", DisplayName = "x64 (IMAGE_FILE_MACHINE_AMD64)")]
    [DataRow((ushort)0xAA64, "arm64", DisplayName = "arm64 (IMAGE_FILE_MACHINE_ARM64)")]
    [DataRow((ushort)0x01C4, "arm", DisplayName = "arm (IMAGE_FILE_MACHINE_ARMNT)")]
    public void DetectPeArchitecture_ReturnsExpected_ForValidPeHeader(ushort machineType, string expected)
    {
        var pe = BuildMinimalNativePe(machineType);
        var path = Path.Combine(_tempDir.FullName, $"test_{machineType:X4}.exe");
        File.WriteAllBytes(path, pe);

        // Act
        var result = PeHelper.DetectPeArchitecture(path);

        // Assert
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void DetectPeArchitecture_ReturnsNull_ForUnknownMachineType()
    {
        // Arrange — valid PE structure but with an unrecognized Machine value (0xFFFF)
        var pe = BuildMinimalNativePe(0xFFFF);
        var path = Path.Combine(_tempDir.FullName, "unknown_machine.exe");
        File.WriteAllBytes(path, pe);

        // Act
        var result = PeHelper.DetectPeArchitecture(path);

        // Assert
        Assert.IsNull(result);
    }

    /// <summary>
    /// Builds a minimal valid native PE file (no COR header) with the given Machine value.
    /// Uses PE32 for 32-bit machine types and PE32+ for 64-bit machine types.
    /// </summary>
    private static byte[] BuildMinimalNativePe(ushort machineType)
    {
        bool is64Bit = machineType is 0x8664 or 0xAA64;
        ushort optHeaderSize = is64Bit ? (ushort)0xF0 : (ushort)0xE0;
        int coffStart = 0x84;
        var pe = new byte[coffStart + 20 + optHeaderSize + 64];

        pe[0] = 0x4D; pe[1] = 0x5A; // MZ
        BitConverter.GetBytes(0x80).CopyTo(pe, 0x3C); // e_lfanew
        pe[0x80] = 0x50; pe[0x81] = 0x45; // PE\0\0

        // COFF header
        BitConverter.GetBytes(machineType).CopyTo(pe, coffStart);
        BitConverter.GetBytes(optHeaderSize).CopyTo(pe, coffStart + 16); // SizeOfOptionalHeader
        pe[coffStart + 18] = 0x02; // Characteristics = EXECUTABLE_IMAGE

        // Optional header
        int optStart = coffStart + 20;
        if (is64Bit)
        {
            pe[optStart] = 0x0B; pe[optStart + 1] = 0x02; // PE32+ magic
            BitConverter.GetBytes(16).CopyTo(pe, optStart + 108); // NumberOfRvaAndSizes
        }
        else
        {
            pe[optStart] = 0x0B; pe[optStart + 1] = 0x01; // PE32 magic
            BitConverter.GetBytes(16).CopyTo(pe, optStart + 92); // NumberOfRvaAndSizes
        }

        return pe;
    }

    #endregion

    #region AutoDetectProcessorArchitecture Tests

    private const string ManifestWithoutArch = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
            <Identity Name="TestApp" Publisher="CN=Test" Version="1.0.0.0" />
        </Package>
        """;

    private const string ManifestWithX86Arch = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
            <Identity Name="TestApp" Publisher="CN=Test" Version="1.0.0.0" ProcessorArchitecture="x86" />
        </Package>
        """;

    private const string ManifestWithNeutralArch = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
            <Identity Name="TestApp" Publisher="CN=Test" Version="1.0.0.0" ProcessorArchitecture="neutral" />
        </Package>
        """;

    private static TaskContext CreateTestTaskContext()
    {
        var task = new GroupableTask("test", null);
        var console = new Spectre.Console.Testing.TestConsole();
        var logger = NullLogger<MsixService>.Instance;
        var renderLock = new Lock();
        return new TaskContext(task, null, console, logger, renderLock);
    }

    [TestMethod]
    public void AutoDetectProcessorArchitecture_SetsArch_WhenMissingFromManifest()
    {
        // Arrange — x64 PE, manifest without ProcessorArchitecture
        var exePath = Path.Combine(_tempDir.FullName, "test.exe");
        File.WriteAllBytes(exePath, BuildMinimalNativePe(0x8664)); // x64
        var taskContext = CreateTestTaskContext();

        // Act
        var (content, arch) = MsixService.AutoDetectProcessorArchitecture(ManifestWithoutArch, exePath, taskContext);

        // Assert
        Assert.AreEqual("x64", arch);
        StringAssert.Contains(content, "ProcessorArchitecture=\"x64\"");
    }

    [TestMethod]
    public void AutoDetectProcessorArchitecture_SetsArm64_WhenMissingFromManifest()
    {
        // Arrange — arm64 PE, manifest without ProcessorArchitecture
        var exePath = Path.Combine(_tempDir.FullName, "test.exe");
        File.WriteAllBytes(exePath, BuildMinimalNativePe(0xAA64)); // arm64
        var taskContext = CreateTestTaskContext();

        // Act
        var (content, arch) = MsixService.AutoDetectProcessorArchitecture(ManifestWithoutArch, exePath, taskContext);

        // Assert
        Assert.AreEqual("arm64", arch);
        StringAssert.Contains(content, "ProcessorArchitecture=\"arm64\"");
    }

    [TestMethod]
    public void AutoDetectProcessorArchitecture_ReturnsExistingArch_WhenMatchesExe()
    {
        // Arrange — x86 PE, manifest already has x86
        var exePath = Path.Combine(_tempDir.FullName, "test.exe");
        File.WriteAllBytes(exePath, BuildMinimalNativePe(0x014C)); // x86
        var taskContext = CreateTestTaskContext();

        // Act
        var (content, arch) = MsixService.AutoDetectProcessorArchitecture(ManifestWithX86Arch, exePath, taskContext);

        // Assert — manifest unchanged, architecture returned
        Assert.AreEqual("x86", arch);
        Assert.AreEqual(ManifestWithX86Arch, content, "Manifest should not be modified when arch matches");
    }

    [TestMethod]
    public void AutoDetectProcessorArchitecture_ReturnsExistingArch_WhenMismatch()
    {
        // Arrange — x64 PE, manifest says x86 → should warn but keep existing
        var exePath = Path.Combine(_tempDir.FullName, "test.exe");
        File.WriteAllBytes(exePath, BuildMinimalNativePe(0x8664)); // x64
        var taskContext = CreateTestTaskContext();

        // Act
        var (content, arch) = MsixService.AutoDetectProcessorArchitecture(ManifestWithX86Arch, exePath, taskContext);

        // Assert — returns existing arch, does not modify manifest
        Assert.AreEqual("x86", arch);
        Assert.AreEqual(ManifestWithX86Arch, content, "Manifest should not be modified on mismatch");
    }

    [TestMethod]
    public void AutoDetectProcessorArchitecture_SkipsWarning_WhenNeutral()
    {
        // Arrange — x64 PE, manifest says "neutral" → should not warn
        var exePath = Path.Combine(_tempDir.FullName, "test.exe");
        File.WriteAllBytes(exePath, BuildMinimalNativePe(0x8664)); // x64
        var taskContext = CreateTestTaskContext();

        // Act
        var (content, arch) = MsixService.AutoDetectProcessorArchitecture(ManifestWithNeutralArch, exePath, taskContext);

        // Assert — returns "neutral", no modification
        Assert.AreEqual("neutral", arch);
        Assert.AreEqual(ManifestWithNeutralArch, content);
    }

    [TestMethod]
    public void AutoDetectProcessorArchitecture_ReturnsExistingArch_WhenExeNotFound()
    {
        // Arrange — non-existent exe path, manifest has no arch
        var exePath = Path.Combine(_tempDir.FullName, "nonexistent.exe");
        var taskContext = CreateTestTaskContext();

        // Act
        var (content, arch) = MsixService.AutoDetectProcessorArchitecture(ManifestWithoutArch, exePath, taskContext);

        // Assert — returns null arch (can't detect), manifest unchanged
        Assert.IsNull(arch);
        Assert.AreEqual(ManifestWithoutArch, content);
    }

    [TestMethod]
    public void AutoDetectProcessorArchitecture_ReturnsExistingArch_WhenExeIsNotPe()
    {
        // Arrange — non-PE file, manifest already has arch
        var exePath = Path.Combine(_tempDir.FullName, "notape.exe");
        File.WriteAllText(exePath, "not a PE file");
        var taskContext = CreateTestTaskContext();

        // Act
        var (content, arch) = MsixService.AutoDetectProcessorArchitecture(ManifestWithX86Arch, exePath, taskContext);

        // Assert — returns existing manifest arch when PE detection fails
        Assert.AreEqual("x86", arch);
        Assert.AreEqual(ManifestWithX86Arch, content);
    }

    #endregion

    #region ContainsXGenerateLanguage / ReplaceXGenerateLanguage Tests

    [TestMethod]
    public void ContainsXGenerateLanguage_ReturnsTrueForXGenerateManifest()
    {
        var manifest = @"<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Resources>
    <Resource Language=""x-generate""/>
  </Resources>
</Package>";

        Assert.IsTrue(MsixService.ContainsXGenerateLanguage(manifest));
    }

    [TestMethod]
    public void ContainsXGenerateLanguage_ReturnsFalseForConcreteLanguage()
    {
        var manifest = @"<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Resources>
    <Resource Language=""en-US""/>
  </Resources>
</Package>";

        Assert.IsFalse(MsixService.ContainsXGenerateLanguage(manifest));
    }

    [TestMethod]
    public void ContainsXGenerateLanguage_ReturnsFalseForNoResources()
    {
        var manifest = @"<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""><Identity Name=""Test""/></Package>";

        Assert.IsFalse(MsixService.ContainsXGenerateLanguage(manifest));
    }

    [TestMethod]
    public void ReplaceXGenerateLanguage_ReplacesSingleLanguage()
    {
        var manifest = @"<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Resources>
    <Resource Language=""x-generate""/>
  </Resources>
</Package>";

        var result = MsixService.ReplaceXGenerateLanguage(manifest, ["en-US"]);

        Assert.Contains(@"Language=""en-US""", result);
        Assert.DoesNotContain("x-generate", result);
    }

    [TestMethod]
    public void ReplaceXGenerateLanguage_ReplacesMultipleLanguages()
    {
        var manifest = @"<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Resources>
    <Resource Language=""x-generate""/>
  </Resources>
</Package>";

        var result = MsixService.ReplaceXGenerateLanguage(manifest, ["en-US", "fr-FR", "de-DE"]);

        Assert.Contains(@"Language=""en-US""", result);
        Assert.Contains(@"Language=""fr-FR""", result);
        Assert.Contains(@"Language=""de-DE""", result);
        Assert.DoesNotContain("x-generate", result);
    }

    [TestMethod]
    public void ReplaceXGenerateLanguage_PreservesRestOfManifest()
    {
        var manifest = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""TestApp"" Version=""1.0.0.0""/>
  <Resources>
    <Resource Language=""x-generate""/>
  </Resources>
  <Applications/>
</Package>";

        var result = MsixService.ReplaceXGenerateLanguage(manifest, ["en-US"]);

        Assert.Contains(@"<Identity Name=""TestApp""", result);
        Assert.Contains("Applications", result);
        Assert.Contains(@"Language=""en-US""", result);
        Assert.DoesNotContain("x-generate", result);
    }

    [TestMethod]
    public void ReplaceXGenerateLanguage_HandlesVariousWhitespace()
    {
        var manifest = "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">\n<Resources>\n<Resource Language=\"x-generate\" />\n</Resources>\n</Package>";

        var result = MsixService.ReplaceXGenerateLanguage(manifest, ["en-US"]);

        Assert.Contains(@"Language=""en-US""", result);
        Assert.DoesNotContain("x-generate", result);
    }

    [TestMethod]
    public void ContainsXGenerateLanguage_HandlesSingleQuotes()
    {
        var manifest = @"<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Resources>
    <Resource Language='x-generate'/>
  </Resources>
</Package>";

        Assert.IsTrue(MsixService.ContainsXGenerateLanguage(manifest));
    }

    #endregion

    #region Sparse Manifest VisualElements Tests

    [TestMethod]
    public async Task UpdateAppxManifestContentAsync_AddsAppListEntry_WhenVisualElementsTagEndsWithAngleBracket()
    {
        // Arrange
        var service = CreateMsixServiceForManifestRewriteTests();
        var manifest = """
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
    <Identity Name="TestApp" Publisher="CN=Test" Version="1.0.0.0" />
    <Properties>
        <DisplayName>Test App</DisplayName>
    </Properties>
    <Capabilities>
        <rescap:Capability Name="runFullTrust" />
    </Capabilities>
    <Applications>
        <Application Id="App" Executable="TestApp.dll" EntryPoint="Windows.FullTrustApplication">
            <uap:VisualElements DisplayName="Test App" Square150x150Logo="Assets\\Logo.png">
            </uap:VisualElements>
        </Application>
    </Applications>
</Package>
""";

        // Act
        var result = await InvokeUpdateAppxManifestContentAsync(service, manifest);

        // Assert
        StringAssert.Contains(result, "<uap:VisualElements DisplayName=\"Test App\" Square150x150Logo=\"Assets\\\\Logo.png\" AppListEntry=\"none\">");
    }

    [TestMethod]
    public async Task UpdateAppxManifestContentAsync_AddsAppListEntry_WhenVisualElementsTagSelfCloses()
    {
        // Arrange
        var service = CreateMsixServiceForManifestRewriteTests();
        var manifest = """
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
    <Identity Name="TestApp" Publisher="CN=Test" Version="1.0.0.0" />
    <Properties>
        <DisplayName>Test App</DisplayName>
    </Properties>
    <Capabilities>
        <rescap:Capability Name="runFullTrust" />
    </Capabilities>
    <Applications>
        <Application Id="App" Executable="TestApp.dll" EntryPoint="Windows.FullTrustApplication">
            <uap:VisualElements DisplayName="Test App" Square150x150Logo="Assets\\Logo.png" />
        </Application>
    </Applications>
</Package>
""";

        // Act
        var result = await InvokeUpdateAppxManifestContentAsync(service, manifest);

        // Assert
        StringAssert.Contains(result, "<uap:VisualElements DisplayName=\"Test App\" Square150x150Logo=\"Assets\\\\Logo.png\" AppListEntry=\"none\" />");
    }

    [TestMethod]
    public async Task UpdateAppxManifestContentAsync_Sparse_FolderInput_CorrectsFromManifestExecutable()
    {
        // Arrange — folder packing supplies no explicit entryPointPath, so sparse status and the
        // executable kind must be derived from the manifest itself. A folder whose manifest is
        // sparse (AllowExternalContent) with a legacy RuntimeBehavior="packagedClassicApp" must
        // still be corrected to win32App.
        var service = CreateMsixServiceForManifestRewriteTests();
        var manifest = """
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                 xmlns:desktop6="http://schemas.microsoft.com/appx/manifest/desktop/windows10/6"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
    <Identity Name="TestApp" Publisher="CN=Test" Version="1.0.0.0" />
    <Properties>
        <DisplayName>Test App</DisplayName>
        <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
    </Properties>
    <Capabilities>
        <rescap:Capability Name="runFullTrust" />
    </Capabilities>
    <Applications>
        <Application Id="App" Executable="TestApp.exe" uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="packagedClassicApp" />
    </Applications>
</Package>
""";

        // Act — no explicit entry point (folder input); sparse status comes from the manifest.
        var result = await InvokeUpdateAppxManifestContentAsync(service, manifest, entryPointPath: null, sparse: true);

        // Assert — the sparse rewrite was applied using the manifest's own executable.
        StringAssert.Contains(result, "uap10:RuntimeBehavior=\"win32App\"");
        Assert.IsFalse(result.Contains("packagedClassicApp"), "Pre-existing packagedClassicApp must be corrected to win32App for folder inputs");
        StringAssert.Contains(result, "uap10:TrustLevel=\"mediumIL\"");
    }

    [TestMethod]
    public async Task UpdateAppxManifestContentAsync_Sparse_CorrectsPreExistingRuntimeBehavior()
    {
        // Arrange — a manifest that already declares uap10:TrustLevel and an INCORRECT
        // RuntimeBehavior="packagedClassicApp". Converting an EXE to a sparse Win32 package must
        // force RuntimeBehavior="win32App" even though TrustLevel is already present.
        var service = CreateMsixServiceForManifestRewriteTests();
        var manifest = """
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
    <Identity Name="TestApp" Publisher="CN=Test" Version="1.0.0.0" />
    <Properties>
        <DisplayName>Test App</DisplayName>
    </Properties>
    <Capabilities>
        <rescap:Capability Name="runFullTrust" />
    </Capabilities>
    <Applications>
        <Application Id="App" Executable="TestApp.exe" uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="packagedClassicApp" />
    </Applications>
</Package>
""";

        // Act — sparse conversion of an .exe entry point.
        var result = await InvokeUpdateAppxManifestContentAsync(service, manifest, "TestApp.exe", sparse: true);

        // Assert
        StringAssert.Contains(result, "uap10:RuntimeBehavior=\"win32App\"");
        Assert.IsFalse(result.Contains("packagedClassicApp"), "Pre-existing packagedClassicApp must be corrected to win32App");
        StringAssert.Contains(result, "uap10:TrustLevel=\"mediumIL\"");
    }

    [TestMethod]
    public async Task UpdateAppxManifestContentAsync_Sparse_FolderInput_RaisesMinVersionFloor()
    {
        // Arrange — a sparse folder created from an older template keeps MinVersion 10.0.18362.0.
        // AllowExternalContent requires 10.0.19041.0, so the folder rewrite must raise it just as
        // the manifest-file path does; otherwise MakeAppx /nv packs an MSIX deployment rejects.
        var service = CreateMsixServiceForManifestRewriteTests();
        var manifest = """
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10">
    <Identity Name="TestApp" Publisher="CN=Test" Version="1.0.0.0" ProcessorArchitecture="neutral" />
    <Properties>
        <DisplayName>Test App</DisplayName>
        <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
    </Properties>
    <Dependencies>
        <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.18362.0" MaxVersionTested="10.0.19041.0" />
    </Dependencies>
    <Applications>
        <Application Id="App" Executable="TestApp.exe" uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="win32App" />
    </Applications>
</Package>
""";

        var result = await InvokeUpdateAppxManifestContentAsync(service, manifest, entryPointPath: null, sparse: true);

        StringAssert.Contains(result, "MinVersion=\"10.0.19041.0\"");
        Assert.IsFalse(result.Contains("MinVersion=\"10.0.18362.0\""), "MinVersion below the AllowExternalContent floor must be raised for folder inputs");
    }

    private MsixService CreateMsixServiceForManifestRewriteTests()
    {
        return new MsixService(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            NullLogger<MsixService>.Instance,
            new CurrentDirectoryProvider(_tempDir.FullName));
    }

    private static Task<string> InvokeUpdateAppxManifestContentAsync(MsixService service, string manifest)
        => InvokeUpdateAppxManifestContentAsync(service, manifest, "TestApp.dll", sparse: true);

    private static async Task<string> InvokeUpdateAppxManifestContentAsync(MsixService service, string manifest, string? entryPointPath, bool sparse)
    {
        var updateMethod = typeof(MsixService).GetMethod("UpdateAppxManifestContentAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(updateMethod, "Could not locate UpdateAppxManifestContentAsync via reflection");

        // selfContained=true avoids dependency mutation paths, keeping this test focused
        // exePath=null skips ProcessorArchitecture detection
        var resultTask = updateMethod.Invoke(service,
        [
            manifest,
            null,
            entryPointPath,
            null,
            sparse,
            true,
            null,
            CreateTestTaskContext(),
            CancellationToken.None
        ]) as dynamic;

        Assert.IsNotNull(resultTask, "Reflection call did not return a Task");
        var result = await resultTask;
        // Named tuple members aren't available via reflection; use Item1 (Content)
        return result.Item1;
    }

    #endregion

    #region ExtractExecutionAliases tests

    [TestMethod]
    public void ExtractExecutionAliases_WithUap5Alias_ReturnsAlias()
    {
        // Arrange
        var manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5">
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <uap5:Extension Category="windows.appExecutionAlias">
                      <uap5:AppExecutionAlias>
                        <uap5:ExecutionAlias Alias="myapp.exe" />
                      </uap5:AppExecutionAlias>
                    </uap5:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """;

        // Act
        var aliases = MsixService.ExtractExecutionAliases(manifest);

        // Assert
        Assert.AreEqual(1, aliases.Count);
        Assert.AreEqual("myapp.exe", aliases[0]);
    }

    [TestMethod]
    public void ExtractExecutionAliases_WithDesktopAlias_ReturnsAlias()
    {
        // Arrange
        var manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10">
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <desktop:Extension Category="windows.appExecutionAlias">
                      <desktop:ExecutionAlias Alias="desktopapp.exe" />
                    </desktop:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """;

        // Act
        var aliases = MsixService.ExtractExecutionAliases(manifest);

        // Assert
        Assert.AreEqual(1, aliases.Count);
        Assert.AreEqual("desktopapp.exe", aliases[0]);
    }

    [TestMethod]
    public void ExtractExecutionAliases_WithMultipleAliases_ReturnsAll()
    {
        // Arrange
        var manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5">
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <uap5:Extension Category="windows.appExecutionAlias">
                      <uap5:AppExecutionAlias>
                        <uap5:ExecutionAlias Alias="app1.exe" />
                        <uap5:ExecutionAlias Alias="app2.exe" />
                      </uap5:AppExecutionAlias>
                    </uap5:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """;

        // Act
        var aliases = MsixService.ExtractExecutionAliases(manifest);

        // Assert
        Assert.AreEqual(2, aliases.Count);
        Assert.AreEqual("app1.exe", aliases[0]);
        Assert.AreEqual("app2.exe", aliases[1]);
    }

    [TestMethod]
    public void ExtractExecutionAliases_NoAliases_ReturnsEmptyList()
    {
        // Arrange
        var manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="App" Executable="app.exe">
                </Application>
              </Applications>
            </Package>
            """;

        // Act
        var aliases = MsixService.ExtractExecutionAliases(manifest);

        // Assert
        Assert.AreEqual(0, aliases.Count);
    }

    #endregion

    #region InsertPackageLevelExtensions tests

    [TestMethod]
    public void InsertPackageLevelExtensions_WithExistingPackageLevelExtensions_InsertsBeforeClose()
    {
        // Arrange — manifest has both Application-level and Package-level <Extensions>
        var manifest = @"<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Applications>
    <Application Id=""App"" Executable=""app.exe"">
      <Extensions>
        <Extension Category=""windows.appExecutionAlias"" />
      </Extensions>
    </Application>
  </Applications>
  <Extensions>
    <Extension Category=""windows.activatableClass.proxyStub"" />
  </Extensions>
</Package>";
        var newEntry = @"<Extension xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"" Category=""windows.activatableClass.inProcessServer"" />";

        // Act
        var result = MsixService.InsertPackageLevelExtensions(manifest, newEntry);

        // Assert — new entry should appear inside the Package-level <Extensions>, not the Application-level one
        Assert.IsTrue(result.Contains("inProcessServer"), "Should contain the new entry");
        var appExtIndex = result.IndexOf("windows.appExecutionAlias", StringComparison.Ordinal);
        var proxyStubIndex = result.IndexOf("windows.activatableClass.proxyStub", StringComparison.Ordinal);
        var inProcessIndex = result.IndexOf("windows.activatableClass.inProcessServer", StringComparison.Ordinal);
        Assert.IsTrue(inProcessIndex > proxyStubIndex, "InProcessServer should be in Package-level Extensions (after proxyStub)");
        Assert.IsTrue(inProcessIndex > appExtIndex, "InProcessServer should be after Application-level Extensions");
    }

    [TestMethod]
    public void InsertPackageLevelExtensions_WithOnlyApplicationLevelExtensions_CreatesNewPackageLevelBlock()
    {
        // Arrange — manifest has ONLY Application-level <Extensions> (the regression scenario)
        var manifest = @"<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Applications>
    <Application Id=""App"" Executable=""app.exe"">
      <Extensions>
        <Extension Category=""windows.appExecutionAlias"" />
      </Extensions>
    </Application>
  </Applications>
</Package>";
        var newEntry = @"<Extension xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"" Category=""windows.activatableClass.inProcessServer"" />";

        // Act
        var result = MsixService.InsertPackageLevelExtensions(manifest, newEntry);

        // Assert — should create a NEW Package-level <Extensions> block, NOT insert into Application-level one
        Assert.IsTrue(result.Contains("inProcessServer"), "Should contain the new entry");

        // The new entry must appear AFTER </Applications>
        var applicationsCloseIndex = result.IndexOf("</Applications>", StringComparison.Ordinal);
        var inProcessIndex = result.IndexOf("windows.activatableClass.inProcessServer", StringComparison.Ordinal);
        Assert.IsTrue(inProcessIndex > applicationsCloseIndex,
            "InProcessServer must be outside <Applications> (Package-level), not inside Application-level Extensions");

        // Should have two separate <Extensions> blocks
        Assert.AreEqual(2, CountOccurrences(result, "<Extensions"),
            "Should have Application-level + new Package-level <Extensions> blocks");
    }

    [TestMethod]
    public void InsertPackageLevelExtensions_WithNoExtensions_CreatesNewPackageLevelBlock()
    {
        // Arrange — manifest has no <Extensions> at all
        var manifest = @"<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Applications>
    <Application Id=""App"" Executable=""app.exe"" />
  </Applications>
</Package>";
        var newEntry = @"<Extension xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"" Category=""windows.activatableClass.inProcessServer"" />";

        // Act
        var result = MsixService.InsertPackageLevelExtensions(manifest, newEntry);

        // Assert
        Assert.IsTrue(result.Contains("inProcessServer"), "Should contain the new entry");
        var packageCloseIndex = result.IndexOf("</Package>", StringComparison.Ordinal);
        var extensionsIndex = result.IndexOf("<Extensions", StringComparison.Ordinal);
        Assert.IsTrue(extensionsIndex > result.IndexOf("</Applications>", StringComparison.Ordinal),
            "New <Extensions> block should be after </Applications>");
        Assert.IsTrue(extensionsIndex < packageCloseIndex,
            "New <Extensions> block should be before </Package>");
    }

    #endregion

    #region UnregisterExistingPackageAsync Tests

    private static MsixService CreateMsixServiceForUnregister(IPackageRegistrationService prs, string cwd)
    {
        return new MsixService(
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            prs,
            null!, null!,
            NullLogger<MsixService>.Instance,
            new CurrentDirectoryProvider(cwd));
    }

    [TestMethod]
    public async Task UnregisterExistingPackageAsync_NoInstalls_ReturnsFalse()
    {
        var fake = new FakePackageRegistrationService { FakeDevPackages = [] };
        var svc = CreateMsixServiceForUnregister(fake, _tempDir.FullName);

        var result = await svc.UnregisterExistingPackageAsync("MyApp", CreateTestTaskContext());

        Assert.IsFalse(result);
        Assert.AreEqual(0, fake.UnregisterByFullNameCalls.Count);
        Assert.AreEqual(0, fake.UnregisterCalls.Count, "Should never use the name-based API");
    }

    [TestMethod]
    public async Task UnregisterExistingPackageAsync_DevModePackage_RemovesByFullNameWithPreserve()
    {
        var fake = new FakePackageRegistrationService
        {
            FakeDevPackages =
            [
                new DevPackageInfo("MyApp_1.0.0.0_x64__abc", "MyApp", "1.0.0.0",
                    Path.Combine(_tempDir.FullName, "AppX"), IsDevelopmentMode: true)
            ]
        };
        var svc = CreateMsixServiceForUnregister(fake, _tempDir.FullName);

        var result = await svc.UnregisterExistingPackageAsync("MyApp", CreateTestTaskContext(), preserveAppData: true);

        Assert.IsTrue(result);
        Assert.AreEqual(1, fake.UnregisterByFullNameCalls.Count);
        Assert.AreEqual("MyApp_1.0.0.0_x64__abc", fake.UnregisterByFullNameCalls[0].PackageFullName);
        Assert.IsTrue(fake.UnregisterByFullNameCalls[0].PreserveAppData);
    }

    [TestMethod]
    public async Task UnregisterExistingPackageAsync_WithPublisher_IgnoresSameNameFromAnotherPublisher()
    {
        var fake = new FakePackageRegistrationService
        {
            FakeDevPackages =
            [
                new("MyApp_1.0.0.0_x64__expected", "MyApp", "1.0.0.0", _tempDir.FullName, true, "CN=Expected"),
                new("MyApp_1.0.0.0_x64__external", "MyApp", "1.0.0.0", @"C:\External", true, "CN=External"),
            ],
        };
        var svc = CreateMsixServiceForUnregister(fake, _tempDir.FullName);

        var result = await svc.UnregisterExistingPackageAsync(
            "MyApp",
            CreateTestTaskContext(),
            publisher: "CN=Expected");

        Assert.IsTrue(result);
        Assert.HasCount(1, fake.UnregisterByFullNameCalls);
        Assert.AreEqual("MyApp_1.0.0.0_x64__expected", fake.UnregisterByFullNameCalls[0].PackageFullName);
    }

    [TestMethod]
    public async Task UnregisterExistingPackageAsync_NonDevInTree_RemovesWithoutPreservingAppData()
    {
        var inTreeLocation = Path.Combine(_tempDir.FullName, "PackageInstall");
        var fake = new FakePackageRegistrationService
        {
            FakeDevPackages =
            [
                new DevPackageInfo("MyApp_1.0.0.0_x64__store", "MyApp", "1.0.0.0",
                    inTreeLocation, IsDevelopmentMode: false)
            ]
        };
        var svc = CreateMsixServiceForUnregister(fake, _tempDir.FullName);

        var result = await svc.UnregisterExistingPackageAsync("MyApp", CreateTestTaskContext(), preserveAppData: true);

        Assert.IsTrue(result);
        Assert.AreEqual(1, fake.UnregisterByFullNameCalls.Count);
        Assert.AreEqual("MyApp_1.0.0.0_x64__store", fake.UnregisterByFullNameCalls[0].PackageFullName);
        Assert.IsFalse(fake.UnregisterByFullNameCalls[0].PreserveAppData,
            "Non-dev-mode packages cannot be removed with PreserveApplicationData");
    }

    [TestMethod]
    public async Task UnregisterExistingPackageAsync_NonDevOutOfTree_ThrowsActionableException()
    {
        var outOfTreeLocation = Path.Combine(Path.GetTempPath(), $"OutsideTree_{Guid.NewGuid():N}");
        var fake = new FakePackageRegistrationService
        {
            FakeDevPackages =
            [
                new DevPackageInfo("MyApp_2.0.0.0_x64__store", "MyApp", "2.0.0.0",
                    outOfTreeLocation, IsDevelopmentMode: false)
            ]
        };
        var svc = CreateMsixServiceForUnregister(fake, _tempDir.FullName);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UnregisterExistingPackageAsync("MyApp", CreateTestTaskContext()));

        StringAssert.Contains(ex.Message, "MyApp_2.0.0.0_x64__store");
        StringAssert.Contains(ex.Message, "Get-AppxPackage MyApp | Remove-AppxPackage");
        Assert.AreEqual(0, fake.UnregisterByFullNameCalls.Count, "Must not remove anything when refusing");
    }

    [TestMethod]
    public async Task UnregisterExistingPackageAsync_SiblingDirectoryWithSharedPrefix_TreatedAsOutOfTree()
    {
        // Regression test for the StartsWith() prefix bug:
        // cwd = ...\proj should NOT include a sibling at ...\project2.
        var projDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "proj"));
        projDir.Create();
        var siblingDir = Path.Combine(_tempDir.FullName, "project2", "AppX");

        var fake = new FakePackageRegistrationService
        {
            FakeDevPackages =
            [
                new DevPackageInfo("MyApp_1.0.0.0_x64__store", "MyApp", "1.0.0.0",
                    siblingDir, IsDevelopmentMode: false)
            ]
        };
        var svc = CreateMsixServiceForUnregister(fake, projDir.FullName);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UnregisterExistingPackageAsync("MyApp", CreateTestTaskContext()));

        Assert.AreEqual(0, fake.UnregisterByFullNameCalls.Count,
            "Sibling directory must not be treated as inside the project tree");
    }

    [TestMethod]
    public async Task UnregisterExistingPackageAsync_MixedPackages_OutOfTreeOneRefusesAllRemovals()
    {
        // Critical regression test for H1: classification must happen up front so that
        // the dev-mode package isn't removed before the out-of-tree non-dev one is checked.
        var inTreeDevLocation = Path.Combine(_tempDir.FullName, "DevAppX");
        var outOfTreeStoreLocation = Path.Combine(Path.GetTempPath(), $"StoreOutside_{Guid.NewGuid():N}");
        var fake = new FakePackageRegistrationService
        {
            FakeDevPackages =
            [
                new DevPackageInfo("MyApp_1.0.0.0_x64__dev", "MyApp", "1.0.0.0",
                    inTreeDevLocation, IsDevelopmentMode: true),
                new DevPackageInfo("MyApp_2.0.0.0_x64__store", "MyApp", "2.0.0.0",
                    outOfTreeStoreLocation, IsDevelopmentMode: false),
            ]
        };
        var svc = CreateMsixServiceForUnregister(fake, _tempDir.FullName);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UnregisterExistingPackageAsync("MyApp", CreateTestTaskContext()));

        Assert.AreEqual(0, fake.UnregisterByFullNameCalls.Count,
            "No package may be removed when an out-of-tree non-dev install is present");
    }

    [TestMethod]
    public void IsPathInsideDirectory_RejectsSiblingWithSharedPrefix()
    {
        var container = Path.Combine(_tempDir.FullName, "proj");
        var sibling = Path.Combine(_tempDir.FullName, "project2");

        Assert.IsFalse(MsixService.IsPathInsideDirectory(sibling, container));
    }

    [TestMethod]
    public void IsPathInsideDirectory_AcceptsActualChild()
    {
        var container = Path.Combine(_tempDir.FullName, "proj");
        var child = Path.Combine(_tempDir.FullName, "proj", "sub", "AppX");

        Assert.IsTrue(MsixService.IsPathInsideDirectory(child, container));
    }

    [TestMethod]
    public void IsPathInsideDirectory_NullOrEmptyCandidate_ReturnsFalse()
    {
        Assert.IsFalse(MsixService.IsPathInsideDirectory(null, _tempDir.FullName));
        Assert.IsFalse(MsixService.IsPathInsideDirectory(string.Empty, _tempDir.FullName));
    }

    [TestMethod]
    public async Task UnregisterExistingPackageAsync_CancellationFromRemoval_PropagatesNotSwallowed()
    {
        // Regression test: the catch-all in this method must not swallow OperationCanceledException
        // (otherwise StatusService treats cancel as a normal "no package removed" outcome).
        var fake = new FakePackageRegistrationService
        {
            FakeDevPackages =
            [
                new DevPackageInfo("MyApp_1.0.0.0_x64__abc", "MyApp", "1.0.0.0",
                    Path.Combine(_tempDir.FullName, "AppX"), IsDevelopmentMode: true)
            ],
            UnregisterByFullNameThrows = new OperationCanceledException("user cancelled")
        };
        var svc = CreateMsixServiceForUnregister(fake, _tempDir.FullName);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => svc.UnregisterExistingPackageAsync("MyApp", CreateTestTaskContext()));
    }

    #endregion

    #region IsExistingRegistrationUpToDate Tests (issue #537)

    private (MsixService Svc, FakePackageRegistrationService Fake, DirectoryInfo OutputDir, FileInfo NewManifest, byte[]? PreviousBytes) CreateSkipRegistrationFixture(
        string manifestBody,
        bool isDevelopmentMode = true,
        string? installLocationOverride = null,
        int packageCount = 1,
        bool capturePreviousManifest = true,
        string? previousManifestBody = null)
    {
        var outputDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "AppX"));
        outputDir.Create();

        // Snapshot a "previous" registration's manifest BEFORE we write the new one — this is
        // what the production code path captures via TryReadExistingLayoutManifestBytes before
        // sync/copy overwrites the file.
        byte[]? previousBytes = null;
        if (capturePreviousManifest)
        {
            previousBytes = Encoding.UTF8.GetBytes(previousManifestBody ?? manifestBody);
        }

        var newManifestPath = new FileInfo(Path.Combine(outputDir.FullName, "AppxManifest.xml"));
        File.WriteAllText(newManifestPath.FullName, manifestBody);

        // The "installed" location for the dev package. Defaults to the same directory as the
        // newly-written manifest (the happy path for skip). Callers can override to simulate
        // a package registered from a different location.
        var installLocation = installLocationOverride ?? outputDir.FullName;

        var devPackages = new List<DevPackageInfo>();
        for (int i = 0; i < packageCount; i++)
        {
            devPackages.Add(new DevPackageInfo(
                FullName: $"MyApp_1.0.0.{i}_x64__abc",
                Name: "MyApp",
                Version: $"1.0.0.{i}",
                InstallLocation: installLocation,
                IsDevelopmentMode: isDevelopmentMode));
        }

        var fake = new FakePackageRegistrationService { FakeDevPackages = devPackages };
        var svc = CreateMsixServiceForUnregister(fake, _tempDir.FullName);
        return (svc, fake, outputDir, newManifestPath, previousBytes);
    }

    [TestMethod]
    public void IsExistingRegistrationUpToDate_MatchingDevPackage_ReturnsTrue()
    {
        var fixture = CreateSkipRegistrationFixture("<Package />");

        var result = fixture.Svc.IsExistingRegistrationUpToDate(
            "MyApp", fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir, CreateTestTaskContext());

        Assert.IsTrue(result, "Identical dev-mode package at the same location should be considered up to date");
        Assert.AreEqual(1, fixture.Fake.FindDevPackagesCalls.Count);
    }

    [TestMethod]
    public void IsExistingRegistrationUpToDate_NoPreviousManifestSnapshot_ReturnsFalse()
    {
        // First run: no manifest existed in outputAppXDirectory before, so we have nothing
        // to compare against and must take the unregister+register path.
        var fixture = CreateSkipRegistrationFixture("<Package />", capturePreviousManifest: false);

        var result = fixture.Svc.IsExistingRegistrationUpToDate(
            "MyApp", fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir, CreateTestTaskContext());

        Assert.IsFalse(result);
        Assert.AreEqual(0, fixture.Fake.FindDevPackagesCalls.Count, "Should short-circuit before querying packages when there's nothing to compare");
    }

    [TestMethod]
    public void IsExistingRegistrationUpToDate_NoInstalledPackage_ReturnsFalse()
    {
        var fixture = CreateSkipRegistrationFixture("<Package />", packageCount: 0);

        var result = fixture.Svc.IsExistingRegistrationUpToDate(
            "MyApp", fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir, CreateTestTaskContext());

        Assert.IsFalse(result, "Zero installed packages means a fresh registration is needed");
    }

    [TestMethod]
    public void IsExistingRegistrationUpToDate_MultiplePackages_ReturnsFalse()
    {
        var fixture = CreateSkipRegistrationFixture("<Package />", packageCount: 2);

        var result = fixture.Svc.IsExistingRegistrationUpToDate(
            "MyApp", fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir, CreateTestTaskContext());

        Assert.IsFalse(result, "Multiple installed packages must trigger the cleanup path");
    }

    [TestMethod]
    public void IsExistingRegistrationUpToDate_NonDevModePackage_ReturnsFalse()
    {
        var fixture = CreateSkipRegistrationFixture("<Package />", isDevelopmentMode: false);

        var result = fixture.Svc.IsExistingRegistrationUpToDate(
            "MyApp", fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir, CreateTestTaskContext());

        Assert.IsFalse(result, "Non-dev-mode packages must always go through the unregister path");
    }

    [TestMethod]
    public void IsExistingRegistrationUpToDate_DifferentInstallLocation_ReturnsFalse()
    {
        var otherLocation = Path.Combine(_tempDir.FullName, "OtherInstall");
        var fixture = CreateSkipRegistrationFixture(
            "<Package />",
            installLocationOverride: otherLocation);

        var result = fixture.Svc.IsExistingRegistrationUpToDate(
            "MyApp", fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir, CreateTestTaskContext());

        Assert.IsFalse(result, "A package installed from a different location must be re-registered");
    }

    [TestMethod]
    public void IsExistingRegistrationUpToDate_ManifestContentDiffers_ReturnsFalse()
    {
        // Simulate: user edited Package.appxmanifest between runs. The previous bytes (what was
        // registered before) differ from the new manifest now on disk.
        var fixture = CreateSkipRegistrationFixture(
            manifestBody:          "<Package version=\"new\"/>",
            previousManifestBody:  "<Package version=\"old\"/>");

        var result = fixture.Svc.IsExistingRegistrationUpToDate(
            "MyApp", fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir, CreateTestTaskContext());

        Assert.IsFalse(result, "Manifest changes must trigger re-registration");
    }

    [TestMethod]
    public void IsExistingRegistrationUpToDate_ManifestSizeMatchesButBytesDiffer_ReturnsFalse()
    {
        // Both strings are equal length, different bytes — exercises the byte-level comparison
        // path past the early size-mismatch shortcut.
        var fixture = CreateSkipRegistrationFixture(
            manifestBody:          "<Package a/>",
            previousManifestBody:  "<Package b/>");

        var result = fixture.Svc.IsExistingRegistrationUpToDate(
            "MyApp", fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir, CreateTestTaskContext());

        Assert.IsFalse(result, "Equal-length but byte-different manifests must trigger re-registration");
    }

    [TestMethod]
    public void IsExistingRegistrationUpToDate_InstallLocationMissing_ReturnsFalse()
    {
        var fixture = CreateSkipRegistrationFixture(
            manifestBody: "<Package />",
            installLocationOverride: string.Empty);

        var result = fixture.Svc.IsExistingRegistrationUpToDate(
            "MyApp", fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir, CreateTestTaskContext());

        Assert.IsFalse(result, "Empty install location must be treated as unknown");
    }

    [TestMethod]
    public void IsExistingRegistrationUpToDate_NewManifestMissing_ReturnsFalse()
    {
        // We have a previous snapshot but the new manifest was never written to disk — the
        // cheap existence check must short-circuit to false before querying packages.
        var outputDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "AppX"));
        outputDir.Create();
        var missingManifest = new FileInfo(Path.Combine(outputDir.FullName, "AppxManifest.xml"));
        var previousBytes = Encoding.UTF8.GetBytes("<Package />");

        var fake = new FakePackageRegistrationService();
        var svc = CreateMsixServiceForUnregister(fake, _tempDir.FullName);

        var result = svc.IsExistingRegistrationUpToDate(
            "MyApp", previousBytes, missingManifest, outputDir, CreateTestTaskContext());

        Assert.IsFalse(result);
        Assert.AreEqual(0, fake.FindDevPackagesCalls.Count, "Should short-circuit before querying packages when the new manifest is missing");
    }

    [TestMethod]
    public void IsExistingRegistrationUpToDate_ManifestSizeDiffers_ReturnsFalse()
    {
        // Different-length previous vs new manifest — the size shortcut returns false before
        // enumerating packages.
        var fixture = CreateSkipRegistrationFixture(
            manifestBody:         "<Package longer-than-before/>",
            previousManifestBody: "<Package/>");

        var result = fixture.Svc.IsExistingRegistrationUpToDate(
            "MyApp", fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir, CreateTestTaskContext());

        Assert.IsFalse(result, "A size mismatch must trigger re-registration");
        Assert.AreEqual(0, fixture.Fake.FindDevPackagesCalls.Count, "Size mismatch should short-circuit before querying packages");
    }

    [TestMethod]
    public void IsExistingRegistrationUpToDate_InstallLocationPathInvalid_ReturnsFalse()
    {
        // A dev package whose InstallLocation can't be normalized (embedded NUL) must be treated
        // as unknown via the path-normalization catch, not crash.
        var fixture = CreateSkipRegistrationFixture(
            manifestBody: "<Package />",
            installLocationOverride: "C:\\bad\0location");

        var result = fixture.Svc.IsExistingRegistrationUpToDate(
            "MyApp", fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir, CreateTestTaskContext());

        Assert.IsFalse(result, "An un-normalizable install location must fall back to re-registration");
    }

    [TestMethod]
    public void IsExistingRegistrationUpToDate_FindDevPackagesThrows_ReturnsFalse()
    {
        // L4 (PR #542 review): future code changes flipping the fallback to "true" or removing
        // the debug message shouldn't slip past the test suite.
        var outputDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "AppX"));
        outputDir.Create();
        var newManifestPath = new FileInfo(Path.Combine(outputDir.FullName, "AppxManifest.xml"));
        File.WriteAllText(newManifestPath.FullName, "<Package />");
        var previousBytes = Encoding.UTF8.GetBytes("<Package />");

        var fake = new FakePackageRegistrationService { FindDevPackagesThrows = new InvalidOperationException("WinRT exploded") };
        var svc = CreateMsixServiceForUnregister(fake, _tempDir.FullName);
        var taskContext = CreateTestTaskContext();

        var result = svc.IsExistingRegistrationUpToDate(
            "MyApp", previousBytes, newManifestPath, outputDir, taskContext);

        Assert.IsFalse(result, "Exceptions inspecting the existing registration must fall back to the unregister+register path");
    }

    [TestMethod]
    public void IsExistingRegistrationUpToDate_PropagatesOperationCanceled()
    {
        // M3 (PR #542 review): cancellation must NOT be swallowed by the generic catch.
        var outputDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "AppX"));
        outputDir.Create();
        var newManifestPath = new FileInfo(Path.Combine(outputDir.FullName, "AppxManifest.xml"));
        File.WriteAllText(newManifestPath.FullName, "<Package />");
        var previousBytes = Encoding.UTF8.GetBytes("<Package />");

        var fake = new FakePackageRegistrationService { FindDevPackagesThrows = new OperationCanceledException() };
        var svc = CreateMsixServiceForUnregister(fake, _tempDir.FullName);

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            svc.IsExistingRegistrationUpToDate("MyApp", previousBytes, newManifestPath, outputDir, CreateTestTaskContext()));
    }

    [TestMethod]
    public void TryReadExistingLayoutManifestBytes_NoManifest_ReturnsNull()
    {
        var dir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "EmptyAppX"));
        dir.Create();

        var bytes = MsixService.TryReadExistingLayoutManifestBytes(dir);

        Assert.IsNull(bytes);
    }

    [TestMethod]
    public void TryReadExistingLayoutManifestBytes_ReadFailure_ReturnsNull()
    {
        var dir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "LockedAppX"));
        dir.Create();
        var manifestPath = Path.Combine(dir.FullName, "AppxManifest.xml");
        File.WriteAllText(manifestPath, "<Package />");

        // Hold the manifest open with no sharing so the read throws — the helper must swallow it
        // and return null rather than propagating an I/O error.
        using var _ = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var bytes = MsixService.TryReadExistingLayoutManifestBytes(dir);

        Assert.IsNull(bytes);
    }

    [TestMethod]
    public void TryReadExistingLayoutManifestBytes_FindsAppxManifestXml()
    {
        var dir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "PascalAppX"));
        dir.Create();
        var content = "<Package id=\"pascal\" />";
        File.WriteAllText(Path.Combine(dir.FullName, "AppxManifest.xml"), content);

        var bytes = MsixService.TryReadExistingLayoutManifestBytes(dir);

        Assert.IsNotNull(bytes);
        Assert.AreEqual(content, Encoding.UTF8.GetString(bytes));
    }

    [TestMethod]
    public void TryReadExistingLayoutManifestBytes_IgnoresStrayPackageAppxmanifest()
    {
        // L1 (PR #542 review): a stray Package.appxmanifest in the output dir is NEVER the
        // file Windows actually registered — `winapp run` always normalizes to AppxManifest.xml
        // / appxmanifest.xml. Using it as a snapshot baseline would silently let the skip
        // path trigger off the wrong artifact.
        var dir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "StrayAppX"));
        dir.Create();
        File.WriteAllText(Path.Combine(dir.FullName, "Package.appxmanifest"), "<Package stray=\"true\" />");

        var bytes = MsixService.TryReadExistingLayoutManifestBytes(dir);

        Assert.IsNull(bytes, "Package.appxmanifest must not be used as a registration snapshot baseline");
    }

    [TestMethod]
    public void TryReadExistingLayoutManifestBytes_NonexistentDirectory_ReturnsNull()
    {
        var dir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "DoesNotExist"));

        var bytes = MsixService.TryReadExistingLayoutManifestBytes(dir);

        Assert.IsNull(bytes);
    }

    #endregion

    #region TrySkipRegistration Tests (PR #542 review M6 — issue #537)

    [TestMethod]
    public void TrySkipRegistration_WhenCleanIsTrue_ReturnsNullAndDoesNotQueryPackages()
    {
        var fixture = CreateSkipRegistrationFixture("<Package />");

        var result = fixture.Svc.TrySkipRegistration(
            "MyApp", "CN=Test", "App",
            fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir,
            clean: true, CreateTestTaskContext(), CancellationToken.None);

        Assert.IsNull(result, "--clean must always force the unregister+register path");
        Assert.AreEqual(0, fixture.Fake.FindDevPackagesCalls.Count,
            "Should short-circuit before any package enumeration when clean=true");
    }

    [TestMethod]
    public void TrySkipRegistration_WhenRegistrationUpToDate_ReturnsExistingIdentity()
    {
        var fixture = CreateSkipRegistrationFixture("<Package />");

        var result = fixture.Svc.TrySkipRegistration(
            "MyApp", "CN=TestPublisher", "AppId42",
            fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir,
            clean: false, CreateTestTaskContext(), CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual("MyApp", result.PackageName);
        Assert.AreEqual("CN=TestPublisher", result.Publisher);
        Assert.AreEqual("AppId42", result.ApplicationId);
    }

    [TestMethod]
    public void TrySkipRegistration_WhenManifestChanged_ReturnsNull()
    {
        // Drives the most important integration assertion: when the helper says
        // "not up to date", the caller will fall through and call Unregister/Register.
        var fixture = CreateSkipRegistrationFixture(
            manifestBody:         "<Package new=\"true\"/>",
            previousManifestBody: "<Package old=\"true\"/>");

        var result = fixture.Svc.TrySkipRegistration(
            "MyApp", "CN=Test", "App",
            fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir,
            clean: false, CreateTestTaskContext(), CancellationToken.None);

        Assert.IsNull(result, "Changed manifest must drop through to unregister+register");
    }

    [TestMethod]
    public void TrySkipRegistration_WhenFirstRun_ReturnsNull()
    {
        // No previous snapshot (e.g. first run on this machine) — must register normally.
        var fixture = CreateSkipRegistrationFixture("<Package />", capturePreviousManifest: false);

        var result = fixture.Svc.TrySkipRegistration(
            "MyApp", "CN=Test", "App",
            fixture.PreviousBytes, fixture.NewManifest, fixture.OutputDir,
            clean: false, CreateTestTaskContext(), CancellationToken.None);

        Assert.IsNull(result);
    }

    #endregion

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    #region MergeRuntimeFilesIntoStaging tests

    [TestMethod]
    public void MergeRuntimeFilesIntoStaging_DoesNotOverwriteExistingResourcesPri()
    {
        // Arrange — staging has the app's resources.pri
        var stagingDir = _tempDir.CreateSubdirectory("staging");
        var appPriContent = "APP_PRI_CONTENT"u8.ToArray();
        File.WriteAllBytes(Path.Combine(stagingDir.FullName, "resources.pri"), appPriContent);

        // Runtime source has its own resources.pri
        var runtimeDir = _tempDir.CreateSubdirectory("runtime");
        File.WriteAllBytes(Path.Combine(runtimeDir.FullName, "resources.pri"), "RUNTIME_PRI"u8.ToArray());

        // Act
        MsixService.MergeRuntimeFilesIntoStaging(runtimeDir, stagingDir);

        // Assert — app's PRI is preserved
        var resultPri = File.ReadAllBytes(Path.Combine(stagingDir.FullName, "resources.pri"));
        CollectionAssert.AreEqual(appPriContent, resultPri,
            "App's resources.pri must not be overwritten by the runtime's");
    }

    [TestMethod]
    public void MergeRuntimeFilesIntoStaging_DoesNotOverwriteExistingAssets()
    {
        // Arrange — staging has the app's StoreLogo
        var stagingDir = _tempDir.CreateSubdirectory("staging");
        var assetsDir = stagingDir.CreateSubdirectory("Assets");
        var appLogoContent = "APP_STORE_LOGO"u8.ToArray();
        File.WriteAllBytes(Path.Combine(assetsDir.FullName, "StoreLogo.png"), appLogoContent);

        // Runtime source has its own Assets\StoreLogo.png
        var runtimeDir = _tempDir.CreateSubdirectory("runtime");
        var runtimeAssetsDir = runtimeDir.CreateSubdirectory("Assets");
        File.WriteAllBytes(Path.Combine(runtimeAssetsDir.FullName, "StoreLogo.png"), "RUNTIME_LOGO"u8.ToArray());

        // Act
        MsixService.MergeRuntimeFilesIntoStaging(runtimeDir, stagingDir);

        // Assert — app's asset is preserved
        var resultLogo = File.ReadAllBytes(Path.Combine(assetsDir.FullName, "StoreLogo.png"));
        CollectionAssert.AreEqual(appLogoContent, resultLogo,
            "App's Assets\\StoreLogo.png must not be overwritten by the runtime's");
    }

    [TestMethod]
    public void MergeRuntimeFilesIntoStaging_CopiesNewRuntimeFiles()
    {
        // Arrange — staging is empty
        var stagingDir = _tempDir.CreateSubdirectory("staging");

        // Runtime source has DLLs and PRI
        var runtimeDir = _tempDir.CreateSubdirectory("runtime");
        File.WriteAllBytes(Path.Combine(runtimeDir.FullName, "Microsoft.WindowsAppRuntime.dll"), [0x01]);
        File.WriteAllBytes(Path.Combine(runtimeDir.FullName, "Microsoft.UI.pri"), [0x02]);

        // Act
        MsixService.MergeRuntimeFilesIntoStaging(runtimeDir, stagingDir);

        // Assert — runtime files are copied
        Assert.IsTrue(File.Exists(Path.Combine(stagingDir.FullName, "Microsoft.WindowsAppRuntime.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(stagingDir.FullName, "Microsoft.UI.pri")));
    }

    [TestMethod]
    public void MergeRuntimeFilesIntoStaging_ThrowsWhenSourceDirMissing()
    {
        var stagingDir = _tempDir.CreateSubdirectory("staging");
        var missingDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "does-not-exist"));

        Assert.ThrowsExactly<DirectoryNotFoundException>(
            () => MsixService.MergeRuntimeFilesIntoStaging(missingDir, stagingDir));
    }

    #endregion
}
