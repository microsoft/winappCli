// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class WinmdServiceTests : BaseCommandTests
{
    private IWinmdService _winmdService = null!;
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _winmdService = GetRequiredService<IWinmdService>();
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"WinmdTest_{Guid.NewGuid():N}"));
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
    /// Creates a minimal valid .winmd (PE with ECMA-335 metadata) containing
    /// the specified activatable class names. Each class is marked with
    /// [Windows.Foundation.Metadata.ActivatableAttribute] so that
    /// GetActivatableClasses correctly discovers them.
    /// </summary>
    private static void CreateTestWinmd(string path, params string[] activatableClassNames)
    {
        CreateTestWinmd(path, activatableClassNames, nonActivatableClassNames: []);
    }

    /// <summary>
    /// Creates a minimal valid .winmd with both activatable and non-activatable classes.
    /// Activatable classes get the [ActivatableAttribute] custom attribute;
    /// non-activatable classes are plain public types (e.g., event args).
    /// </summary>
    private static void CreateTestWinmd(string path, string[] activatableClassNames, string[] nonActivatableClassNames)
    {
        CreateTestWinmd(path, activatableClassNames, nonActivatableClassNames, nonPublicActivatableClassNames: []);
    }

    /// <summary>
    /// Creates a minimal valid .winmd. In addition to public activatable and plain
    /// non-activatable classes, <paramref name="nonPublicActivatableClassNames"/> get the
    /// [ActivatableAttribute] but are emitted with non-public visibility, so
    /// GetActivatableClasses should filter them out.
    /// </summary>
    private static void CreateTestWinmd(
        string path,
        string[] activatableClassNames,
        string[] nonActivatableClassNames,
        string[] nonPublicActivatableClassNames)
    {
        var metadata = new MetadataBuilder();

        // Module and assembly boilerplate
        metadata.AddModule(0, metadata.GetOrAddString("<Module>"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(
            metadata.GetOrAddString("TestWinmd"),
            new Version(1, 0, 0, 0),
            default, default,
            default, AssemblyHashAlgorithm.None);

        // Reference mscorlib/System.Runtime for base type System.Object
        var systemRuntimeRef = metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Runtime"),
            new Version(4, 0, 0, 0),
            default, default, default, default);

        var objectTypeRef = metadata.AddTypeReference(
            systemRuntimeRef,
            metadata.GetOrAddString("System"),
            metadata.GetOrAddString("Object"));

        // Reference Windows.Foundation.Metadata.ActivatableAttribute for marking activatable classes
        var wfmAssemblyRef = metadata.AddAssemblyReference(
            metadata.GetOrAddString("Windows.Foundation.FoundationContract"),
            new Version(4, 0, 0, 0),
            default, default, default, default);

        var activatableAttrTypeRef = metadata.AddTypeReference(
            wfmAssemblyRef,
            metadata.GetOrAddString("Windows.Foundation.Metadata"),
            metadata.GetOrAddString("ActivatableAttribute"));

        // Build a minimal signature for the ActivatableAttribute .ctor (parameterless)
        // Blob: prolog (0x0001) + no params
        var ctorSigBlob = new BlobBuilder();
        ctorSigBlob.WriteByte(0x20); // HASTHIS
        ctorSigBlob.WriteByte(0x00); // param count = 0
        ctorSigBlob.WriteByte(0x01); // return type = void

        var activatableAttrCtor = metadata.AddMemberReference(
            activatableAttrTypeRef,
            metadata.GetOrAddString(".ctor"),
            metadata.GetOrAddBlob(ctorSigBlob));

        // Minimal custom attribute value: just prolog (0x0001) + NumNamed (0x0000)
        var attrValueBlob = new BlobBuilder();
        attrValueBlob.WriteUInt16(0x0001); // prolog
        attrValueBlob.WriteUInt16(0x0000); // NumNamed = 0

        // <Module> type
        metadata.AddTypeDefinition(
            default,
            default,
            metadata.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        // Add each activatable class with [ActivatableAttribute]
        foreach (var fullName in activatableClassNames)
        {
            var lastDot = fullName.LastIndexOf('.');
            var ns = lastDot > 0 ? fullName[..lastDot] : "";
            var name = lastDot > 0 ? fullName[(lastDot + 1)..] : fullName;

            var typeDefHandle = metadata.AddTypeDefinition(
                TypeAttributes.Public | TypeAttributes.Class,
                metadata.GetOrAddString(ns),
                metadata.GetOrAddString(name),
                objectTypeRef,
                MetadataTokens.FieldDefinitionHandle(1),
                MetadataTokens.MethodDefinitionHandle(1));

            // Add [ActivatableAttribute] custom attribute
            metadata.AddCustomAttribute(typeDefHandle, activatableAttrCtor, metadata.GetOrAddBlob(attrValueBlob));
        }

        // Add non-activatable classes (no custom attribute — e.g., event args, settings types)
        foreach (var fullName in nonActivatableClassNames)
        {
            var lastDot = fullName.LastIndexOf('.');
            var ns = lastDot > 0 ? fullName[..lastDot] : "";
            var name = lastDot > 0 ? fullName[(lastDot + 1)..] : fullName;

            metadata.AddTypeDefinition(
                TypeAttributes.Public | TypeAttributes.Class,
                metadata.GetOrAddString(ns),
                metadata.GetOrAddString(name),
                objectTypeRef,
                MetadataTokens.FieldDefinitionHandle(1),
                MetadataTokens.MethodDefinitionHandle(1));
        }

        // Add non-public activatable classes: they carry [ActivatableAttribute] but are
        // NOT public, so GetActivatableClasses must skip them (visibility filter).
        foreach (var fullName in nonPublicActivatableClassNames)
        {
            var lastDot = fullName.LastIndexOf('.');
            var ns = lastDot > 0 ? fullName[..lastDot] : "";
            var name = lastDot > 0 ? fullName[(lastDot + 1)..] : fullName;

            var typeDefHandle = metadata.AddTypeDefinition(
                TypeAttributes.Class, // no Public bit => NotPublic
                metadata.GetOrAddString(ns),
                metadata.GetOrAddString(name),
                objectTypeRef,
                MetadataTokens.FieldDefinitionHandle(1),
                MetadataTokens.MethodDefinitionHandle(1));

            metadata.AddCustomAttribute(typeDefHandle, activatableAttrCtor, metadata.GetOrAddBlob(attrValueBlob));
        }
        var peHeaderBuilder = new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll);
        var peBuilder = new ManagedPEBuilder(peHeaderBuilder, new MetadataRootBuilder(metadata), ilStream: new BlobBuilder());
        var blobBuilder = new BlobBuilder();
        peBuilder.Serialize(blobBuilder);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        blobBuilder.WriteContentTo(fs);
    }

    /// <summary>
    /// Creates a fake NuGet package directory with a .winmd in lib/ and an optional
    /// matching implementation DLL.
    /// </summary>
    private string CreateFakePackage(
        string packageName,
        string version,
        string[] activatableClasses,
        string? dllLocation = null)
    {
        var pkgRoot = Path.Combine(_tempDir.FullName, packageName.ToLowerInvariant(), version);
        var winmdName = $"{packageName}.winmd";

        // Place .winmd in lib/
        CreateTestWinmd(Path.Combine(pkgRoot, "lib", winmdName), activatableClasses);

        // Place implementation DLL if requested
        if (dllLocation != null)
        {
            var dllPath = Path.Combine(pkgRoot, dllLocation, $"{packageName}.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);
            File.WriteAllBytes(dllPath, [0]);
        }

        return pkgRoot;
    }

    #region GetActivatableClasses Tests

    [TestMethod]
    public void GetActivatableClasses_ValidWinmd_ReturnsExpectedClasses()
    {
        // Arrange: create a .winmd with known activatable classes
        var winmdPath = Path.Combine(_tempDir.FullName, "Test.winmd");
        var expectedClasses = new[]
        {
            "MyNamespace.CanvasDevice",
            "MyNamespace.CanvasBitmap",
            "MyNamespace.UI.Xaml.CanvasControl"
        };
        CreateTestWinmd(winmdPath, expectedClasses);

        // Act
        var classes = _winmdService.GetActivatableClasses(new FileInfo(winmdPath));

        // Assert
        Assert.IsNotNull(classes);
        Assert.IsNotEmpty(classes);
        foreach (var expected in expectedClasses)
        {
            Assert.IsTrue(classes.Contains(expected), $"Should contain {expected}");
        }
    }

    [TestMethod]
    public void GetActivatableClasses_NonExistentFile_ReturnsEmpty()
    {
        var result = _winmdService.GetActivatableClasses(new FileInfo(@"C:\nonexistent\fake.winmd"));
        Assert.IsNotNull(result);
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void GetActivatableClasses_CorruptWinmd_ReturnsEmpty()
    {
        // Arrange: write garbage bytes that are not a valid PE/winmd
        var corruptPath = Path.Combine(_tempDir.FullName, "Corrupt.winmd");
        File.WriteAllBytes(corruptPath, [0x00, 0xFF, 0xFE, 0xAB, 0xCD, 0x12, 0x34]);

        // Act: should not throw — returns empty list
        var result = _winmdService.GetActivatableClasses(new FileInfo(corruptPath));

        Assert.IsNotNull(result);
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void GetActivatableClasses_FiltersOutNonActivatableClasses()
    {
        // Arrange: create a .winmd with both activatable and non-activatable classes
        // (simulates WebView2 which has ~6 activatable classes but ~70 event args / settings types)
        var winmdPath = Path.Combine(_tempDir.FullName, "Mixed.winmd");
        var activatable = new[]
        {
            "MyNamespace.CoreWebView2Environment",
            "MyNamespace.CoreWebView2Controller",
            "MyNamespace.CoreWebView2EnvironmentOptions"
        };
        var nonActivatable = new[]
        {
            "MyNamespace.CoreWebView2NavigationStartingEventArgs",
            "MyNamespace.CoreWebView2Settings",
            "MyNamespace.CoreWebView2HttpResponseHeaders",
            "MyNamespace.CoreWebView2WebResourceRequest",
            "MyNamespace.CoreWebView2ProcessFailedEventArgs"
        };

        CreateTestWinmd(winmdPath, activatable, nonActivatable);

        // Act
        var classes = _winmdService.GetActivatableClasses(new FileInfo(winmdPath));

        // Assert: only activatable classes should be returned
        Assert.HasCount(activatable.Length, classes,
            $"Should return exactly {activatable.Length} activatable classes, got {classes.Count}: [{string.Join(", ", classes)}]");

        foreach (var expected in activatable)
        {
            Assert.IsTrue(classes.Contains(expected), $"Should contain activatable class {expected}");
        }

        foreach (var excluded in nonActivatable)
        {
            Assert.IsFalse(classes.Contains(excluded), $"Should NOT contain non-activatable class {excluded}");
        }
    }

    [TestMethod]
    public void GetActivatableClasses_ActivatableClassWithEmptyNamespace_IsSkipped()
    {
        // Arrange: an activatable class with no namespace (name has no dot => empty namespace)
        var winmdPath = Path.Combine(_tempDir.FullName, "EmptyNs.winmd");
        CreateTestWinmd(winmdPath, "NoNamespaceClass");

        // Act
        var classes = _winmdService.GetActivatableClasses(new FileInfo(winmdPath));

        // Assert: classes with an empty namespace are filtered out
        Assert.IsNotNull(classes);
        Assert.IsEmpty(classes);
    }

    [TestMethod]
    public void GetActivatableClasses_NonPublicActivatableClass_IsSkipped()
    {
        // Arrange: an activatable class that is not public should be excluded
        var winmdPath = Path.Combine(_tempDir.FullName, "NonPublic.winmd");
        CreateTestWinmd(
            winmdPath,
            activatableClassNames: [],
            nonActivatableClassNames: [],
            nonPublicActivatableClassNames: ["MyNamespace.InternalOnly"]);

        // Act
        var classes = _winmdService.GetActivatableClasses(new FileInfo(winmdPath));

        // Assert: the non-public activatable class must not be reported
        Assert.IsNotNull(classes);
        Assert.IsEmpty(classes);
    }

    #endregion

    #region DiscoverWinRTComponents Tests

    [TestMethod]
    public void DiscoverWinRTComponents_NativeDirLayout_FindsComponent()
    {
        // Arrange: simulate Win2D-style layout (DLL in runtimes/win-x64/native/)
        var packageName = "Fake.Graphics.Win2D";
        var version = "1.0.0";
        var pkgRoot = Path.Combine(_tempDir.FullName, packageName.ToLowerInvariant(), version);

        CreateTestWinmd(
            Path.Combine(pkgRoot, "lib", "net8.0-windows10.0.19041.0", $"{packageName}.winmd"),
            "Fake.Graphics.Win2D.CanvasDevice");

        // DLL in native dir
        var nativeDir = Path.Combine(pkgRoot, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(nativeDir);
        File.WriteAllBytes(Path.Combine(nativeDir, $"{packageName}.dll"), [0]);

        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { packageName, version }
        };

        // Act
        var components = _winmdService.DiscoverWinRTComponents(_tempDir, packages, "x64");

        // Assert
        Assert.IsNotNull(components);
        Assert.IsNotEmpty(components, "Should discover component with DLL in runtimes/win-x64/native/");

        var component = components.FirstOrDefault(c => c.ImplementationDll == $"{packageName}.dll");
        Assert.IsNotNull(component, $"Should find {packageName}.dll as implementation DLL");
    }

    [TestMethod]
    public void DiscoverWinRTComponents_LibDirFallback_FindsComponentWithDllInLib()
    {
        // Arrange: simulate WebView2-style layout (DLL in lib/net462/, no runtimes dir)
        var packageName = "Fake.Web.WebView2.Core";
        var version = "1.0.0";
        var pkgRoot = Path.Combine(_tempDir.FullName, packageName.ToLowerInvariant(), version);

        // .winmd at lib root (like WebView2)
        CreateTestWinmd(
            Path.Combine(pkgRoot, "lib", $"{packageName}.winmd"),
            "Fake.Web.WebView2.Core.CoreWebView2");

        // DLL in lib/net462/ (like WebView2)
        var libTfmDir = Path.Combine(pkgRoot, "lib", "net462");
        Directory.CreateDirectory(libTfmDir);
        File.WriteAllBytes(Path.Combine(libTfmDir, $"{packageName}.dll"), [0]);

        // Deliberately NO runtimes/ directory

        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { packageName, version }
        };

        // Act
        var components = _winmdService.DiscoverWinRTComponents(_tempDir, packages, "x64");

        // Assert — should find the component via lib/ fallback
        Assert.IsNotNull(components);
        Assert.IsNotEmpty(components, "Should discover component with DLL in lib/ directory");

        var component = components.FirstOrDefault(c => c.ImplementationDll == $"{packageName}.dll");
        Assert.IsNotNull(component, $"Should find {packageName}.dll as implementation DLL");
    }

    [TestMethod]
    public void DiscoverWinRTComponents_SDKPackages_SkipsExcluded()
    {
        // Arrange: fake NuGet cache with SDK package directory structures
        var excludedPackages = new (string Name, string Version)[]
        {
            ("Microsoft.Windows.SDK.CPP", "10.0.26100.1742"),
            ("Microsoft.Windows.SDK.Contracts", "10.0.22621.756"),
            ("Microsoft.Windows.SDK.BuildTools", "10.0.26100.1"),
            ("Microsoft.Windows.CppWinRT", "2.0.240405.15"),
            ("Microsoft.Windows.ImplementationLibrary", "1.0.240803.1"),
        };

        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, version) in excludedPackages)
        {
            var pkgDir = Directory.CreateDirectory(
                Path.Combine(_tempDir.FullName, name.ToLowerInvariant(), version, "lib"));
            File.WriteAllBytes(Path.Combine(pkgDir.FullName, "Fake.winmd"), [0]);
            packages.Add(name, version);
        }

        // Act
        var components = _winmdService.DiscoverWinRTComponents(_tempDir, packages, "x64");

        // Assert - all SDK packages should be excluded by IsExcludedPackage
        Assert.IsNotNull(components);
        Assert.IsEmpty(components, "SDK packages should be excluded from WinRT component discovery");
    }

    [TestMethod]
    public void DiscoverWinRTComponents_WithExcludeSet_SkipsExcludedPackages()
    {
        // Arrange
        var packageName = "Fake.WinRT.Lib";
        var version = "1.0.0";
        CreateFakePackage(packageName, version,
            ["Fake.WinRT.Lib.SomeClass"],
            dllLocation: Path.Combine("runtimes", "win-x64", "native"));

        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { packageName, version }
        };

        var excludeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { packageName };

        // Act
        var components = _winmdService.DiscoverWinRTComponents(_tempDir, packages, "x64", excludeSet);

        // Assert
        Assert.IsNotNull(components);
        Assert.IsEmpty(components, "Excluded packages should not be discovered");
    }

    [TestMethod]
    public void DiscoverWinRTComponents_NoMatchingDll_ReturnsEmpty()
    {
        // Arrange: .winmd exists but no matching DLL anywhere
        var packageName = "Fake.NoDll.Component";
        var version = "1.0.0";
        var pkgRoot = Path.Combine(_tempDir.FullName, packageName.ToLowerInvariant(), version);

        CreateTestWinmd(
            Path.Combine(pkgRoot, "lib", $"{packageName}.winmd"),
            "Fake.NoDll.Component.SomeClass");
        // No DLL placed anywhere

        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { packageName, version }
        };

        // Act
        var components = _winmdService.DiscoverWinRTComponents(_tempDir, packages, "x64");

        // Assert
        Assert.IsNotNull(components);
        Assert.IsEmpty(components, "Should not discover component when no matching DLL exists");
    }

    [TestMethod]
    public void DiscoverWinRTComponents_MetadataAndReferencesLayouts_FindWinmds()
    {
        // Beyond lib/, FindWinmdsInPackage also probes metadata/, metadata/10.0.18362.0/,
        // and References/. Place a winmd in each with a matching native DLL so every
        // discovery branch is exercised and each winmd surfaces as a component.
        var packageName = "Fake.Layout.Component";
        var version = "1.0.0";
        var pkgRoot = Path.Combine(_tempDir.FullName, packageName.ToLowerInvariant(), version);

        CreateTestWinmd(Path.Combine(pkgRoot, "metadata", "Fake.Meta.winmd"), "Fake.Meta.Thing");
        CreateTestWinmd(Path.Combine(pkgRoot, "metadata", "10.0.18362.0", "Fake.Versioned.winmd"), "Fake.Versioned.Thing");
        CreateTestWinmd(Path.Combine(pkgRoot, "References", "Fake.Ref.winmd"), "Fake.Ref.Thing");

        // A native DLL matching each winmd stem so they are all treated as components.
        var nativeDir = Path.Combine(pkgRoot, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(nativeDir);
        foreach (var stem in new[] { "Fake.Meta", "Fake.Versioned", "Fake.Ref" })
        {
            File.WriteAllBytes(Path.Combine(nativeDir, $"{stem}.dll"), [0]);
        }

        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { packageName, version }
        };

        var components = _winmdService.DiscoverWinRTComponents(_tempDir, packages, "x64");

        var dlls = components.Select(c => c.ImplementationDll).ToList();
        Assert.Contains("Fake.Meta.dll", dlls, "Winmd under metadata/ should be discovered.");
        Assert.Contains("Fake.Versioned.dll", dlls, "Winmd under metadata/10.0.18362.0/ should be discovered.");
        Assert.Contains("Fake.Ref.dll", dlls, "Winmd under References/ should be discovered.");
    }

    [TestMethod]
    public void DiscoverWinRTComponents_PackageDirMissing_SkipsPackage()
    {
        // A package listed in used-versions but absent from the cache is silently skipped.
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Not.Restored.Package", "9.9.9" }
        };

        var components = _winmdService.DiscoverWinRTComponents(_tempDir, packages, "x64");

        Assert.IsEmpty(components, "A package with no directory on disk must be skipped.");
    }

    [TestMethod]
    public void DiscoverWinRTComponents_AppxFragmentPackage_IsSkipped()
    {
        // Packages carrying runtimes-framework/package.appxfragment are handled by the
        // existing WinApp SDK fragment path and must be skipped here.
        var packageName = "Fake.Framework.Package";
        var version = "1.0.0";
        var pkgRoot = Path.Combine(_tempDir.FullName, packageName.ToLowerInvariant(), version);

        CreateTestWinmd(Path.Combine(pkgRoot, "lib", $"{packageName}.winmd"), "Fake.Framework.Thing");
        var fragmentDir = Path.Combine(pkgRoot, "runtimes-framework");
        Directory.CreateDirectory(fragmentDir);
        File.WriteAllText(Path.Combine(fragmentDir, "package.appxfragment"), "<fragment/>");

        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { packageName, version }
        };

        var components = _winmdService.DiscoverWinRTComponents(_tempDir, packages, "x64");

        Assert.IsEmpty(components, "Packages with an appxfragment must be skipped.");
    }

    #endregion

    #region End-to-End: activatable classes to SxS manifest format

    [TestMethod]
    public void EndToEnd_GeneratesCorrectManifestEntries()
    {
        // Arrange: set up a fake package with known activatable classes
        var packageName = "Fake.Graphics.Canvas";
        var version = "1.0.0";
        var expectedClasses = new[]
        {
            "Fake.Graphics.Canvas.CanvasDevice",
            "Fake.Graphics.Canvas.CanvasBitmap",
            "Fake.Graphics.Canvas.UI.Xaml.CanvasControl"
        };

        var pkgRoot = Path.Combine(_tempDir.FullName, packageName.ToLowerInvariant(), version);
        CreateTestWinmd(
            Path.Combine(pkgRoot, "lib", "net8.0-windows10.0.19041.0", $"{packageName}.winmd"),
            expectedClasses);
        var nativeDir = Path.Combine(pkgRoot, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(nativeDir);
        File.WriteAllBytes(Path.Combine(nativeDir, $"{packageName}.dll"), [0]);

        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { packageName, version }
        };

        // Act: Discover components
        var components = _winmdService.DiscoverWinRTComponents(_tempDir, packages, "x64");
        Assert.IsNotEmpty(components, "Should discover component");

        // Generate SxS manifest entries
        var sb = new System.Text.StringBuilder();
        foreach (var component in components)
        {
            var classes = _winmdService.GetActivatableClasses(component.WinmdPath);
            Assert.IsNotEmpty(classes, $"Should have classes for {component.ImplementationDll}");

            sb.AppendLine($"    <asmv3:file name='{component.ImplementationDll}'>");
            foreach (var className in classes)
            {
                sb.AppendLine($"        <winrtv1:activatableClass name='{className}' threadingModel='both'/>");
            }
            sb.AppendLine("    </asmv3:file>");
        }

        var manifestEntries = sb.ToString();

        // Assert
        foreach (var cls in expectedClasses)
        {
            Assert.Contains(cls, manifestEntries, $"Manifest should contain {cls}");
        }

        Assert.Contains($"{packageName}.dll", manifestEntries,
            $"Manifest should reference {packageName}.dll");

        // Generate AppxManifest.xml format
        var appxSb = new System.Text.StringBuilder();
        foreach (var component in components)
        {
            var classes = _winmdService.GetActivatableClasses(component.WinmdPath);
            appxSb.AppendLine(@"    <Extension Category=""windows.activatableClass.inProcessServer"">");
            appxSb.AppendLine(@"      <InProcessServer>");
            appxSb.AppendLine($@"        <Path>{component.ImplementationDll}</Path>");
            foreach (var className in classes)
            {
                appxSb.AppendLine($@"        <ActivatableClass ActivatableClassId=""{className}"" ThreadingModel=""both""/>");
            }
            appxSb.AppendLine(@"      </InProcessServer>");
            appxSb.AppendLine(@"    </Extension>");
        }

        var appxEntries = appxSb.ToString();
        Assert.Contains("<InProcessServer>", appxEntries);
        Assert.Contains(@"ThreadingModel=""both""", appxEntries);
    }

    #endregion
}
