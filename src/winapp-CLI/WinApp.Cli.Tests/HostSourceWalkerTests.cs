// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Host-side containment: a directory junction or symlink in a source folder must never widen what
/// gets hashed, deployed, or copied into the guest.
/// </summary>
/// <remarks>
/// <para>
/// These are deliberately real links on a real filesystem rather than an abstracted file system.
/// The bug being guarded against is precisely that
/// <see cref="SearchOption.AllDirectories"/> follows a directory reparse point while every file
/// reached through it is an ordinary file with no reparse attribute — so a file-only check passes
/// all of them. Only a real link exercises that.
/// </para>
/// <para>
/// Junctions are attempted first because, unlike symbolic links, creating one needs no Developer
/// Mode and no elevation, so these tests actually run on an ordinary machine. When neither can be
/// created the outcome is <see cref="Assert.Inconclusive(string)"/> — never a silent pass, which
/// for a containment test would be worse than no test at all.
/// </para>
/// </remarks>
[TestClass]
public class HostSourceWalkerTests
{
    private const string SecretName = "secret.txt";

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The deployment snapshot must not hash a file that is only reachable through a link.
    /// </summary>
    [TestMethod]
    public async Task DeploymentSnapshot_RefusesToWalkThroughADirectoryLink()
    {
        var root = TestPaths.TempRoot(nameof(DeploymentSnapshot_RefusesToWalkThroughADirectoryLink));
        var outside = TestPaths.TempRoot("outside-deploy");

        try
        {
            PrepareSourceAndOutside(root, outside);

            if (!TryCreateDirectoryLink(TestPaths.Under(root, "linked"), outside))
            {
                Assert.Inconclusive("Creating a directory junction or symbolic link is not possible in this run.");
                return;
            }

            // Fails closed. A snapshot that quietly omitted the link would also be acceptable
            // containment, but deployment already refuses reparse *files*, and silently deploying a
            // tree that is not the folder the developer named is the outcome worth refusing loudly.
            var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
                () => DeploymentPlanner.CreateSnapshotAsync(
                    new DirectoryInfo(root), "deployment", TestContext.CancellationToken));

            Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
        }
    }

    /// <summary>
    /// The snapshot still captures the real files when the link is removed, so the test above is
    /// failing for the right reason rather than because the folder was unreadable.
    /// </summary>
    [TestMethod]
    public async Task DeploymentSnapshot_CapturesOrdinaryFiles()
    {
        var root = TestPaths.TempRoot(nameof(DeploymentSnapshot_CapturesOrdinaryFiles));

        try
        {
            Directory.CreateDirectory(TestPaths.Under(root, "nested"));
            await File.WriteAllTextAsync(
                TestPaths.Under(root, "nested", "app.dll"), "real", TestContext.CancellationToken);

            var snapshot = await DeploymentPlanner.CreateSnapshotAsync(
                new DirectoryInfo(root), "deployment", TestContext.CancellationToken);

            Assert.AreEqual(1, snapshot.Files.Count);
            Assert.AreEqual(@"nested\app.dll", snapshot.Files[0].RelativePath);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>
    /// <c>target push</c> must not enumerate — and therefore can never copy — a file that only
    /// exists outside the folder the caller asked to copy.
    /// </summary>
    [TestMethod]
    public void SandboxCopy_NeverEnumeratesAFileBehindADirectoryLink()
    {
        var root = TestPaths.TempRoot(nameof(SandboxCopy_NeverEnumeratesAFileBehindADirectoryLink));
        var outside = TestPaths.TempRoot("outside-copy");

        try
        {
            PrepareSourceAndOutside(root, outside);

            if (!TryCreateDirectoryLink(TestPaths.Under(root, "linked"), outside))
            {
                Assert.Inconclusive("Creating a directory junction or symbolic link is not possible in this run.");
                return;
            }

            var sources = TargetFileTransferService.EnumerateHostSources(
                root, TestContext.CancellationToken, out _);

            Assert.IsFalse(
                sources.Any(f => f.Name.Equals(SecretName, StringComparison.OrdinalIgnoreCase)),
                "A file reachable only through a directory link must never be enumerated for copy.");

            // The genuine content is still copied: containment, not refusing to do the job.
            Assert.IsTrue(sources.Any(f => f.Name.Equals("app.dll", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
        }
    }

    /// <summary>A source that is itself a link is refused rather than silently resolved.</summary>
    [TestMethod]
    public void SandboxCopy_RefusesALinkedSourceDirectory()
    {
        var root = TestPaths.TempRoot(nameof(SandboxCopy_RefusesALinkedSourceDirectory));
        var outside = TestPaths.TempRoot("outside-linked-source");

        try
        {
            PrepareSourceAndOutside(root, outside);
            var link = TestPaths.Under(root, "linked");

            if (!TryCreateDirectoryLink(link, outside))
            {
                Assert.Inconclusive("Creating a directory junction or symbolic link is not possible in this run.");
                return;
            }

            var failure = Assert.ThrowsExactly<ExecutionTargetException>(
                () => TargetFileTransferService.EnumerateHostSources(link, TestContext.CancellationToken, out _));

            Assert.AreEqual(ExecutionTargetErrorCodes.ArtifactFailed, failure.Error.Code);
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
        }
    }

    /// <summary>
    /// A junction pointing back at its own ancestor must end the walk, not recurse until the path
    /// length or the stack gives out.
    /// </summary>
    /// <remarks>
    /// The time bound is the assertion. Following the loop even a few levels deep would multiply the
    /// tree on every pass, so anything that completes promptly demonstrably did not follow it.
    /// </remarks>
    [TestMethod]
    public void SelfReferencingLink_TerminatesPromptly()
    {
        var root = TestPaths.TempRoot(nameof(SelfReferencingLink_TerminatesPromptly));

        try
        {
            Directory.CreateDirectory(TestPaths.Under(root, "nested"));
            File.WriteAllText(TestPaths.Under(root, "nested", "app.dll"), "real");

            if (!TryCreateDirectoryLink(TestPaths.Under(root, "nested", "loop"), root))
            {
                Assert.Inconclusive("Creating a directory junction or symbolic link is not possible in this run.");
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var files = HostSourceWalker.EnumerateFiles(
                root, HostReparsePolicy.Skip, TestContext.CancellationToken);
            stopwatch.Stop();

            Assert.AreEqual(1, files.Count, "The loop edge must be refused, leaving only the real file.");
            Assert.IsLessThan(
                TimeSpan.FromSeconds(15),
                stopwatch.Elapsed,
                "A self-referencing link must terminate the walk immediately.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>
    /// A source root that is <em>itself</em> a link is refused before anything is walked.
    /// </summary>
    /// <remarks>
    /// The per-entry check never sees the root: the walk starts by enumerating the root's contents,
    /// so a linked root would have every file beneath it reported as inside the folder that was
    /// named while it actually lives somewhere else — the same escape as an intermediate junction,
    /// one level up, and invisible to every check that only looks at entries.
    /// </remarks>
    [TestMethod]
    public async Task DeploymentSnapshot_RefusesARootThatIsItselfALink()
    {
        var real = TestPaths.TempRoot(nameof(DeploymentSnapshot_RefusesARootThatIsItselfALink));
        var outside = TestPaths.TempRoot("outside-root-deploy");
        var linkedRoot = TestPaths.Under(real, "linked-root");

        try
        {
            PrepareSourceAndOutside(real, outside);

            if (!TryCreateDirectoryLink(linkedRoot, outside))
            {
                Assert.Inconclusive("Creating a directory junction or symbolic link is not possible in this run.");
                return;
            }

            var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
                () => DeploymentPlanner.CreateSnapshotAsync(
                    new DirectoryInfo(linkedRoot), "deployment", TestContext.CancellationToken));

            Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
        }
        finally
        {
            TryDeleteDirectory(real);
            TryDeleteDirectory(outside);
        }
    }

    /// <summary>The same linked root is refused by <c>target push</c>.</summary>
    [TestMethod]
    public void SandboxCopy_RefusesARootThatIsItselfALink()
    {
        var real = TestPaths.TempRoot(nameof(SandboxCopy_RefusesARootThatIsItselfALink));
        var outside = TestPaths.TempRoot("outside-root-copy");
        var linkedRoot = TestPaths.Under(real, "linked-root");

        try
        {
            PrepareSourceAndOutside(real, outside);

            if (!TryCreateDirectoryLink(linkedRoot, outside))
            {
                Assert.Inconclusive("Creating a directory junction or symbolic link is not possible in this run.");
                return;
            }

            var failure = Assert.ThrowsExactly<ExecutionTargetException>(
                () => TargetFileTransferService.EnumerateHostSources(
                    linkedRoot, TestContext.CancellationToken, out _));

            Assert.AreEqual(ExecutionTargetErrorCodes.ArtifactFailed, failure.Error.Code);
        }
        finally
        {
            TryDeleteDirectory(real);
            TryDeleteDirectory(outside);
        }
    }

    /// <summary>
    /// The walker refuses a linked root even under <see cref="HostReparsePolicy.Skip"/>.
    /// </summary>
    /// <remarks>
    /// "Treat the link as absent" is the right rule for an entry inside the root, but applying it to
    /// the root would mean enumerating nothing and reporting success — copying zero files while the
    /// caller believes their folder was transferred. Refusing is the honest outcome.
    /// </remarks>
    [TestMethod]
    public void LinkedRoot_IsRefusedUnderBothPolicies()
    {
        var real = TestPaths.TempRoot(nameof(LinkedRoot_IsRefusedUnderBothPolicies));
        var outside = TestPaths.TempRoot("outside-root-policy");
        var linkedRoot = TestPaths.Under(real, "linked-root");

        try
        {
            PrepareSourceAndOutside(real, outside);

            if (!TryCreateDirectoryLink(linkedRoot, outside))
            {
                Assert.Inconclusive("Creating a directory junction or symbolic link is not possible in this run.");
                return;
            }

            // Materialised rather than left lazy: these projections perform the assertions, so a
            // deferred sequence that nobody enumerated would silently test nothing.
            var failures = ((HostReparsePolicy[])[HostReparsePolicy.Reject, HostReparsePolicy.Skip])
                .Select(policy => Assert.ThrowsExactly<ExecutionTargetException>(
                    () => HostSourceWalker.EnumerateFiles(linkedRoot, policy, TestContext.CancellationToken),
                    $"A linked root must be refused under {policy}."))
                .ToList();

            foreach (var failure in failures)
            {
                Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
            }
        }
        finally
        {
            TryDeleteDirectory(real);
            TryDeleteDirectory(outside);
        }
    }

    /// <summary>
    /// A file swapped for a link <em>after</em> enumeration is refused before it is read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enumeration does check each file's reparse state, but that check is exactly what the pre-read
    /// re-check exists to redo, and the read itself does not repeat it — opening a symbolic link
    /// follows it to the target like any other open. So without the leaf in the re-check, a file
    /// replaced between the walk and the read is hashed and deployed straight out of the tree.
    /// </para>
    /// <para>
    /// The timing is deterministic rather than raced: the <c>exclude</c> predicate is invoked inside
    /// the snapshot loop, after the whole enumeration has been materialised and before any later
    /// file is re-checked, so swapping the second file while the first is being considered lands
    /// exactly in the window under test.
    /// </para>
    /// <para>
    /// <b>The decoy is deliberately indistinguishable.</b> The victim is a zero-byte file — ordinary
    /// in build output — and the link that replaces it keeps its last-write time. That blinds
    /// <see cref="DeploymentPlanner.VerifyUnchanged"/> completely: it stats with
    /// <see cref="FileInfo"/>, which does <em>not</em> follow a symbolic link and so reports the
    /// link's own length of zero and the timestamp just restored, both matching what enumeration
    /// recorded. Hashing, by contrast, opens with <see cref="FileStream"/>, which does follow. So
    /// with the leaf excluded from the re-check the decoy's contents are read and recorded and
    /// nothing else objects — verified by removing the check, which makes the assertion below fail.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task DeploymentSnapshot_RefusesAFileSwappedForALinkAfterEnumeration()
    {
        var root = TestPaths.TempRoot(nameof(DeploymentSnapshot_RefusesAFileSwappedForALinkAfterEnumeration));
        var outside = TestPaths.TempRoot("outside-leaf-deploy");

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);

        // Sorted case-insensitively by full path, so "a" is considered first and "b" is still
        // pending when the swap happens.
        var first = TestPaths.Under(root, "a-first.txt");
        var victim = TestPaths.Under(root, "b-victim.txt");
        var decoy = TestPaths.Under(outside, SecretName);

        await File.WriteAllTextAsync(first, "real", TestContext.CancellationToken);
        await File.WriteAllBytesAsync(victim, [], TestContext.CancellationToken);
        await File.WriteAllTextAsync(decoy, "leak", TestContext.CancellationToken);

        var decoyHash = Sha256Hex("leak"u8.ToArray());
        var swapped = false;
        DeploymentSnapshot? snapshot = null;
        ExecutionTargetException? failure = null;

        try
        {
            try
            {
                snapshot = await DeploymentPlanner.CreateSnapshotAsync(
                    new DirectoryInfo(root),
                    "deployment",
                    TestContext.CancellationToken,
                    exclude: relativePath =>
                    {
                        if (!swapped && relativePath.Contains("a-first", StringComparison.OrdinalIgnoreCase))
                        {
                            swapped = TryReplaceWithLink(victim, decoy, outside);
                        }

                        return false;
                    });
            }
            catch (ExecutionTargetException ex)
            {
                failure = ex;
            }

            if (!swapped)
            {
                Assert.Inconclusive("Replacing a file with a link is not possible in this run.");
                return;
            }

            // The load-bearing assertion: whatever the outcome, content from outside the deployment
            // root must never have been read. This is what fails if the leaf is dropped from the
            // pre-read re-check.
            Assert.IsFalse(
                snapshot?.Files.Any(f => string.Equals(f.Sha256, decoyHash, StringComparison.OrdinalIgnoreCase)) == true,
                "Content from outside the deployment root was hashed into the snapshot.");

            Assert.IsNotNull(failure, "A file that became a link must be refused, not silently deployed.");
            Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure!.Error.Code);
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
        }
    }

    /// <summary>
    /// The pre-read re-check refuses a leaf that is a link, which is the guard <c>target push</c>
    /// runs immediately before every file it reads.
    /// </summary>
    [TestMethod]
    public void EnsureNoLinkOnPath_RefusesALeafThatIsALink()
    {
        var root = TestPaths.TempRoot(nameof(EnsureNoLinkOnPath_RefusesALeafThatIsALink));
        var outside = TestPaths.TempRoot("outside-leaf-guard");

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);

        var victim = TestPaths.Under(root, "payload.bin");
        File.WriteAllText(victim, "real");
        File.WriteAllText(TestPaths.Under(outside, SecretName), "not yours");

        try
        {
            // Passes while it is an ordinary file, so the rejection below is about the link and not
            // about the path being wrong all along.
            HostSourceWalker.EnsureNoLinkOnPath(root, victim);

            if (!TryReplaceWithLink(victim, TestPaths.Under(outside, SecretName), outside))
            {
                Assert.Inconclusive("Replacing a file with a link is not possible in this run.");
                return;
            }

            var failure = Assert.ThrowsExactly<ExecutionTargetException>(
                () => HostSourceWalker.EnsureNoLinkOnPath(root, victim));

            Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
        }
    }

    /// <summary>The pre-read re-check also refuses a root that became a link.</summary>
    [TestMethod]
    public void EnsureNoLinkOnPath_RefusesARootThatIsALink()
    {
        var real = TestPaths.TempRoot(nameof(EnsureNoLinkOnPath_RefusesARootThatIsALink));
        var outside = TestPaths.TempRoot("outside-root-guard");
        var linkedRoot = TestPaths.Under(real, "linked-root");

        try
        {
            PrepareSourceAndOutside(real, outside);

            if (!TryCreateDirectoryLink(linkedRoot, outside))
            {
                Assert.Inconclusive("Creating a directory junction or symbolic link is not possible in this run.");
                return;
            }

            var failure = Assert.ThrowsExactly<ExecutionTargetException>(
                () => HostSourceWalker.EnsureNoLinkOnPath(linkedRoot, TestPaths.Under(linkedRoot, SecretName)));

            Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
        }
        finally
        {
            TryDeleteDirectory(real);
            TryDeleteDirectory(outside);
        }
    }

    /// <summary>
    /// The pre-read re-check refuses a path through a link.
    /// </summary>
    [TestMethod]
    public void EnsureNoLinkOnPath_RefusesAPathThroughALink()
    {
        var root = TestPaths.TempRoot(nameof(EnsureNoLinkOnPath_RefusesAPathThroughALink));
        var outside = TestPaths.TempRoot("outside-ancestor");

        try
        {
            PrepareSourceAndOutside(root, outside);

            if (!TryCreateDirectoryLink(TestPaths.Under(root, "linked"), outside))
            {
                Assert.Inconclusive("Creating a directory junction or symbolic link is not possible in this run.");
                return;
            }

            // Lexically contained; only checking the ancestors' reparse state catches that the
            // content it names is not actually inside the root.
            var failure = Assert.ThrowsExactly<ExecutionTargetException>(
                () => HostSourceWalker.EnsureNoLinkOnPath(
                    root, TestPaths.Under(root, "linked", SecretName)));

            Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
        }
    }

    /// <summary>A path outside the root is refused outright.</summary>
    [TestMethod]
    public void EnsureNoLinkOnPath_RefusesAPathOutsideTheRoot()
    {
        var root = TestPaths.TempRoot(nameof(EnsureNoLinkOnPath_RefusesAPathOutsideTheRoot));
        Directory.CreateDirectory(root);

        try
        {
            var failure = Assert.ThrowsExactly<ExecutionTargetException>(
                () => HostSourceWalker.EnsureNoLinkOnPath(root, Path.Join(root + "-sibling", "app.dll")));

            Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>
    /// <c>target push</c> refuses a file swapped for a link after enumeration, end to end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven through the real <see cref="TargetFileTransferService.CopyAsync"/> against a real guest
    /// command server, so what is proven is that the copy path actually refuses to send the file —
    /// not merely that the guard would have said no if it had been asked.
    /// </para>
    /// <para>
    /// The timing is deterministic rather than raced. <c>CopyToGuestAsync</c> enumerates the source,
    /// then awaits a round trip to the guest to list what is already there, then reads each file. A
    /// transport decorator performs the swap on the first frame the copy sends — which is that list
    /// request — so it lands strictly after the enumeration has been materialised and strictly
    /// before any file is re-checked or opened.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task SandboxCopy_RefusesAFileSwappedForALinkAfterEnumeration()
    {
        var root = TestPaths.TempRoot(nameof(SandboxCopy_RefusesAFileSwappedForALinkAfterEnumeration));
        var outside = TestPaths.TempRoot("outside-leaf-copy");
        var guestManaged = TestPaths.TempRoot("guest-leaf-copy");

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(guestManaged);

        var victim = TestPaths.Under(root, "payload.bin");
        await File.WriteAllTextAsync(victim, "real", TestContext.CancellationToken);
        await File.WriteAllTextAsync(
            TestPaths.Under(outside, SecretName), "not yours", TestContext.CancellationToken);

        var swapped = false;

        try
        {
            await using var harness = new CopyHarness(
                guestManaged,
                onFirstSend: () => swapped = TryReplaceWithLink(
                    victim, TestPaths.Under(outside, SecretName), outside));

            var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
                () => TargetFileTransferService.CopyAsync(
                    harness.Channel,
                    new TargetTransferRequest(TargetTransferDirection.ToTarget, root, @"Work\leaf"),
                    TestContext.CancellationToken));

            if (!swapped)
            {
                Assert.Inconclusive("Replacing a file with a link is not possible in this run.");
                return;
            }

            Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);

            // Nothing was sent, so the guest never received content from outside the source folder.
            var guestFiles = await harness.Channel.ListFilesAsync(
                new GuestPathScope(GuestRootNames.Work, Scope: null), TestContext.CancellationToken);

            Assert.IsFalse(
                guestFiles.Any(f => f.RelativePath.Contains("payload", StringComparison.OrdinalIgnoreCase)),
                "A file that became a link must never be transferred.");
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
            TryDeleteDirectory(guestManaged);
        }
    }

    /// <summary>
    /// A single named file lands at exactly the destination it was given.
    /// </summary>
    /// <remarks>
    /// The defect: the destination kind was re-derived by asking <c>File.Exists</c> about the
    /// source root, which for a named file is its <em>parent directory</em> — always false. The
    /// copy then treated the file as a folder member and appended its name, so
    /// <c>push sandbox .\setup.ps1 Setup\setup.ps1</c> produced
    /// <c>Setup\setup.ps1\setup.ps1</c>: the command exited 0, and the file was not where the
    /// caller asked for it, so the next command failed.
    /// </remarks>
    [TestMethod]
    [DataRow(@"Setup\setup.ps1", @"Setup\setup.ps1", DisplayName = "nested destination")]
    [DataRow("setup.ps1", "setup.ps1", DisplayName = "root destination")]
    [DataRow(@"Setup\renamed.ps1", @"Setup\renamed.ps1", DisplayName = "renamed on arrival")]
    [DataRow(@"My Tools\setup.ps1", @"My Tools\setup.ps1", DisplayName = "spaces")]
    [DataRow(@"Ünïcode\sétup.ps1", @"Ünïcode\sétup.ps1", DisplayName = "non-ASCII")]
    public async Task SingleFileCopy_LandsExactlyWhereItWasPointed(string guestPath, string expected)
    {
        var root = TestPaths.TempRoot(nameof(SingleFileCopy_LandsExactlyWhereItWasPointed));
        var guestManaged = TestPaths.TempRoot("guest-single-file");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(guestManaged);

        var source = TestPaths.Under(root, "setup.ps1");
        await File.WriteAllTextAsync(source, "Write-Output hi", TestContext.CancellationToken);

        try
        {
            await using var harness = new CopyHarness(guestManaged, onFirstSend: () => { });

            var result = await TargetFileTransferService.CopyAsync(
                harness.Channel,
                new TargetTransferRequest(TargetTransferDirection.ToTarget, source, guestPath),
                TestContext.CancellationToken);

            Assert.AreEqual(1, result.Transferred);

            var guestFiles = await harness.Channel.ListFilesAsync(
                new GuestPathScope(GuestRootNames.Work, Scope: null), TestContext.CancellationToken);

            // The exact guest listing is the assertion: a path that merely *contains* the name
            // would still be satisfied by the buggy nested form.
            CollectionAssert.AreEqual(
                (string[])[expected],
                guestFiles.Select(f => f.RelativePath).ToArray());
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(guestManaged);
        }
    }

    /// <summary>A folder source still preserves its structure beneath the destination.</summary>
    /// <remarks>
    /// The counterpart to the fix: making a single file land exactly must not flatten a directory
    /// copy onto one path.
    /// </remarks>
    [TestMethod]
    public async Task DirectoryCopy_StillPreservesItsStructure()
    {
        var root = TestPaths.TempRoot(nameof(DirectoryCopy_StillPreservesItsStructure));
        var guestManaged = TestPaths.TempRoot("guest-directory");
        Directory.CreateDirectory(Path.Join(root, "nested"));
        Directory.CreateDirectory(guestManaged);

        await File.WriteAllTextAsync(TestPaths.Under(root, "a.txt"), "a", TestContext.CancellationToken);
        await File.WriteAllTextAsync(
            TestPaths.Under(root, "nested", "b.txt"), "b", TestContext.CancellationToken);

        try
        {
            await using var harness = new CopyHarness(guestManaged, onFirstSend: () => { });

            var result = await TargetFileTransferService.CopyAsync(
                harness.Channel,
                new TargetTransferRequest(TargetTransferDirection.ToTarget, root, "Payload"),
                TestContext.CancellationToken);

            Assert.AreEqual(2, result.Transferred);

            var guestFiles = await harness.Channel.ListFilesAsync(
                new GuestPathScope(GuestRootNames.Work, Scope: null), TestContext.CancellationToken);

            CollectionAssert.AreEquivalent(
                (string[])[@"Payload\a.txt", @"Payload\nested\b.txt"],
                guestFiles.Select(f => f.RelativePath).ToArray());
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(guestManaged);
        }
    }

    /// <summary>Copying the same file twice overwrites in place and reports it as unchanged.</summary>
    [TestMethod]
    public async Task RepeatedSingleFileCopy_IsANoOpAtTheSamePath()
    {
        var root = TestPaths.TempRoot(nameof(RepeatedSingleFileCopy_IsANoOpAtTheSamePath));
        var guestManaged = TestPaths.TempRoot("guest-repeat");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(guestManaged);

        var source = TestPaths.Under(root, "setup.ps1");
        await File.WriteAllTextAsync(source, "Write-Output hi", TestContext.CancellationToken);

        try
        {
            await using var harness = new CopyHarness(guestManaged, onFirstSend: () => { });
            var request = new TargetTransferRequest(TargetTransferDirection.ToTarget, source, @"Setup\setup.ps1");

            var first = await TargetFileTransferService.CopyAsync(harness.Channel, request, TestContext.CancellationToken);
            var second = await TargetFileTransferService.CopyAsync(harness.Channel, request, TestContext.CancellationToken);

            Assert.AreEqual(1, first.Transferred);
            Assert.AreEqual(0, second.Transferred, "Identical content must not be re-sent.");
            Assert.AreEqual(1, second.Skipped);

            var guestFiles = await harness.Channel.ListFilesAsync(
                new GuestPathScope(GuestRootNames.Work, Scope: null), TestContext.CancellationToken);

            // Still exactly one file: a second copy must not nest another level.
            CollectionAssert.AreEqual(
                (string[])[@"Setup\setup.ps1"],
                guestFiles.Select(f => f.RelativePath).ToArray());
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(guestManaged);
        }
    }

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

    private static void PrepareSourceAndOutside(string root, string outside)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);

        File.WriteAllText(TestPaths.Under(root, "app.dll"), "real");
        File.WriteAllText(TestPaths.Under(outside, SecretName), "not yours");
    }

    /// <summary>
    /// Replaces an existing file with a link, preferring a real file symbolic link.
    /// </summary>
    /// <remarks>
    /// A file symbolic link is the faithful shape of the attack — the enumerated file is swapped for
    /// something that redirects elsewhere — but creating one needs Developer Mode or elevation. A
    /// directory junction standing in the file's place is the privilege-free equivalent: it carries
    /// the same reparse attribute, redirects the same way, and is what the guard must reject. Both
    /// are attempted so the test runs on an ordinary machine, and false is returned only when
    /// neither is possible, so the caller reports inconclusive rather than passing vacuously.
    /// <para>
    /// The victim's last-write time is restored onto the link, so a swap of a zero-byte file is
    /// indistinguishable to any check that stats without following — which is what makes the
    /// deployment test above depend on the leaf check rather than on an unrelated safety net.
    /// </para>
    /// </remarks>
    private static bool TryReplaceWithLink(string path, string fileTarget, string directoryTarget)
    {
        DateTime lastWriteUtc;

        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(path);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            File.CreateSymbolicLink(path, fileTarget);
            TryPreserveTimestamp(path, lastWriteUtc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Symbolic links need Developer Mode or elevation; fall back to a junction.
        }

        if (!TryCreateJunction(path, directoryTarget))
        {
            return false;
        }

        TryPreserveTimestamp(path, lastWriteUtc);
        return true;
    }

    private static void TryPreserveTimestamp(string path, DateTime lastWriteUtc)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The link is in place either way; only the disguise is best effort.
        }
    }

    /// <summary>
    /// Creates a directory link, preferring a junction.
    /// </summary>
    /// <remarks>
    /// A junction is a reparse point that any user can create, so this exercises the real defect on
    /// an ordinary developer machine. A symbolic link needs Developer Mode or elevation and is only
    /// the fallback. Returning false — rather than throwing or quietly succeeding — is what lets the
    /// caller report inconclusive instead of passing without having tested anything.
    /// </remarks>
    private static bool TryCreateDirectoryLink(string linkPath, string target) =>
        TryCreateJunction(linkPath, target) || TryCreateSymbolicLink(linkPath, target);

    private static bool TryCreateJunction(string linkPath, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                ArgumentList = { "/c", "mklink", "/J", linkPath, target },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(milliseconds: 30_000);

            return Directory.Exists(linkPath)
                && new DirectoryInfo(linkPath).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool TryCreateSymbolicLink(string linkPath, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes a temp tree without ever following a link out of it.
    /// </summary>
    /// <remarks>
    /// Cleanup has to obey the same rule the code under test does. A recursive delete that followed
    /// a junction would delete the contents of the "outside" folder these tests assert about, and a
    /// recursive <em>enumeration</em> that followed one would not terminate against the
    /// self-referencing link. So links are unlinked in place, depth-first, and only real directories
    /// are descended into.
    /// </remarks>
    private static void TryDeleteDirectory(string path)
    {
        UnlinkReparsePoints(path);

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Temp cleanup is not worth failing a test over.
        }
    }

    /// <summary>Removes every link beneath a directory, link itself only, never its target.</summary>
    private static void UnlinkReparsePoints(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        FileSystemInfo[] entries;

        try
        {
            entries = new DirectoryInfo(path).GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Directory.Delete on a junction removes the link and leaves the target alone.
                    if (entry is DirectoryInfo)
                    {
                        Directory.Delete(entry.FullName);
                    }
                    else
                    {
                        File.Delete(entry.FullName);
                    }

                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    UnlinkReparsePoints(entry.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // Best effort.
            }
        }
    }

    /// <summary>
    /// Host channel against a real guest command server, with a hook that fires on the first frame
    /// the host sends.
    /// </summary>
    /// <remarks>
    /// The hook is what makes the post-enumeration swap deterministic. It is on the send side rather
    /// than the receive side because a send happens synchronously inside the copy's own call stack,
    /// so "the first frame this copy sent" is unambiguously after the source has been enumerated.
    /// </remarks>
    private sealed class CopyHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));
        private readonly Task _serverTask;

        public CopyHarness(string guestManagedRoot, Action onFirstSend)
        {
            var pair = new LoopbackTransportPair();

            var server = new GuestCommandServer(
                pair.Guest,
                ExecutionTargetEpoch.Create("sandbox-leaf", "nonce-leaf"),
                new FakeGuestProcessHostFactory(),
                new StaticGuestSessionProbe(new GuestSessionInfo(1, "WinSta0", true)),
                new GuestAgentIdentity("1.0.0", "hash", "arm64", 1, 1),
                new GuestFileService(guestManagedRoot));

            _serverTask = server.RunAsync(_cancellation.Token);

            Channel = new GuestCommandChannel(
                new FirstSendHook(pair.Host, onFirstSend),
                ExecutionTargetEpoch.Create("sandbox-leaf", "nonce-leaf"));

            Channel.Start();
        }

        public GuestCommandChannel Channel { get; }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();

            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            await Channel.DisposeAsync();
            _cancellation.Dispose();
        }

        /// <summary>Runs an action once, on the first frame sent through the wrapped transport.</summary>
        private sealed class FirstSendHook(IGuestTransport inner, Action onFirstSend) : IGuestTransport
        {
            private int _fired;

            public bool IsConnected => inner.IsConnected;

            public ValueTask SendFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
            {
                if (Interlocked.Exchange(ref _fired, 1) == 0)
                {
                    onFirstSend();
                }

                return inner.SendFrameAsync(payload, cancellationToken);
            }

            public ValueTask<ReadOnlyMemory<byte>?> ReceiveFrameAsync(CancellationToken cancellationToken) =>
                inner.ReceiveFrameAsync(cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
