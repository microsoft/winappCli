// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Coverage for the window between verifying a downloaded build tool and running it. Both steps
/// name the tool by path, so unless the file is held open the bytes that were checked and the bytes
/// Windows loads are only assumed to be the same file.
/// </summary>
[TestClass]
public class VerifiedToolTests
{
    private DirectoryInfo _root = null!;
    private DirectoryInfo _binDir = null!;
    private FileInfo _tool = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), $"VerifiedTool_{Guid.NewGuid():N}"));
        _binDir = Directory.CreateDirectory(Path.Join(_root.FullName, "bin"));
        _tool = new FileInfo(Path.Join(_binDir.FullName, "mt.exe"));
        File.WriteAllText(_tool.FullName, "fake tool");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _root.Delete(true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static VerifiedTool Open(FileInfo tool, Func<string, bool>? verdict = null) =>
        VerifiedTool.Open(tool, (path, _) => verdict?.Invoke(path) ?? true, NullLogger.Instance);

    /// <summary>Everything an attacker would need to do to swap the file out from under us.</summary>
    private static bool CanBeReplaced(FileInfo tool)
    {
        try
        {
            using var writable = File.Open(tool.FullName, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string RunAndCaptureOutput(string exePath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;

        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout.Trim();
    }

    [TestMethod]
    public void Open_WhileHeld_TheToolCannotBeOverwritten()
    {
        using var held = Open(_tool);

        Assert.IsFalse(CanBeReplaced(_tool), "A verified tool must not be writable while it runs.");
    }

    [TestMethod]
    public void Open_WhileHeld_TheToolCannotBeRenamedAway()
    {
        using var held = Open(_tool);

        Assert.ThrowsExactly<IOException>(
            () => File.Move(_tool.FullName, Path.Join(_binDir.FullName, "mt.exe.old")),
            "Renaming the verified tool aside would let a replacement take its path.");
    }

    [TestMethod]
    public void Open_WhileHeld_TheToolCannotBeDeleted()
    {
        using var held = Open(_tool);

        Assert.ThrowsExactly<IOException>(() => File.Delete(_tool.FullName));
    }

    [TestMethod]
    public void Open_WhileHeld_TheContainingDirectoryCannotBeRenamed()
    {
        // Holding the file has to bind the path, not just the bytes: renaming a directory above the
        // tool would otherwise point the same path at an attacker's tree.
        var renamedBin = Path.Join(_root.FullName, "bin-old");

        using (Open(_tool))
        {
            Assert.ThrowsExactly<IOException>(() => Directory.Move(_binDir.FullName, renamedBin));
            Assert.ThrowsExactly<IOException>(() => Directory.Move(_root.FullName, _root.FullName + "-old"));
        }

        // Control: the same rename is otherwise allowed, so the assertions above are the hold
        // talking and not some unrelated reason the directory could never be moved.
        Directory.Move(_binDir.FullName, renamedBin);
        Assert.IsTrue(Directory.Exists(renamedBin));
    }

    [TestMethod]
    public void Open_VerifiesTheToolOnlyAfterItIsHeld()
    {
        // Ordering is the whole point. Verifying first and holding afterwards would leave exactly
        // the window this class exists to close.
        var replaceableDuringVerification = true;

        using var held = Open(_tool, verdict: _ =>
        {
            replaceableDuringVerification = CanBeReplaced(_tool);
            return true;
        });

        Assert.IsFalse(replaceableDuringVerification,
            "The tool must already be held when the signature check reads it.");
    }

    [TestMethod]
    public void Open_ChecksTheSameFileItHandsBackToBeLaunched()
    {
        // Checking one path and launching another is how a substituted file slips through, so the
        // path the signature check is given must be the path the caller ends up starting.
        string? checkedPath = null;

        using var verified = Open(_tool, verdict: path => { checkedPath = path; return true; });

        Assert.AreEqual(verified.Path, checkedPath);
    }

    [TestMethod]
    public void Open_AfterDispose_TheToolIsReleased()
    {
        Open(_tool).Dispose();

        Assert.IsTrue(CanBeReplaced(_tool),
            "The hold must last only as long as the caller needs it, so the cache stays usable.");
    }

    [TestMethod]
    public void Open_WhenTheSignatureCheckFails_ThrowsAndDoesNotReturnAHandle()
    {
        var ex = Assert.ThrowsExactly<BuildToolSignatureException>(
            () => Open(_tool, verdict: _ => false).Dispose());

        StringAssert.Contains(ex.Message, "not validly signed by Microsoft");
        StringAssert.Contains(ex.Message, "mt.exe");
        StringAssert.Contains(ex.Message, "NuGet cache");
    }

    [TestMethod]
    public void Open_WhenRefused_ReportsTheRemedyEvenThroughGetBaseException()
    {
        // Several callers report failures with GetBaseException().Message. Nesting anything inside
        // this exception would silently replace the remedy with a lower-level message.
        var ex = Assert.ThrowsExactly<BuildToolSignatureException>(() => Open(_tool, verdict: _ => false));
        var wrapped = new InvalidOperationException("Failed to create MSIX package.", ex);

        StringAssert.Contains(wrapped.GetBaseException().Message, "NuGet cache");
    }

    [TestMethod]
    public void Open_WhenTheSignatureCheckFails_ReleasesTheTool()
    {
        Assert.ThrowsExactly<BuildToolSignatureException>(() => Open(_tool, verdict: _ => false));

        Assert.IsTrue(CanBeReplaced(_tool),
            "A rejected tool must not stay locked; the user has to be able to delete the package.");
    }

    [TestMethod]
    public void Open_WhenTheSignatureCheckThrows_ReleasesTheTool()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => VerifiedTool.Open(
                _tool,
                (_, _) => throw new InvalidOperationException("boom"),
                NullLogger.Instance));

        Assert.IsTrue(CanBeReplaced(_tool));
    }

    [TestMethod]
    public void Open_MissingTool_FailsClosedWithASignatureException()
    {
        var missing = new FileInfo(Path.Join(_binDir.FullName, "does-not-exist.exe"));
        var verified = false;

        var ex = Assert.ThrowsExactly<BuildToolSignatureException>(
            () => Open(missing, verdict: _ => { verified = true; return true; }).Dispose());

        StringAssert.Contains(ex.Message, "could not be held open");
        Assert.IsFalse(verified, "A file that cannot be held must never reach the signature check.");
    }

    [TestMethod]
    public void Open_ToolAlreadyOpenForWriting_FailsClosed()
    {
        using var writer = File.Open(_tool.FullName, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        var ex = Assert.ThrowsExactly<BuildToolSignatureException>(() => Open(_tool).Dispose());

        StringAssert.Contains(ex.Message, "could not be held open");
    }

    [TestMethod]
    public void Open_WhileHeld_TheToolCanStillBeLaunched()
    {
        // The hold is worthless if it also blocks the launch it is protecting, and nothing else in
        // this suite would notice: every other test stands in a text file for a real executable.
        var real = new FileInfo(Path.Join(_binDir.FullName, "probe.exe"));
        File.Copy(Path.Join(Environment.SystemDirectory, "whoami.exe"), real.FullName);

        using var verified = Open(real);

        Assert.IsFalse(string.IsNullOrWhiteSpace(RunAndCaptureOutput(verified.Path)),
            "Windows must still be able to load a held executable.");
    }

    [TestMethod]
    public void Open_ARePointedJunctionCannotSubstituteADifferentTool()
    {
        // Holding the file pins the file, not the path. A junction along the way can be deleted and
        // re-created while the handle stays valid, so the path the tool was found at would go on to
        // launch whatever the junction now points at.
        var good = Directory.CreateDirectory(Path.Join(_root.FullName, "good"));
        var evil = Directory.CreateDirectory(Path.Join(_root.FullName, "evil"));
        File.Copy(Path.Join(Environment.SystemDirectory, "whoami.exe"), Path.Join(good.FullName, "tool.exe"));
        File.Copy(Path.Join(Environment.SystemDirectory, "hostname.exe"), Path.Join(evil.FullName, "tool.exe"));

        var junction = Path.Join(_root.FullName, "pkg");
        if (!TryCreateJunction(junction, good.FullName))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
        }

        var expected = RunAndCaptureOutput(Path.Join(good.FullName, "tool.exe"));
        using var verified = Open(new FileInfo(Path.Join(junction, "tool.exe")));

        Directory.Delete(junction);
        Assert.IsTrue(TryCreateJunction(junction, evil.FullName),
            "Re-pointing the junction must succeed for this test to mean anything.");

        Assert.AreEqual(expected, RunAndCaptureOutput(verified.Path),
            "The verified tool must be launched from its own location, not from a path an attacker can re-point.");
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;

        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(linkPath);
    }
}
