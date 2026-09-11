// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

// Direct coverage for the shared PathSafety contract: reject UNC/reparse paths
// before any filesystem probe can follow attacker-controlled redirects.
[TestClass]
public class PathSafetyTests
{
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"PathSafetyTests_{Guid.NewGuid():N}"));
        _tempDir.Create();
    }

    [TestCleanup]
    public void Teardown()
    {
        try { _tempDir.Delete(true); } catch { /* ignore */ }
    }

    // ---------------------------------------------------------------------
    // Containment
    // ---------------------------------------------------------------------

    [TestMethod]
    public void HasReparsePointOnPath_PathEqualsBoundary_ReturnsFalse()
    {
        // The boundary itself is a valid target — callers pass e.g. the
        // workspace dir as both path and boundary when checking the root.
        bool unsafePath = PathSafety.HasReparsePointOnPath(_tempDir.FullName, _tempDir.FullName);
        Assert.IsFalse(unsafePath, "boundary == path is allowed when neither is a reparse point");
    }

    [TestMethod]
    public void HasReparsePointOnPath_PathUnderBoundary_NoReparsePoints_ReturnsFalse()
    {
        var nested = Path.Combine(_tempDir.FullName, "sub", "deeper", "file.txt");
        bool unsafePath = PathSafety.HasReparsePointOnPath(nested, _tempDir.FullName);
        Assert.IsFalse(unsafePath, "deep child under a clean boundary must pass");
    }

    [TestMethod]
    public void HasReparsePointOnPath_PathOutsideBoundary_ReturnsTrue()
    {
        // A sibling of the boundary is NOT contained — refuse.
        var sibling = Path.Combine(_tempDir.Parent!.FullName, "other-workspace", "file.txt");
        bool unsafePath = PathSafety.HasReparsePointOnPath(sibling, _tempDir.FullName);
        Assert.IsTrue(unsafePath, "sibling-of-boundary must be rejected (containment violation)");
    }

    [TestMethod]
    public void HasReparsePointOnPath_PathTraversingParent_ReturnsTrue()
    {
        // `..` lets a path escape the boundary; GetFullPath should
        // normalise and containment should then reject.
        var escape = Path.Combine(_tempDir.FullName, "..", "elsewhere", "file.txt");
        bool unsafePath = PathSafety.HasReparsePointOnPath(escape, _tempDir.FullName);
        Assert.IsTrue(unsafePath, "..-escaping path must be rejected after normalisation");
    }

    [TestMethod]
    public void HasReparsePointOnPath_PrefixCollision_ReturnsTrue()
    {
        // `C:\foo-bar\file` starts with `C:\foo` as a string but is NOT
        // contained — the separator-after-boundary requirement guards
        // against this kind of substring confusion.
        var boundary = Path.Combine(_tempDir.FullName, "foo");
        var nearby = Path.Combine(_tempDir.FullName, "foo-bar", "file.txt");
        bool unsafePath = PathSafety.HasReparsePointOnPath(nearby, boundary);
        Assert.IsTrue(unsafePath, "substring-prefix collisions must NOT count as containment");
    }

    // ---------------------------------------------------------------------
    // UNC rejection
    // ---------------------------------------------------------------------

    [TestMethod]
    public void HasReparsePointOnPath_UncBoundary_ReturnsTrue()
    {
        bool unsafePath = PathSafety.HasReparsePointOnPath(
            @"\\server\share\file.txt",
            @"\\server\share");
        Assert.IsTrue(unsafePath, "UNC boundary must be refused outright");
    }

    [TestMethod]
    public void HasReparsePointOnPath_UncPath_ReturnsTrue()
    {
        bool unsafePath = PathSafety.HasReparsePointOnPath(
            @"\\server\share\file.txt",
            _tempDir.FullName);
        Assert.IsTrue(unsafePath, "UNC path must be refused outright");
    }

    [TestMethod]
    public void HasReparsePointOnPath_LongPathPrefixLocal_TreatedAsLocal()
    {
        // \\?\C:\… is the long-path prefix for a local path; NOT UNC.
        // It must NOT be rejected via the UNC check.
        var longPath = @"\\?\" + Path.Combine(_tempDir.FullName, "file.txt");
        var longBoundary = @"\\?\" + _tempDir.FullName;
        bool unsafePath = PathSafety.HasReparsePointOnPath(longPath, longBoundary);
        Assert.IsFalse(unsafePath,
            @"\\?\ long-path prefix on a local drive must NOT be treated as UNC");
    }

    [TestMethod]
    public void HasReparsePointOnPath_LongPathUncPrefix_ReturnsTrue()
    {
        // \\?\UNC\server\share IS UNC, just behind the long-path prefix —
        // must still be refused.
        bool unsafePath = PathSafety.HasReparsePointOnPath(
            @"\\?\UNC\server\share\file.txt",
            @"\\?\UNC\server\share");
        Assert.IsTrue(unsafePath, @"\\?\UNC\ is still UNC and must be refused");
    }

    // ---------------------------------------------------------------------
    // Missing segments
    // ---------------------------------------------------------------------

    [TestMethod]
    public void HasReparsePointOnPath_MissingLeaf_IsAllowed()
    {
        // Callers (e.g. ConfigService.Save) check the path
        // BEFORE creating the file — a missing leaf must not be rejected.
        var leaf = Path.Combine(_tempDir.FullName, "not-yet-written.yaml");
        Assert.IsFalse(File.Exists(leaf));
        bool unsafePath = PathSafety.HasReparsePointOnPath(leaf, _tempDir.FullName);
        Assert.IsFalse(unsafePath, "missing leaf must not trigger refusal");
    }

    [TestMethod]
    public void HasReparsePointOnPath_MissingIntermediate_IsAllowed()
    {
        var nested = Path.Combine(_tempDir.FullName, "new", "subdir", "file.txt");
        bool unsafePath = PathSafety.HasReparsePointOnPath(nested, _tempDir.FullName);
        Assert.IsFalse(unsafePath, "missing intermediates (about to be created) must pass");
    }

    // ---------------------------------------------------------------------
    // Reparse-point detection
    // ---------------------------------------------------------------------

    [TestMethod]
    public void HasReparsePointOnPath_JunctionBoundary_ReturnsTrue()
    {
        // If the boundary itself is a junction, every descendant probe
        // would silently follow it. We must refuse without ever touching
        // a descendant.
        var junctionParent = Path.Combine(_tempDir.FullName, "real");
        Directory.CreateDirectory(junctionParent);
        var junction = Path.Combine(_tempDir.FullName, "boundary-as-junction");
        if (!TryCreateJunction(junction, junctionParent))
        {
            Assert.Inconclusive("Could not create a junction (CI may lack the privilege).");
            return;
        }

        try
        {
            var leaf = Path.Combine(junction, "file.txt");
            bool unsafePath = PathSafety.HasReparsePointOnPath(leaf, junction);
            Assert.IsTrue(unsafePath, "boundary being a junction must be refused");
        }
        finally
        {
            try { Directory.Delete(junction, recursive: false); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void HasReparsePointOnPath_JunctionIntermediate_ReturnsTrue()
    {
        // A junction on an ancestor between boundary and the leaf must
        // also be refused.
        var real = Path.Combine(_tempDir.FullName, "real-target");
        Directory.CreateDirectory(real);
        var junctionDir = Path.Combine(_tempDir.FullName, "linked");
        if (!TryCreateJunction(junctionDir, real))
        {
            Assert.Inconclusive("Could not create a junction (CI may lack the privilege).");
            return;
        }

        try
        {
            var leaf = Path.Combine(junctionDir, "file.txt");
            bool unsafePath = PathSafety.HasReparsePointOnPath(leaf, _tempDir.FullName);
            Assert.IsTrue(unsafePath, "junction on the descent path must be refused");
        }
        finally
        {
            try { Directory.Delete(junctionDir, recursive: false); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void RedirectsToNetwork_OrdinaryLocalPath_ReturnsFalse()
    {
        string nested = Path.Combine(_tempDir.FullName, "packages", "lib");
        Directory.CreateDirectory(nested);

        Assert.IsFalse(PathSafety.RedirectsToNetwork(nested));
    }

    [TestMethod]
    public void RedirectsToNetwork_UncPath_ReturnsTrue()
    {
        Assert.IsTrue(PathSafety.RedirectsToNetwork(@"\\server\share\packages"));
    }

    [TestMethod]
    public void RedirectsToNetwork_JunctionToAnotherLocalDirectory_ReturnsFalse()
    {
        // Relocating a package cache with a junction is a normal developer setup. This
        // check exists to stop a redirection leaving the machine, not to ban redirection,
        // so a local target must stay allowed.
        string real = Path.Combine(_tempDir.FullName, "RealCache");
        string link = Path.Combine(_tempDir.FullName, "LinkedCache");
        Directory.CreateDirectory(Path.Combine(real, "pkg"));
        if (!TryCreateJunction(link, real))
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        try
        {
            Assert.IsFalse(PathSafety.RedirectsToNetwork(Path.Combine(link, "pkg")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public void RedirectsToNetwork_MissingPath_IsNotARedirection()
    {
        // A path that does not exist yet is not a redirection; callers still have to
        // handle it being absent.
        Assert.IsFalse(PathSafety.RedirectsToNetwork(Path.Combine(_tempDir.FullName, "nope", "still-nope")));
    }

    [TestMethod]
    public void RedirectsToNetwork_InspectsOnlyImmediateTargets()
    {
        string target = Path.Combine(_tempDir.FullName, "Cache");
        string link = Path.Combine(_tempDir.FullName, "LinkedCache");
        Directory.CreateDirectory(target);
        if (!TryCreateJunction(link, target))
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        var inspected = new List<string>();
        try
        {
            bool refused = PathSafety.RedirectsToNetwork(link, info =>
            {
                inspected.Add(info.FullName);
                return info.LinkTarget;
            });

            Assert.IsFalse(refused, "An ordinary local cache relocation remains allowed.");
            Assert.HasCount(1, inspected);
            Assert.AreEqual(@"\\?\" + link, inspected[0], "The link itself is read without short-name expansion.");
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public void RedirectsToNetwork_InspectsLinksInsideTargetPathBeforeProbingChildren()
    {
        string real = Path.Combine(_tempDir.FullName, "RealCache");
        string middle = Path.Combine(_tempDir.FullName, "MiddleCache");
        string first = Path.Combine(_tempDir.FullName, "LinkedCache");
        Directory.CreateDirectory(Path.Combine(real, "pkg"));
        if (!TryCreateJunction(middle, real))
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        bool firstCreated = false;
        var inspected = new List<string>();
        try
        {
            firstCreated = TryCreateJunction(first, Path.Combine(middle, "pkg"));
            if (!firstCreated)
            {
                Assert.Inconclusive("Could not create a junction on this machine.");
            }

            bool refused = PathSafety.RedirectsToNetwork(Path.Combine(first, "Api.winmd"), info =>
            {
                inspected.Add(info.FullName);
                // Model a network destination without creating or accessing a network link.
                return string.Equals(info.FullName, @"\\?\" + middle, StringComparison.OrdinalIgnoreCase)
                    ? @"\\server\share\PKG~1"
                    : info.LinkTarget;
            });

            Assert.IsTrue(refused, "The intermediate target link must be inspected before its child.");
            string[] expected = [@"\\?\" + first, @"\\?\" + middle];
            CollectionAssert.AreEqual(expected, inspected);
        }
        finally
        {
            if (firstCreated)
            {
                Directory.Delete(first);
            }
            Directory.Delete(middle);
        }
    }

    [TestMethod]
    public void RedirectsToNetwork_LinkWithUnknownTarget_IsRefused()
    {
        string target = Path.Combine(_tempDir.FullName, "Cache");
        string link = Path.Combine(_tempDir.FullName, "LinkedCache");
        Directory.CreateDirectory(target);
        if (!TryCreateJunction(link, target))
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        try
        {
            Assert.IsTrue(PathSafety.RedirectsToNetwork(link, static _ => null));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    [DataRow(@"\\server\share")]
    [DataRow(@"\\?\UNC\server\share")]
    [DataRow(@"\\?\GLOBALROOT\Device\Mup\server\share")]
    [DataRow(@"\\server\share\PKG~1")]
    public void RedirectsToNetwork_RejectsImmediateNetworkTarget(string networkTarget)
    {
        string target = Path.Combine(_tempDir.FullName, "Cache");
        string link = Path.Combine(_tempDir.FullName, "LinkedCache");
        Directory.CreateDirectory(target);
        if (!TryCreateJunction(link, target))
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        int resolutions = 0;
        try
        {
            // The on-disk link is local. Only the injected result names a share;
            // the test must never resolve that result or probe its descendants.
            bool refused = PathSafety.RedirectsToNetwork(Path.Combine(link, "Api.winmd"), _ =>
            {
                resolutions++;
                return networkTarget;
            });

            Assert.IsTrue(refused);
            Assert.AreEqual(1, resolutions);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public void RedirectsToNetwork_LocalTargetChain_PreservesUnvisitedSuffix()
    {
        string real = Path.Combine(_tempDir.FullName, "Cache");
        string first = Path.Combine(_tempDir.FullName, "First");
        string second = Path.Combine(_tempDir.FullName, "Second");
        string child = Path.Combine(real, "pkg");
        Directory.CreateDirectory(child);
        if (!TryCreateJunction(second, real))
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        bool firstCreated = false;
        bool childCreated = false;
        var inspected = new List<string>();
        try
        {
            firstCreated = TryCreateJunction(first, second);
            childCreated = TryCreateJunction(Path.Combine(child, "nested"), real);
            if (!firstCreated || !childCreated)
            {
                Assert.Inconclusive("Could not create a junction on this machine.");
            }

            bool refused = PathSafety.RedirectsToNetwork(Path.Combine(first, "pkg", "nested", "Api.winmd"), info =>
            {
                inspected.Add(info.FullName);
                return info.LinkTarget;
            });

            Assert.IsFalse(refused, "Local chains and a missing final file are allowed.");
            string[] expected = [@"\\?\" + first, @"\\?\" + second, @"\\?\" + Path.Combine(child, "nested")];
            CollectionAssert.AreEqual(expected, inspected);
        }
        finally
        {
            if (firstCreated)
            {
                Directory.Delete(first);
            }
            if (childCreated)
            {
                Directory.Delete(Path.Combine(child, "nested"));
            }
            Directory.Delete(second);
        }
    }

    [TestMethod]
    public void RedirectsToNetwork_RedirectionCycle_IsRefused()
    {
        string target = Path.Combine(_tempDir.FullName, "Cache");
        string first = Path.Combine(_tempDir.FullName, "First");
        string second = Path.Combine(_tempDir.FullName, "Second");
        Directory.CreateDirectory(target);
        if (!TryCreateJunction(first, target))
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        bool secondCreated = false;
        int resolutions = 0;
        try
        {
            secondCreated = TryCreateJunction(second, target);
            if (!secondCreated)
            {
                Assert.Inconclusive("Could not create a junction on this machine.");
            }

            bool refused = PathSafety.RedirectsToNetwork(Path.Combine(first, "Api.winmd"), info =>
            {
                resolutions++;
                return string.Equals(info.FullName, @"\\?\" + first, StringComparison.OrdinalIgnoreCase) ? second : first;
            });

            Assert.IsTrue(refused);
            Assert.AreEqual(2, resolutions, "Stop when the same unresolved path is encountered again.");
        }
        finally
        {
            Directory.Delete(first);
            if (secondCreated)
            {
                Directory.Delete(second);
            }
        }
    }

    [TestMethod]
    public void RedirectsToNetwork_ExpandingLinkChain_IsBounded()
    {
        string target = Path.Combine(_tempDir.FullName, "Cache");
        string link = Path.Combine(_tempDir.FullName, "LinkedCache");
        Directory.CreateDirectory(target);
        if (!TryCreateJunction(link, target))
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        int resolutions = 0;
        try
        {
            bool refused = PathSafety.RedirectsToNetwork(link, _ =>
            {
                resolutions++;
                return Path.Combine(link, "deeper");
            });

            Assert.IsTrue(refused);
            Assert.AreEqual(64, resolutions, "An expanding suffix must not evade the traversal bound.");
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RedirectsToNetwork_RelativeLocalSymbolicLink_IsAllowed(bool directory)
    {
        string target = Path.Combine(_tempDir.FullName, "Target");
        string link = Path.Combine(_tempDir.FullName, "Linked");
        if (directory)
        {
            Directory.CreateDirectory(target);
        }
        else
        {
            File.WriteAllText(target, "metadata");
        }
        try
        {
            if (directory)
            {
                Directory.CreateSymbolicLink(link, "Target");
            }
            else
            {
                File.CreateSymbolicLink(link, "Target");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Could not create a symbolic link on this machine: {ex.Message}");
        }

        try
        {
            Assert.IsFalse(PathSafety.RedirectsToNetwork(directory ? Path.Combine(link, "Api.winmd") : link));
        }
        finally
        {
            if (directory)
            {
                Directory.Delete(link);
            }
            else
            {
                File.Delete(link);
            }
        }
    }

    [TestMethod]
    [DataRow(@"C:\Cache\PKG~1\Api.winmd", @"\\?\C:\Cache\PKG~1\Api.winmd")]
    [DataRow(@"C:\Cache\sub\..\PKG~1\Api.winmd", @"\\?\C:\Cache\PKG~1\Api.winmd")]
    [DataRow(@"\\?\C:\Cache\sub\..\PKG~1\Api.winmd", @"\\?\C:\Cache\PKG~1\Api.winmd")]
    [DataRow(@"C:\", @"\\?\C:\")]
    [DataRow(@"\\?\Volume{00000000-0000-0000-0000-000000000001}\a\..\b", @"\\?\Volume{00000000-0000-0000-0000-000000000001}\b")]
    public void NormalizeLocalPathWithoutProbing_PreservesShortNamesAndLocalRoots(string path, string expected)
    {
        Assert.AreEqual(expected, PathSafety.NormalizeLocalPathWithoutProbing(path));
    }

    [TestMethod]
    [DataRow(@"\\server\share\PKG~1")]
    [DataRow(@"\\?\UNC\server\share\PKG~1")]
    [DataRow(@"\\?\GLOBALROOT\Device\Mup\server\share\PKG~1")]
    [DataRow("")]
    [DataRow("bad\0path")]
    public void NormalizeLocalPathWithoutProbing_NetworkOrInvalidPath_IsRefused(string path)
    {
        Assert.IsNull(PathSafety.NormalizeLocalPathWithoutProbing(path));
    }

    [TestMethod]
    public void NormalizeLocalPathWithoutProbing_RelativePath_UsesCurrentDirectory()
    {
        string expected = @"\\?\" + Path.Combine(Environment.CurrentDirectory, "PKG~1", "Api.winmd");
        Assert.AreEqual(expected, PathSafety.NormalizeLocalPathWithoutProbing(@"PKG~1\Api.winmd"));
    }

    [TestMethod]
    public void NormalizeLocalPathWithoutProbing_LongPath_ResizesBuffer()
    {
        string suffix = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('a', 100), 6));
        string path = Path.Combine(@"C:\Cache", suffix, "PKG~1", "Api.winmd");
        Assert.AreEqual(@"\\?\" + path, PathSafety.NormalizeLocalPathWithoutProbing(path));
    }

    [TestMethod]
    public void RedirectsToNetwork_LocalNamesContainingTildes_AreAllowed()
    {
        string target = Path.Combine(_tempDir.FullName, "Cache~1");
        string link = Path.Combine(_tempDir.FullName, "Link~1");
        Directory.CreateDirectory(target);
        if (!TryCreateJunction(link, target))
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        try
        {
            Assert.IsFalse(PathSafety.RedirectsToNetwork(Path.Combine(link, "Api.winmd")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    // Creates a junction when the host permits it; callers mark false as inconclusive.
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return false;
            }
            p.WaitForExit(5000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Drive-root boundary
    // ---------------------------------------------------------------------

    [TestMethod]
    public void HasReparsePointOnPath_DriveRootBoundary_StillRejectsJunctionDescendant()
    {
        // Drive-root boundaries must remain rooted so descendant junctions are probed.
        var junctionDir = Path.Combine(_tempDir.FullName, "boundary-drive-root-junction");
        if (!TryCreateJunction(junctionDir, Path.GetTempPath()))
        {
            Assert.Inconclusive("Could not create a junction (CI may lack the privilege).");
            return;
        }

        try
        {
            // Boundary = drive root (e.g. `C:\`). Path is a junction descendant.
            var drive = Path.GetPathRoot(_tempDir.FullName)!;
            var leaf = Path.Combine(junctionDir, "victim.yaml");
            bool unsafePath = PathSafety.HasReparsePointOnPath(leaf, drive);
            Assert.IsTrue(unsafePath, "drive-root boundary must still detect junction on descent");
        }
        finally
        {
            try { Directory.Delete(junctionDir, recursive: false); } catch { /* ignore */ }
        }
    }

    // ---------------------------------------------------------------------
    // AtomicWriteAllTextAsync
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task AtomicWriteAllTextAsync_NewFile_WritesContentsAndLeavesNoTempBehind()
    {
        var target = Path.Combine(_tempDir.FullName, "out.yaml");
        const string contents = "key: value\n";

        await PathSafety.AtomicWriteAllTextAsync(target, contents, System.Text.Encoding.UTF8);

        Assert.IsTrue(File.Exists(target), "target file must exist after atomic write");
        Assert.AreEqual(contents, File.ReadAllText(target));
        // No stray sibling temp files left behind on success.
        var siblings = Directory.GetFiles(_tempDir.FullName, "out.yaml.tmp-*");
        Assert.AreEqual(0, siblings.Length, "atomic write must remove its sibling .tmp on success");
    }

    [TestMethod]
    public async Task AtomicWriteAllTextAsync_ExistingFile_OverwritesContents()
    {
        var target = Path.Combine(_tempDir.FullName, "existing.yaml");
        File.WriteAllText(target, "old contents");

        await PathSafety.AtomicWriteAllTextAsync(target, "new contents", System.Text.Encoding.UTF8);

        Assert.AreEqual("new contents", File.ReadAllText(target));
    }

    [TestMethod]
    public async Task AtomicWriteAllTextAsync_DestinationDirMissing_ThrowsAndCleansSiblingTemp()
    {
        // Failing before temp-file creation must not leave sibling temp files behind.
        var missingDir = Path.Combine(_tempDir.FullName, "no-such-dir");
        var target = Path.Combine(missingDir, "out.yaml");

        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(async () =>
            await PathSafety.AtomicWriteAllTextAsync(target, "x", System.Text.Encoding.UTF8));

        Assert.IsFalse(Directory.Exists(missingDir), "atomic write must not create parent dirs");
        // No sibling temp under the workspace either (the failure happened
        // before any file existed).
        var stray = Directory.GetFiles(_tempDir.FullName, "*.tmp-*", SearchOption.AllDirectories);
        Assert.AreEqual(0, stray.Length, "no .tmp sibling should be left behind on failure");
    }

    [TestMethod]
    public void HasReparsePointOnPath_InvalidPathCharacters_ReturnsTrue()
    {
        // An embedded NUL makes Path.GetFullPath throw; the fail-closed catch biases to "unsafe".
        bool unsafePath = PathSafety.HasReparsePointOnPath("bad\0path", _tempDir.FullName);
        Assert.IsTrue(unsafePath, "a path that cannot be normalised must be refused");
    }

    [TestMethod]
    public void IsNetworkPath_EmptyString_ReturnsFalse()
    {
        Assert.IsFalse(PathSafety.IsNetworkPath(string.Empty));
    }

    [TestMethod]
    public void IsNetworkPath_Null_ReturnsFalse()
    {
        Assert.IsFalse(PathSafety.IsNetworkPath(null!));
    }

    [TestMethod]
    public void IsNetworkPath_LocalDevicePaths_ReturnFalse()
    {
        // A drive letter or volume GUID behind the device prefix is local storage;
        // refusing these would stop callers probing perfectly ordinary paths.
        Assert.IsFalse(PathSafety.IsNetworkPath(@"\\?\C:\repo\App.csproj"));
        Assert.IsFalse(PathSafety.IsNetworkPath(@"\\?\Volume{b75e2c83-0000-0000-0000-602f00000000}\repo\App.csproj"));
    }

    [TestMethod]
    public void IsNetworkPath_DeviceNamespaceRoutesToSmb_ReturnsTrue()
    {
        // \\?\UNC\ is the obvious spelling, but the MUP and LanmanRedirector devices
        // reach the same SMB redirector without the letters "UNC" appearing anywhere.
        // A crafted solution or project asset file naming one of these turns a local,
        // read-only query into an outbound authentication attempt.
        Assert.IsTrue(PathSafety.IsNetworkPath(@"\\?\UNC\attacker.example\share\Evil.csproj"));
        Assert.IsTrue(PathSafety.IsNetworkPath(@"\\?\GLOBALROOT\Device\Mup\attacker.example\share\Evil.csproj"));
        Assert.IsTrue(PathSafety.IsNetworkPath(@"\\.\GLOBALROOT\Device\LanmanRedirector\attacker.example\share\Evil.csproj"));
        Assert.IsTrue(PathSafety.IsNetworkPath(@"//?/GLOBALROOT/Device/Mup/attacker.example/share/Evil.csproj"));
    }

    [TestMethod]
    public async Task AtomicWriteAllTextAsync_BareFilename_WritesToCurrentDirectory()
    {
        // A path with no directory component defaults to the current working directory.
        var name = "psafe_" + Guid.NewGuid().ToString("N") + ".txt";
        var cwd = Directory.GetCurrentDirectory();
        try
        {
            await PathSafety.AtomicWriteAllTextAsync(name, "hello cwd", System.Text.Encoding.UTF8);

            var written = Path.Combine(cwd, name);
            Assert.IsTrue(File.Exists(written), "bare filename must be written under the current directory");
            Assert.AreEqual("hello cwd", File.ReadAllText(written));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(cwd, name + "*"))
            {
                try { File.Delete(f); } catch { /* best effort */ }
            }
        }
    }

    [TestMethod]
    public async Task AtomicWriteAllTextAsync_DestinationIsExistingDirectory_ThrowsAndDeletesTemp()
    {
        // The temp write succeeds, but the final move onto an existing *directory* fails; the
        // catch must delete the staged temp file and rethrow.
        var target = Path.Combine(_tempDir.FullName, "iam-a-directory");
        Directory.CreateDirectory(target);

        Exception? caught = null;
        try
        {
            await PathSafety.AtomicWriteAllTextAsync(target, "x", System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.IsNotNull(caught, "moving a file onto an existing directory must throw");
        var stray = Directory.GetFiles(_tempDir.FullName, "iam-a-directory.tmp-*");
        Assert.AreEqual(0, stray.Length, "the staged .tmp file must be deleted when the move fails");
    }
}
