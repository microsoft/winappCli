// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    public void Mp4SinkWriterEncoder_PublishAtomic_PreservesDestinationAcl()
    {
        var outputPath = Path.Combine(_tempDirectory.FullName, "acl-preserved.mp4");
        var tempPath = Path.Combine(_tempDirectory.FullName, "acl-preserved.tmp.mp4");
        File.WriteAllText(outputPath, "old");
        File.WriteAllText(tempPath, "new");

        var currentUser = WindowsIdentity.GetCurrent().User;
        Assert.IsNotNull(currentUser, "current Windows user SID must be available for ACL test");
        var rule = new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow);
        var security = new FileSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(rule);
        FileSystemAclExtensions.SetAccessControl(new FileInfo(outputPath), security);

        Mp4SinkWriterEncoder.PublishAtomic(tempPath, outputPath);

        Assert.AreEqual("new", File.ReadAllText(outputPath), "publish should replace file content");
        Assert.IsFalse(File.Exists(tempPath), "publish should consume the temp file");

        var after = FileSystemAclExtensions.GetAccessControl(new FileInfo(outputPath));
        Assert.IsTrue(after.AreAccessRulesProtected, "destination ACL inheritance setting must be preserved");
        var rules = after.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        var preserved = rules.Cast<FileSystemAccessRule>().Any(r =>
            r.IdentityReference == currentUser
            && r.AccessControlType == AccessControlType.Allow
            && (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
        Assert.IsTrue(preserved, "destination explicit FullControl ACL rule must be preserved");
    }

    [TestMethod]
    public void Mp4SinkWriterEncoder_ConstructorFailure_TempFileIsDeleted()
    {
        // Set the injectable seam: inside the constructor try-block, the seam creates the
        // temp file (simulating what MFCreateSinkWriterFromURL does) and then throws a
        // COMException (simulating a missing/unavailable H.264 encoder). The constructor catch must
        // delete the temp file and translate the error into an actionable exception.
        var outputPath = Path.Combine(_tempDirectory.FullName, "ctor-fail.mp4");
        Mp4EncoderInitializationException? thrown = null;
        try
        {
            Mp4SinkWriterEncoder.s_testFaultAfterTempCreate =
                () => throw new System.Runtime.InteropServices.COMException(
                    "Simulated codec rejection (bad fps)", unchecked((int)0xC00D36B4));

            _ = new Mp4SinkWriterEncoder(outputPath, 640, 480, 30, 2_000_000);
        }
        catch (Mp4EncoderInitializationException ex)
        {
            thrown = ex;
        }
        finally
        {
            Mp4SinkWriterEncoder.s_testFaultAfterTempCreate = null; // always clean up seam
        }

        Assert.IsNotNull(thrown, "constructor must translate Media Foundation encoder init failures");
        StringAssert.Contains(thrown.Message, "Media Feature Pack");
        StringAssert.Contains(thrown.Message, "0xC00D36B4");
        Assert.IsInstanceOfType<System.Runtime.InteropServices.COMException>(thrown.InnerException);

        // The output (final) path must not have been created.
        Assert.IsFalse(File.Exists(outputPath), "output path must not exist after constructor failure");

        // No orphaned temp .mp4 files may remain in the directory.
        var orphans = Directory.GetFiles(_tempDirectory.FullName, "*.mp4");
        Assert.AreEqual(0, orphans.Length,
            $"constructor catch must delete the temp file; orphan(s) found: {string.Join(", ", orphans)}");
    }

    [TestMethod]
    public async Task Record_EncoderInitializationFailure_ReturnsActionableError()
    {
        var inner = new System.Runtime.InteropServices.COMException(
            "Simulated missing H.264 encoder", unchecked((int)0xC00D5212));
        Assert.IsTrue(Mp4SinkWriterEncoder.TryDescribeEncoderInitFailure(inner, out var message));
        _fakeRecording.RecordException = new Mp4EncoderInitializationException(message, inner);

        var outputPath = Path.Combine(_tempDirectory.FullName, "encoder-init-fail.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "Media Feature Pack");
        StringAssert.Contains(ConsoleStdErr.ToString(), "0xC00D5212");
    }

    [TestMethod]
    public void Mp4SinkWriterEncoder_MoveFails_TempFileCleanedUp()
    {
        // If File.Move throws after Finalize (e.g. destination locked), _fileMoved remains
        // false, and Dispose() must delete the real encoder temp file rather than leave it orphaned.
        var dir = _tempDirectory.FullName;
        var finalPath = Path.Combine(dir, "final.mp4");

        // Write a sentinel so we can verify it's untouched after the failed move.
        File.WriteAllText(finalPath, "pre-existing-sentinel");

        string? tempFile = null;
        Mp4SinkWriterEncoder? encoder = null;
        try
        {
            Mp4SinkWriterEncoder.s_testPublishAtomic = (temp, _) =>
            {
                tempFile = temp;
                Assert.IsTrue(File.Exists(temp), "encoder temp file must exist before publish");
                throw new IOException("simulated publish failure");
            };

            encoder = new Mp4SinkWriterEncoder(finalPath, 64, 64, 1, 1_000_000);
            encoder.WriteFrame(MakeSolidFrame(64, 64, b: 0, g: 0, r: 0), 0, 10_000_000);
            Assert.ThrowsExactly<IOException>(() => encoder.Complete());
        }
        catch (Mp4EncoderInitializationException ex)
        {
            Assert.Inconclusive($"Media Foundation H.264 encoder unavailable: {ex.Message}");
        }
        finally
        {
            Mp4SinkWriterEncoder.s_testPublishAtomic = null;
            encoder?.Dispose();
        }

        Assert.IsNotNull(tempFile, "test seam must observe the encoder temp file path");
        Assert.IsFalse(File.Exists(tempFile), "temp file must be deleted after a failed move");
        Assert.IsTrue(File.Exists(finalPath), "pre-existing final file must be untouched");
        Assert.AreEqual("pre-existing-sentinel", File.ReadAllText(finalPath), "pre-existing file content must be unchanged");
    }

    [TestMethod]
    public void Mp4SinkWriterEncoder_NoClobberRacePreservesLateDestinationAndCleansTemp()
    {
        var finalPath = Path.Join(_tempDirectory.FullName, "late-destination.mp4");
        try
        {
            using var encoder = new Mp4SinkWriterEncoder(
                finalPath,
                64,
                64,
                1,
                1_000_000,
                overwriteExisting: false);
            encoder.WriteFrame(MakeSolidFrame(64, 64, b: 0, g: 0, r: 0), 0, 10_000_000);
            File.WriteAllText(finalPath, "late-sentinel");

            Assert.ThrowsExactly<IOException>(() => encoder.Complete());
        }
        catch (Mp4EncoderInitializationException ex)
        {
            Assert.Inconclusive($"Media Foundation H.264 encoder unavailable: {ex.Message}");
        }

        Assert.AreEqual("late-sentinel", File.ReadAllText(finalPath));
        CollectionAssert.AreEquivalent(
            new[] { finalPath },
            Directory.GetFiles(_tempDirectory.FullName, "*.mp4"));
    }
}
