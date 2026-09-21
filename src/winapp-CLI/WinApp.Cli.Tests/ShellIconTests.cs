// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Drawing;
using System.Runtime.InteropServices;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class ShellIconTests
{
    [TestMethod]
    public void GetJumboIcon_EmptyPath_DoesNotThrow()
    {
        // An empty path is resolved by the shell to a generic icon on a normal desktop, but may
        // fail on a constrained host. Either way the helper must never surface an exception.
        Icon? icon = ShellIcon.GetJumboIcon(string.Empty);
        icon?.Dispose();
    }

    [TestMethod]
    public void GetJumboIcon_NonexistentPath_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-icon-{Guid.NewGuid():N}.exe");
        Assert.IsNull(ShellIcon.GetJumboIcon(missing));
    }

    [TestMethod]
    public void GetJumboIcon_RealExecutable_ReturnsUsableIcon()
    {
        if (!Environment.UserInteractive)
        {
            Assert.Inconclusive("A shell icon requires an interactive desktop session.");
        }

        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(exe))
        {
            exe = Environment.ProcessPath!;
        }

        // Repeat acquisition to exercise COM wrapper lifetime as well as the returned HICON.
        for (var i = 0; i < 3; i++)
        {
            using var icon = ShellIcon.GetJumboIcon(exe);
            Assert.IsNotNull(icon, "Native shell acquisition must not silently fall back after a marshalling failure.");

            Assert.IsTrue(icon.Width > 0 && icon.Height > 0, "A resolved icon must have positive dimensions.");
            using var stream = new MemoryStream();
            icon.Save(stream);
            stream.Position = 0;
            using var restored = new Icon(stream);
            Assert.AreEqual(icon.Size, restored.Size, "The cloned icon must remain usable after native handles are released.");
        }
    }

    [TestMethod]
    public void GetJumboIconCore_NoSystemIcon_ReturnsNullWithoutQueryingImageList()
    {
        var imageListQueried = false;

        var icon = ShellIcon.GetJumboIconCore(
            "irrelevant",
            _ => null,
            _ => { imageListQueried = true; return SystemIcons.Application; });

        Assert.IsNull(icon, "A null system icon index must short-circuit to null.");
        Assert.IsFalse(imageListQueried, "The image list must not be queried when there is no system icon.");
    }

    [TestMethod]
    public void GetJumboIconCore_ResolvedIndex_MaterializesIconForThatIndex()
    {
        int? seenIndex = null;

        var icon = ShellIcon.GetJumboIconCore(
            "irrelevant",
            _ => 7,
            index => { seenIndex = index; return SystemIcons.Application; });

        Assert.AreEqual(7, seenIndex, "The resolved system icon index must be forwarded to the image-list resolver.");
        Assert.AreSame(SystemIcons.Application, icon);
    }

    [TestMethod]
    public void GetJumboIconCore_SystemIndexResolverThrows_ReturnsNull()
    {
        var icon = ShellIcon.GetJumboIconCore(
            "irrelevant",
            _ => throw new InvalidOperationException("shell failure"),
            _ => SystemIcons.Application);

        Assert.IsNull(icon, "Exceptions from the native resolvers must be swallowed as null.");
    }

    [TestMethod]
    public void GetJumboIconCore_ImageListResolverThrows_ReturnsNull()
    {
        var icon = ShellIcon.GetJumboIconCore(
            "irrelevant",
            _ => 3,
            _ => throw new COMException("image list failure"));

        Assert.IsNull(icon, "Exceptions while materializing the icon must be swallowed as null.");
    }

    [TestMethod]
    public void GetIconFromJumboImageList_AcquisitionReportsFailure_ReturnsNull()
    {
        // Drives the defensive guard that is unreachable via the real shell: SHGetImageList does not
        // fail on a live desktop. The behavior-preserving acquirer seam lets us assert that a failed
        // acquisition (hr.Failed == true) yields a null icon instead of dereferencing a bad list.
        Assert.IsNull(
            ShellIcon.GetIconFromJumboImageList(0, () => (Failed: true, PpvObj: null)),
            "A failed image-list acquisition must yield a null icon.");
    }

    [TestMethod]
    public void GetIconFromJumboImageList_NullImageList_ReturnsNull()
    {
        // Even when the acquire call reports success, a null COM object must be guarded against.
        Assert.IsNull(
            ShellIcon.GetIconFromJumboImageList(0, () => (Failed: false, PpvObj: null)),
            "A null image-list object must yield a null icon.");
    }

    [TestMethod]
    public void GetIconFromJumboImageList_NonImageListObject_ReturnsNull()
    {
        // A non-null object that isn't an IImageList2 must not be treated as a usable image list.
        Assert.IsNull(
            ShellIcon.GetIconFromJumboImageList(0, () => (Failed: false, PpvObj: new object())),
            "A non-IImageList2 object must yield a null icon.");
    }
}
