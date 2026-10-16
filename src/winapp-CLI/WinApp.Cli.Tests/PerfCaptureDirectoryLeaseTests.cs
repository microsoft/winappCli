// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed partial class PerfCaptureDirectoryLeaseTests
{
    [TestMethod]
    public void LeasePinsBothDirectoriesAndTheirAncestorsUntilDisposed()
    {
        var root = Directory.CreateTempSubdirectory("WinApp-Perf-Paths-");
        try
        {
            var capture = root.CreateSubdirectory("output").CreateSubdirectory("capture");
            var registry = root.CreateSubdirectory("registry").CreateSubdirectory("id");
            using (PerfCaptureDirectoryLease.Open(capture.FullName, registry.FullName))
            {
                foreach (var directory in new[] { root, capture.Parent!, capture, registry.Parent!, registry })
                {
                    Assert.Throws<IOException>(() => Directory.Move(directory.FullName, directory.FullName + "-moved"));
                }
                File.WriteAllText(Path.Join(capture.FullName, "capture.json"), "capture");
                File.WriteAllText(Path.Join(registry.FullName, "control.json"), "control");
                WinApp.Cli.Helpers.AtomicFile.WriteAllText(Path.Join(capture.FullName, "capture.json"), "updated");
            }
            Directory.Move(capture.FullName, capture.FullName + "-moved");
            Directory.Move(registry.FullName, registry.FullName + "-moved");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void LeasePreventsJunctionMutationAndAnchorRemovalUntilDisposed()
    {
        var root = Directory.CreateTempSubdirectory("WinApp-Perf-Directory-Write-");
        try
        {
            var directory = root.CreateSubdirectory("capture");
            var target = root.CreateSubdirectory("target");
            File.WriteAllText(Path.Join(target.FullName, "keep.txt"), "untouched");
            using (PerfCaptureDirectoryLease.Open(directory.FullName))
            {
                var anchor = directory.GetFiles().Single();
                Assert.Throws<IOException>(() => File.Delete(anchor.FullName));
                Assert.Throws<IOException>(() => File.Move(anchor.FullName, Path.Join(root.FullName, "moved-anchor")));
                using var handle = OpenForWrite(directory.FullName);
                Assert.IsFalse(handle.IsInvalid);
                Assert.AreEqual(145, SetJunction(handle, target.FullName),
                    "The locked anchor must prevent making the directory empty and setting a junction.");
            }
            Assert.IsEmpty(directory.GetFiles());
            using (var handle = OpenForWrite(directory.FullName))
            {
                Assert.AreEqual(0, SetJunction(handle, target.FullName), "Junction mutation must succeed after disposal.");
            }
            Assert.AreEqual("untouched", File.ReadAllText(Path.Join(target.FullName, "keep.txt")));
            directory.Delete();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExistingJunctionIsRejectedAndEarlierHandlesAreReleased(bool ancestor)
    {
        var root = Directory.CreateTempSubdirectory("WinApp-Perf-Junction-");
        var link = Path.Join(root.FullName, "link");
        try
        {
            var target = root.CreateSubdirectory("target");
            target.CreateSubdirectory("capture");
            File.WriteAllText(Path.Join(target.FullName, "keep.txt"), "untouched");
            Directory.CreateDirectory(link);
            using (var handle = OpenForWrite(link))
            {
                Assert.AreEqual(0, SetJunction(handle, target.FullName));
            }

            var exception = Assert.Throws<IOException>(() =>
                PerfCaptureDirectoryLease.Open(ancestor ? Path.Join(link, "capture") : link));
            StringAssert.Contains(exception.Message, "directory links");
            Assert.AreEqual("untouched", File.ReadAllText(Path.Join(target.FullName, "keep.txt")));
            Directory.Delete(link);
            Directory.Move(target.FullName, target.FullName + "-moved");
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void FailureOpeningLaterDirectoryReleasesEarlierPins()
    {
        var root = Directory.CreateTempSubdirectory("WinApp-Perf-Paths-Failure-");
        try
        {
            var capture = root.CreateSubdirectory("capture");
            Assert.Throws<IOException>(() =>
                PerfCaptureDirectoryLease.Open(capture.FullName, Path.Join(root.FullName, "missing")));
            Assert.IsEmpty(capture.GetFiles(), "A failed lease must remove its temporary anchor.");
            using (var handle = OpenForWrite(capture.FullName))
            {
                Assert.IsFalse(handle.IsInvalid);
                Assert.Throws<IOException>(() => PerfCaptureDirectoryLease.Open(capture.FullName),
                    "A preexisting directory write handle must prevent securing the path.");
            }
            Directory.Move(capture.FullName, capture.FullName + "-moved");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static SafeFileHandle OpenForWrite(string path) =>
        CreateFileW(path, 0x40000000, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3,
            0x02000000 | 0x00200000, IntPtr.Zero);

    private static int SetJunction(SafeFileHandle directory, string target)
    {
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target + "\0");
        var print = Encoding.Unicode.GetBytes(target + "\0");
        var buffer = new byte[16 + substitute.Length + print.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)(substitute.Length - 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)(print.Length - 2)));
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length);
        return DeviceIoControl(directory, 0x900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)
            ? 0 : Marshal.GetLastPInvokeError();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize,
        IntPtr output, int outputSize, out int bytesReturned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security,
        uint creationDisposition, uint flags, IntPtr template);
}
