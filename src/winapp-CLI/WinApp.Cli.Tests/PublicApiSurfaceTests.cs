// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

namespace WinApp.Cli.Tests;

/// <summary>
/// Pins the public surface of the two shipped packages.
/// </summary>
/// <remarks>
/// Package ids cannot be renamed and public API cannot be withdrawn after publish, so a type that
/// leaks out of the implementation is permanent. These assemblies are also unusual: the CLI consumes
/// them across an assembly boundary and cannot use <c>InternalsVisibleTo</c> (both run the CsWin32
/// generator, and sharing internals makes <c>Windows.Win32.PInvoke</c> ambiguous), so the pressure is
/// always toward making one more thing public. Adding a type here should be a deliberate edit, not a
/// side effect.
/// </remarks>
[TestClass]
public class PublicApiSurfaceTests
{
    private static readonly string[] ExpectedUiAutomationTypes =
    [
        "AppNotFoundException",
        "CaptureGeometry",
        "CapturedFrame",
        "CoordinateParser",
        "ForegroundCheck",
        "ForegroundGuard",
        "ForegroundLostException",
        "GestureTargeting",
        "HardBlockedCombo",
        "IForegroundGuard",
        "IFrameGrabber",
        "IKeyboardInput",
        "IMouseInput",
        "IOwnedWindowFinder",
        "IPointerInput",
        "IPollDelay",
        "ISystemUiQuery",
        "IUiAutomation",
        "IUiSelectorParser",
        "IUiTargetResolver",
        "IWindowCapture",
        "KeyAction",
        "KeyboardInput",
        "KeyChord",
        "KeyStringParser",
        "KeyTransport",
        "PointerGesturePlanner",
        "PointerPoint",
        "PointerRect",
        "StableTarget",
        "SystemKeyGuard",
        "TargetStatus",
        "TextInput",
        "TouchGesture",
        "UiAmbiguousSelectorException",
        "UiAutomationServiceCollectionExtensions",
        "UiElement",
        "UiElementNotFoundException",
        "UiElementOffscreenException",
        "UiProcessInfo",
        "UiSelector",
        "UiTarget",
        "UiTargetResolver",
        "WindowMetadata",
    ];

    private static readonly string[] ExpectedRecordingTypes =
    [
        "IUiRecordingService",
        "Mp4EncoderInitializationException",
        "RecordCaptureResult",
        "RecordFrameArtifactResult",
        "RecordFrameBundleManifest",
        "RecordFrameImagesManifest",
        "RecordFrameIndexEntry",
        "RecordFrameOutputException",
        "RecordFrameRequestManifest",
        "RecordFrameTimingManifest",
        "RecordFrameVideoManifest",
        "RecordOptions",
        "RecordPartialOutputException",
        "UiRecordingServiceCollectionExtensions",
    ];

    [TestMethod]
    public void UiAutomationPackage_ExportsOnlyTheSupportedTypes()
        => AssertExportedTypes(typeof(IUiAutomation), ExpectedUiAutomationTypes);

    [TestMethod]
    public void RecordingPackage_ExportsOnlyTheSupportedTypes()
        => AssertExportedTypes(typeof(IUiRecordingService), ExpectedRecordingTypes);

    private static void AssertExportedTypes(Type anchor, string[] expected)
    {
        var actual = anchor.Assembly.GetExportedTypes()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var added = actual.Except(expected, StringComparer.Ordinal).ToArray();
        var removed = expected.Except(actual, StringComparer.Ordinal).ToArray();

        Assert.IsTrue(
            added.Length == 0,
            $"{anchor.Assembly.GetName().Name} newly exports {string.Join(", ", added)}. Anything public here " +
            "ships forever, so make it internal, or add it to the expected list once it is documented and intended.");

        Assert.IsTrue(
            removed.Length == 0,
            $"{anchor.Assembly.GetName().Name} no longer exports {string.Join(", ", removed)}. Removing public API " +
            "breaks consumers; if that is intended, update the expected list in the same change.");
    }

    [TestMethod]
    public void ShippedPackages_DocumentEveryPublicType()
    {
        // GenerateDocumentationFile is on for both packages and the build treats warnings as errors in
        // Release, so an undocumented public type cannot reach a package. This checks the file that
        // actually ships, after the interop entries are trimmed out of it.
        foreach (var anchor in new[] { typeof(IUiAutomation), typeof(IUiRecordingService) })
        {
            var xmlPath = Path.ChangeExtension(anchor.Assembly.Location, ".xml");
            Assert.IsTrue(File.Exists(xmlPath), $"No XML documentation shipped next to {anchor.Assembly.GetName().Name}.");

            var documented = File.ReadAllText(xmlPath);

            // Documentation ids separate a nested type with '.', where reflection uses '+'.
            var docIds = anchor.Assembly.GetExportedTypes().Select(t => t.FullName!.Replace('+', '.'));
            foreach (var docId in docIds)
            {
                StringAssert.Contains(
                    documented,
                    $"\"T:{docId}\"",
                    $"{docId} is public but has no entry in the shipped documentation file.");
            }
        }
    }
}
