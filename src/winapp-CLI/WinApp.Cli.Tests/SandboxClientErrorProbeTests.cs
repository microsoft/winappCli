// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
public class SandboxClientErrorProbeTests
{
    [TestMethod]
    public void TerminalError_UsesNonlocalizedIconAndDialogStructure()
    {
        Assert.IsTrue(SandboxClientErrorProbe.IsTerminalError(ErrorPage(), complete: true));
    }

    [TestMethod]
    public void IncompleteRead_DoesNotExcludeClient()
    {
        Assert.IsFalse(SandboxClientErrorProbe.IsTerminalError(ErrorPage(), complete: false));
    }

    [TestMethod]
    public void EmptyStartingOrInaccessibleClient_IsNotATerminalError()
    {
        Assert.IsFalse(SandboxClientErrorProbe.IsTerminalError([], complete: true));
        Assert.IsFalse(SandboxClientErrorProbe.IsTerminalError([], complete: false));
    }

    [TestMethod]
    public void WarningDialog_IsNotATerminalError()
    {
        Assert.IsFalse(SandboxClientErrorProbe.IsTerminalError(ErrorPage("\uE7BA"), complete: true));
    }

    [TestMethod]
    public void ErrorMessageTextWithoutErrorIcon_IsNotEnough()
    {
        Assert.IsFalse(SandboxClientErrorProbe.IsTerminalError(ErrorPage("Error"), complete: true));
    }

    [TestMethod]
    public void GlyphAloneOrPartialDialog_IsNotEnough()
    {
        var page = ErrorPage();
        foreach (var omitted in Enumerable.Range(0, page.Length))
        {
            Assert.IsFalse(SandboxClientErrorProbe.IsTerminalError(
                page.Where((_, index) => index != omitted).ToArray(), complete: true));
        }
    }

    [TestMethod]
    public void UnknownLayoutOrAdditionalAction_IsNotExcluded()
    {
        Assert.IsFalse(SandboxClientErrorProbe.IsTerminalError(
            [.. ErrorPage(), new("Button", "retry", 50000, "")], complete: true));
        var page = ErrorPage();
        page[2] = page[2] with { AutomationId = "NewDialogAction" };
        Assert.IsFalse(SandboxClientErrorProbe.IsTerminalError(page, complete: true));
    }

    [TestMethod]
    public void Classify_RequiresKnownShellAsWellAsCompleteErrorPage()
    {
        Assert.AreEqual(SandboxClientSurface.TerminalError,
            SandboxClientErrorProbe.Classify(Shell(), ErrorPage(), renderer: false));
        Assert.AreEqual(SandboxClientSurface.Unknown,
            SandboxClientErrorProbe.Classify([], ErrorPage(), renderer: false));
        Assert.AreEqual(SandboxClientSurface.Unknown,
            SandboxClientErrorProbe.Classify(
                [.. Shell(), new("NewShellElement", "", 50033, "")], ErrorPage(), renderer: false));
    }

    [TestMethod]
    public void Classify_MixedErrorAndRenderer_RemainsUnknown()
    {
        Assert.AreEqual(SandboxClientSurface.Unknown,
            SandboxClientErrorProbe.Classify(Shell(renderer: true), ErrorPage(), renderer: true));
    }

    [TestMethod]
    public void Classify_StartingInaccessibleAndNewLayouts_RemainUnknown()
    {
        Assert.AreEqual(SandboxClientSurface.Unknown,
            SandboxClientErrorProbe.Classify(Shell(), [], renderer: false));
        Assert.AreEqual(SandboxClientSurface.Unknown,
            SandboxClientErrorProbe.Classify(Shell(renderer: true), [], renderer: false));
        Assert.AreEqual(SandboxClientSurface.Unknown,
            SandboxClientErrorProbe.Classify(Shell(renderer: true), [], renderer: true));
    }

    [TestMethod]
    public void Classify_RecognizesRendererOnlyWithKnownSessionPage()
    {
        SandboxClientErrorProbe.Control[] page =
        [
            new("Image", "", 50006, "TitleBarIcon"),
            new("TextBlock", "", 50020, "TitleTextBlock"),
            new("Button", "", 50000, "SandboxOptions"),
        ];
        Assert.AreEqual(SandboxClientSurface.Session,
            SandboxClientErrorProbe.Classify(Shell(renderer: true), page, renderer: true));
        Assert.AreEqual(SandboxClientSurface.Unknown,
            SandboxClientErrorProbe.Classify(Shell(renderer: true), page, renderer: false));
    }

    [TestMethod]
    public void Inspect_ComFailureOrBudgetExceeded_RetainsUnknown()
    {
        var window = new SandboxClientWindow(1, 2, 3);
        Assert.AreEqual(SandboxClientSurface.Unknown,
            SandboxClientErrorProbe.Inspect(window, _ => throw new System.Runtime.InteropServices.COMException("Provider unavailable", unchecked((int)0x80040201))));
        Assert.AreEqual(SandboxClientSurface.Unknown,
            SandboxClientErrorProbe.Inspect(window, _ => throw new TimeoutException("Query limit")));
    }

    [TestMethod]
    [DataRow(unchecked((int)0x80070005), typeof(UnauthorizedAccessException))]
    [DataRow(unchecked((int)0x80070057), typeof(ArgumentException))]
    public void Inspect_MappedProviderFailure_RetainsUnknown(int hresult, Type expectedException)
    {
        var mapped = System.Runtime.InteropServices.Marshal.GetExceptionForHR(hresult);
        Assert.IsInstanceOfType(mapped, expectedException);
        var surface = SandboxClientErrorProbe.Inspect(new SandboxClientWindow(1, 2, 3), _ =>
        {
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hresult);
            throw new AssertFailedException("The failing HRESULT must throw.");
        });
        Assert.AreEqual(SandboxClientSurface.Unknown, surface);
    }

    [TestMethod]
    public void IsExpectedRoot_MissingRacingWindow_IsNotAUsableSurface()
    {
        Assert.IsFalse(SandboxClientErrorProbe.IsExpectedRoot(null, 2));
    }

    [TestMethod]
    public void Inspect_ProgrammingFailure_IsNotSilentlyConvertedToUnknown()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SandboxClientErrorProbe.Inspect(new SandboxClientWindow(1, 2, 3),
                _ => throw new InvalidOperationException("Programming error")));
    }

    [TestMethod]
    public void Inspect_UnverifiableProcessLifetime_DoesNotClaimErrorOrSession()
    {
        Assert.AreEqual(SandboxClientSurface.Unknown,
            SandboxClientErrorProbe.Inspect(new SandboxClientWindow(1, 2, 0),
                _ => throw new AssertFailedException("Unverifiable identities must not be queried.")));
    }

    private static SandboxClientErrorProbe.Control[] Shell(bool renderer = false) =>
    [
        new("InputNonClientPointerSource", "", 50033, ""),
        new("ReunionWindowingCaptionControls", "", 50033, ""),
        new("Microsoft.UI.Content.DesktopChildSiteBridge", "", 50033, ""),
        new("", "", 50037, "TitleBar"),
        .. renderer ? new[] { new SandboxClientErrorProbe.Control("AtlAxWin", "", 50033, "") } : [],
    ];

    internal static SandboxClientErrorProbe.Control[] ErrorPage(string glyph = "\uE783") =>
    [
        new("TextBlock", "localized title", 50020, ""),
        new("TextBlock", "localized body", 50020, ""),
        new("Button", "localized close", 50000, ""),
        new("TextBlock", glyph, 50020, "", IsControlElement: false),
    ];
}
