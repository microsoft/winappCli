// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.UI.Accessibility;
using Windows.Win32.Foundation;
using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

/// <summary>
/// Deterministic unit tests for the pure, host-independent helpers on
/// <see cref="UiAutomationService"/>: the UIA control-type name/id mappings and the
/// blank-frame detector. These arms include control types a live WinForms provider never
/// emits (Document, DataGrid, DataItem, Header, SplitButton, SemanticZoom, AppBar, Thumb, ...),
/// so covering them from a real fixture is impossible — they are covered here directly instead.
/// </summary>
[TestClass]
[DoNotParallelize]
public class UiAutomationServicePureTests
{
    [TestCleanup]
    public void CleanupSeams()
    {
        UiAutomationService.ResetNativeSeams();
        WgcCapture.s_isSupported = global::Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported;
        WgcCapture.s_startGrabber = (hwnd, logger, fps) => WgcCapture.StartGrabber(hwnd, logger, fps);
    }

    // Note: UIA_CONTROLTYPE_ID is an internal (CsWin32-generated) enum, so it cannot appear in a
    // public [TestMethod]/[DataRow] signature. The mapping tables are therefore built inline in the
    // method bodies (where the test assembly's InternalsVisibleTo access applies).
    [TestMethod]
    public void GetControlTypeName_MapsEveryKnownControlType()
    {
        var cases = new (UIA_CONTROLTYPE_ID Id, string Name)[]
        {
            (UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId, "Button"),
            (UIA_CONTROLTYPE_ID.UIA_CalendarControlTypeId, "Calendar"),
            (UIA_CONTROLTYPE_ID.UIA_CheckBoxControlTypeId, "CheckBox"),
            (UIA_CONTROLTYPE_ID.UIA_ComboBoxControlTypeId, "ComboBox"),
            (UIA_CONTROLTYPE_ID.UIA_EditControlTypeId, "Edit"),
            (UIA_CONTROLTYPE_ID.UIA_HyperlinkControlTypeId, "Hyperlink"),
            (UIA_CONTROLTYPE_ID.UIA_ImageControlTypeId, "Image"),
            (UIA_CONTROLTYPE_ID.UIA_ListItemControlTypeId, "ListItem"),
            (UIA_CONTROLTYPE_ID.UIA_ListControlTypeId, "List"),
            (UIA_CONTROLTYPE_ID.UIA_MenuControlTypeId, "Menu"),
            (UIA_CONTROLTYPE_ID.UIA_MenuBarControlTypeId, "MenuBar"),
            (UIA_CONTROLTYPE_ID.UIA_MenuItemControlTypeId, "MenuItem"),
            (UIA_CONTROLTYPE_ID.UIA_ProgressBarControlTypeId, "ProgressBar"),
            (UIA_CONTROLTYPE_ID.UIA_RadioButtonControlTypeId, "RadioButton"),
            (UIA_CONTROLTYPE_ID.UIA_ScrollBarControlTypeId, "ScrollBar"),
            (UIA_CONTROLTYPE_ID.UIA_SliderControlTypeId, "Slider"),
            (UIA_CONTROLTYPE_ID.UIA_SpinnerControlTypeId, "Spinner"),
            (UIA_CONTROLTYPE_ID.UIA_StatusBarControlTypeId, "StatusBar"),
            (UIA_CONTROLTYPE_ID.UIA_TabControlTypeId, "Tab"),
            (UIA_CONTROLTYPE_ID.UIA_TabItemControlTypeId, "TabItem"),
            (UIA_CONTROLTYPE_ID.UIA_TextControlTypeId, "Text"),
            (UIA_CONTROLTYPE_ID.UIA_ToolBarControlTypeId, "ToolBar"),
            (UIA_CONTROLTYPE_ID.UIA_ToolTipControlTypeId, "ToolTip"),
            (UIA_CONTROLTYPE_ID.UIA_TreeControlTypeId, "Tree"),
            (UIA_CONTROLTYPE_ID.UIA_TreeItemControlTypeId, "TreeItem"),
            (UIA_CONTROLTYPE_ID.UIA_GroupControlTypeId, "Group"),
            (UIA_CONTROLTYPE_ID.UIA_ThumbControlTypeId, "Thumb"),
            (UIA_CONTROLTYPE_ID.UIA_DataGridControlTypeId, "DataGrid"),
            (UIA_CONTROLTYPE_ID.UIA_DataItemControlTypeId, "DataItem"),
            (UIA_CONTROLTYPE_ID.UIA_DocumentControlTypeId, "Document"),
            (UIA_CONTROLTYPE_ID.UIA_SplitButtonControlTypeId, "SplitButton"),
            (UIA_CONTROLTYPE_ID.UIA_WindowControlTypeId, "Window"),
            (UIA_CONTROLTYPE_ID.UIA_PaneControlTypeId, "Pane"),
            (UIA_CONTROLTYPE_ID.UIA_HeaderControlTypeId, "Header"),
            (UIA_CONTROLTYPE_ID.UIA_HeaderItemControlTypeId, "HeaderItem"),
            (UIA_CONTROLTYPE_ID.UIA_TableControlTypeId, "Table"),
            (UIA_CONTROLTYPE_ID.UIA_TitleBarControlTypeId, "TitleBar"),
            (UIA_CONTROLTYPE_ID.UIA_SeparatorControlTypeId, "Separator"),
            (UIA_CONTROLTYPE_ID.UIA_AppBarControlTypeId, "AppBar"),
            (UIA_CONTROLTYPE_ID.UIA_SemanticZoomControlTypeId, "SemanticZoom"),
        };

        foreach (var (id, name) in cases)
        {
            Assert.AreEqual(name, UiAutomationService.GetControlTypeName(id), $"for {id}");
        }
    }

    [TestMethod]
    public void GetControlTypeName_UnknownControlType_ReturnsUnknownWithNumericId()
    {
        // A value that is not present in the switch falls through to the default arm.
        var unknown = (UIA_CONTROLTYPE_ID)0;
        Assert.AreEqual("Unknown(0)", UiAutomationService.GetControlTypeName(unknown));

        var alsoUnknown = (UIA_CONTROLTYPE_ID)123456;
        Assert.AreEqual("Unknown(123456)", UiAutomationService.GetControlTypeName(alsoUnknown));
    }

    [TestMethod]
    public void MapControlType_MapsEveryKnownTypeName()
    {
        var cases = new (string Name, UIA_CONTROLTYPE_ID Id)[]
        {
            ("Button", UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId),
            ("CheckBox", UIA_CONTROLTYPE_ID.UIA_CheckBoxControlTypeId),
            ("ComboBox", UIA_CONTROLTYPE_ID.UIA_ComboBoxControlTypeId),
            ("Edit", UIA_CONTROLTYPE_ID.UIA_EditControlTypeId),
            ("TextBox", UIA_CONTROLTYPE_ID.UIA_EditControlTypeId),
            ("Hyperlink", UIA_CONTROLTYPE_ID.UIA_HyperlinkControlTypeId),
            ("Image", UIA_CONTROLTYPE_ID.UIA_ImageControlTypeId),
            ("ListItem", UIA_CONTROLTYPE_ID.UIA_ListItemControlTypeId),
            ("List", UIA_CONTROLTYPE_ID.UIA_ListControlTypeId),
            ("Menu", UIA_CONTROLTYPE_ID.UIA_MenuControlTypeId),
            ("MenuBar", UIA_CONTROLTYPE_ID.UIA_MenuBarControlTypeId),
            ("MenuItem", UIA_CONTROLTYPE_ID.UIA_MenuItemControlTypeId),
            ("ProgressBar", UIA_CONTROLTYPE_ID.UIA_ProgressBarControlTypeId),
            ("RadioButton", UIA_CONTROLTYPE_ID.UIA_RadioButtonControlTypeId),
            ("ScrollBar", UIA_CONTROLTYPE_ID.UIA_ScrollBarControlTypeId),
            ("Slider", UIA_CONTROLTYPE_ID.UIA_SliderControlTypeId),
            ("Tab", UIA_CONTROLTYPE_ID.UIA_TabControlTypeId),
            ("TabItem", UIA_CONTROLTYPE_ID.UIA_TabItemControlTypeId),
            ("Text", UIA_CONTROLTYPE_ID.UIA_TextControlTypeId),
            ("TextBlock", UIA_CONTROLTYPE_ID.UIA_TextControlTypeId),
            ("ToolBar", UIA_CONTROLTYPE_ID.UIA_ToolBarControlTypeId),
            ("Tree", UIA_CONTROLTYPE_ID.UIA_TreeControlTypeId),
            ("TreeItem", UIA_CONTROLTYPE_ID.UIA_TreeItemControlTypeId),
            ("Group", UIA_CONTROLTYPE_ID.UIA_GroupControlTypeId),
            ("DataGrid", UIA_CONTROLTYPE_ID.UIA_DataGridControlTypeId),
            ("Window", UIA_CONTROLTYPE_ID.UIA_WindowControlTypeId),
            ("Pane", UIA_CONTROLTYPE_ID.UIA_PaneControlTypeId),
            ("Table", UIA_CONTROLTYPE_ID.UIA_TableControlTypeId),
            ("TitleBar", UIA_CONTROLTYPE_ID.UIA_TitleBarControlTypeId),
        };

        foreach (var (name, id) in cases)
        {
            Assert.AreEqual((int)id, UiAutomationService.MapControlType(name), $"for '{name}'");
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("NotAControlType")]
    [DataRow("button")] // case-sensitive: lowercase does not match
    [DataRow("Custom")]
    public void MapControlType_UnknownTypeName_ReturnsZero(string typeName)
    {
        Assert.AreEqual(0, UiAutomationService.MapControlType(typeName));
    }

    [TestMethod]
    public void IsBlankCapture_AllZeroPixels_ReturnsTrue()
    {
        // 4 px * 4 bytes = 16 bytes, an exact multiple of the 8-byte (long) chunk size.
        var pixels = new byte[16];
        Assert.IsTrue(UiAutomationService.IsBlankCapture(pixels));
    }

    [TestMethod]
    public void IsBlankCapture_EmptyBuffer_ReturnsTrue()
    {
        Assert.IsTrue(UiAutomationService.IsBlankCapture([]));
    }

    [TestMethod]
    public void IsBlankCapture_NonZeroInLongChunk_ReturnsFalse()
    {
        var pixels = new byte[16];
        pixels[3] = 0xFF; // lands inside the first 8-byte chunk
        Assert.IsFalse(UiAutomationService.IsBlankCapture(pixels));
    }

    [TestMethod]
    public void IsBlankCapture_NonZeroOnlyInRemainderTail_ReturnsFalse()
    {
        // 12 bytes = one 8-byte chunk + a 4-byte remainder the tail loop must inspect.
        var pixels = new byte[12];
        pixels[11] = 0x01; // only set in the trailing remainder, past the long-chunk span
        Assert.IsFalse(UiAutomationService.IsBlankCapture(pixels));
    }

    [TestMethod]
    public void IsBlankCapture_AllZeroWithRemainderTail_ReturnsTrue()
    {
        // Exercises the remainder loop's happy path (length not a multiple of 8, all zero).
        var pixels = new byte[13];
        Assert.IsTrue(UiAutomationService.IsBlankCapture(pixels));
    }

    [TestMethod]
    public void CaptureFromWindowWithBlankRetry_RetriesBlankFrame()
    {
        var calls = 0;
        var foregrounded = false;
        UiAutomationService.s_captureFromWindow = (_, _, _) =>
            ++calls == 1 ? new byte[8] : new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        UiAutomationService.s_foregroundWindowForBlankRetry = _ => foregrounded = true;
        UiAutomationService.s_sleepForBlankRetry = ms => Assert.AreEqual(200, ms);

        var service = new UiAutomationService(NullLogger<UiAutomationService>.Instance, new UiSelectorParser());
        var pixels = service.CaptureFromWindowWithBlankRetry(new HWND(123), 1, 2);

        Assert.AreEqual(2, calls, "blank first capture must trigger one retry");
        Assert.IsTrue(foregrounded, "blank retry must foreground the target window");
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, pixels);
    }

    [TestMethod]
    public void CaptureFromWindowWithBlankRetry_NonBlankDoesNotRetry()
    {
        var calls = 0;
        UiAutomationService.s_captureFromWindow = (_, _, _) =>
        {
            calls++;
            return new byte[] { 0, 0, 0, 1 };
        };
        UiAutomationService.s_foregroundWindowForBlankRetry = _ => Assert.Fail("non-blank capture must not foreground/retry");

        var service = new UiAutomationService(NullLogger<UiAutomationService>.Instance, new UiSelectorParser());
        var pixels = service.CaptureFromWindowWithBlankRetry(new HWND(456), 1, 1);

        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, pixels[3]);
    }

    [TestMethod]
    public void CaptureScreenFrame_LetterboxesScaledContent()
    {
        (int x, int y, int sw, int sh, int tw, int th) args = default;
        UiAutomationService.s_captureFromScreenScaled = (x, y, sw, sh, tw, th) =>
        {
            args = (x, y, sw, sh, tw, th);
            return Enumerable.Repeat((byte)0x7F, tw * th * 4).ToArray();
        };

        var frame = UiAutomationService.CaptureScreenFrame(10, 20, 4, 2, 4, 4, 4, 2);

        Assert.AreEqual((10, 20, 4, 2, 4, 2), args);
        Assert.AreEqual(4 * 4 * 4, frame.Length);
        Assert.IsTrue(frame.Take(16).All(b => b == 0), "top letterbox row must remain black");
        Assert.IsTrue(frame.Skip(16).Take(32).All(b => b == 0x7F), "content rows must be copied into the centered band");
        Assert.IsTrue(frame.Skip(48).All(b => b == 0), "bottom letterbox row must remain black");
    }

    [TestMethod]
    public void CaptureScreenFrame_NoLetterboxReturnsNativeContent()
    {
        var expected = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        UiAutomationService.s_captureFromScreenScaled = (_, _, _, _, _, _) => expected;

        var frame = UiAutomationService.CaptureScreenFrame(0, 0, 2, 2, 2, 2, 2, 2);

        Assert.AreSame(expected, frame);
    }

    [TestMethod]
    public void WgcCapture_IsSupported_UsesSafeSeam()
    {
        WgcCapture.s_isSupported = () => false;
        Assert.IsFalse(WgcCapture.IsSupported());

        WgcCapture.s_isSupported = () => throw new InvalidOperationException("simulated WinRT failure");
        Assert.IsFalse(WgcCapture.IsSupported(), "IsSupported must translate WinRT probe failures to false");
    }

    [TestMethod]
    public async Task WgcCapture_NotSupportedPathsThrow()
    {
        WgcCapture.s_isSupported = () => false;

        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(
            () => WgcCapture.CaptureAsync(new HWND(123), NullLogger.Instance, CancellationToken.None));
        Assert.ThrowsExactly<PlatformNotSupportedException>(
            () => WgcCapture.StartGrabber(new HWND(123), NullLogger.Instance));
    }

    [TestMethod]
    public void WgcCapture_IsBlankCapture_HandlesChunksAndTail()
    {
        Assert.IsTrue(WgcCapture.IsBlankCapture(new byte[13]));
        var chunk = new byte[16];
        chunk[4] = 1;
        Assert.IsFalse(WgcCapture.IsBlankCapture(chunk));
        var tail = new byte[13];
        tail[12] = 1;
        Assert.IsFalse(WgcCapture.IsBlankCapture(tail));
    }

    [TestMethod]
    public void WgcCapture_ThrowIfFailed_ThrowsOnlyForNegativeHresult()
    {
        0.ThrowIfFailed("ok");

        var ex = Assert.ThrowsExactly<System.Runtime.InteropServices.COMException>(
            () => unchecked((int)0x80004005).ThrowIfFailed("CopyFrame"));
        StringAssert.Contains(ex.Message, "CopyFrame failed");
        Assert.AreEqual(unchecked((int)0x80004005), ex.HResult);
    }

}
