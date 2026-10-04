// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class FrameworkUdkSecurityTests
{
    private string root = null!;
    private string source = null!;

    [TestInitialize]
    public void Initialize()
    {
        root = Directory.CreateTempSubdirectory("winapp-udk-test-").FullName;
        source = Path.Combine(root, FrameworkUdkSnapshot.FileName);
        File.WriteAllText(source, "not executable");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(root, recursive: true);

    [TestMethod]
    [DataRow(@"\\server\share\Microsoft.Internal.FrameworkUdk.dll")]
    [DataRow(@"\\?\C:\Microsoft.Internal.FrameworkUdk.dll")]
    [DataRow(@"C:\runtime\file.dll:Microsoft.Internal.FrameworkUdk.dll")]
    [DataRow(@"Microsoft.Internal.FrameworkUdk.dll")]
    [DataRow(@"C:\runtime\pretender.dll")]
    public void UntrustedPath_IsRejectedBeforeFileAccess(string path) =>
        Assert.ThrowsExactly<IOException>(() => FrameworkUdkSnapshot.ValidateSourcePath(path));

    [TestMethod]
    public void UnsignedUdk_IsRejectedWithoutLoading()
    {
        var loaded = false;
        var error = Assert.ThrowsExactly<IOException>(() => XamlDiagnosticsInjector.WithAuthenticatedUdk(source,
            (_, _) => { loaded = true; return 0; }));
        StringAssert.Contains(error.Message, "signature");
        Assert.IsFalse(loaded);
    }

    [TestMethod]
    public void InvalidSignature_StopsBeforeMetadataOrArchitecture()
    {
        var error = Assert.ThrowsExactly<IOException>(() => FrameworkUdkSnapshot.Create(source, root,
            _ => false, _ => throw new AssertFailedException("Metadata accessed before trust"),
            _ => throw new AssertFailedException("Architecture accessed before trust")));
        StringAssert.Contains(error.Message, "signature");
        Assert.AreEqual(0, Directory.GetDirectories(root).Length);
    }

    [TestMethod]
    public void SignedWrongProduct_IsRejected()
    {
        var error = Assert.ThrowsExactly<IOException>(() => FrameworkUdkSnapshot.Create(source, root,
            _ => true, _ => "another.dll", _ => throw new AssertFailedException("Wrong product")));
        StringAssert.Contains(error.Message, "not the Windows App Runtime");
    }

    [TestMethod]
    public void SignedWrongArchitecture_IsRejected()
    {
        var error = Assert.ThrowsExactly<IOException>(() => FrameworkUdkSnapshot.Create(source, root,
            _ => true, _ => FrameworkUdkSnapshot.FileName, _ => "x86"));
        StringAssert.Contains(error.Message, "architecture");
    }

    [TestMethod]
    public void Verification_UsesPrivateCopy_AndPinsBytesAndNamespaceUntilDisposed()
    {
        string? copy = null;
        using (var snapshot = FrameworkUdkSnapshot.Create(source, root,
            path =>
            {
                copy = path;
                Assert.AreNotEqual(source, path);
                Assert.ThrowsExactly<IOException>(() => File.WriteAllText(path, "replace"));
                Assert.ThrowsExactly<IOException>(() => Directory.Move(Path.GetDirectoryName(path)!, path + "-moved"));
                Assert.ThrowsExactly<IOException>(() => File.WriteAllText(source, "source replaced after copy"));
                return true;
            }, _ => FrameworkUdkSnapshot.FileName, _ => RuntimeInformation.ProcessArchitecture.ToString()))
        {
            Assert.AreEqual("not executable", File.ReadAllText(snapshot.Path));
            Assert.AreEqual(copy, snapshot.Path);
            Assert.ThrowsExactly<IOException>(() => File.Delete(snapshot.Path));
        }
        Assert.IsFalse(File.Exists(copy));
        Assert.AreEqual(0, Directory.GetDirectories(root).Length);
    }

    [TestMethod]
    [DataRow("S:(ML;;NW;;;LW)", false)]
    [DataRow("S:(ML;;NR;;;ME)", false)]
    [DataRow("S:(ML;;NW;;;ME)", true)]
    [DataRow("S:(ML;;NW;;;HI)", true)]
    [DataRow("D:(A;;FA;;;SY)", true)]
    public void Snapshot_RequiresNoWriteUpAndAtLeastMediumIntegrity(string sddl, bool expected) =>
        Assert.AreEqual(expected, FrameworkUdkSnapshot.IsMediumOrHigher(new RawSecurityDescriptor(sddl)));

    [TestMethod]
    public void Worker_RejectsFailedDependencyPolicyBeforeInjection()
    {
        var called = false;
        var result = XamlDiagnosticsInjector.RunWorker(
            [XamlDiagnosticsInjector.InternalVerb, "123", "agent", source, "{}"],
            () => false, (_, _, _, _) => { called = true; return 0; });
        Assert.AreEqual(1, result);
        Assert.IsFalse(called);
    }

    [TestMethod]
    public void Worker_PolicyPrecedesInjection_AndPreservesHresult()
    {
        var restricted = false;
        var result = XamlDiagnosticsInjector.RunWorker(
            [XamlDiagnosticsInjector.InternalVerb, "123", "agent", source, "{}"],
            () => restricted = true,
            (pid, tap, udk, data) =>
            {
                Assert.IsTrue(restricted);
                Assert.AreEqual(123u, pid);
                Assert.AreEqual(source, udk);
                Assert.AreEqual("agent", tap);
                Assert.AreEqual("{}", data);
                return unchecked((int)0x80070490);
            });
        Assert.AreEqual(0, result);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("bad")]
    public void Worker_InvalidRequestCannotChangeLoaderPolicy(string pid)
    {
        Assert.AreEqual(1, XamlDiagnosticsInjector.RunWorker(
            [XamlDiagnosticsInjector.InternalVerb, pid, "agent", source, "{}"],
            () => throw new AssertFailedException("Policy applied to invalid input"),
            (_, _, _, _) => throw new AssertFailedException("Invalid injection")));
    }

    [TestMethod]
    [DataRow(259, false)]
    [DataRow(260, true)]
    public void WorkerArguments_BoundInitializationInUtf16(int units, bool rejected)
    {
        var initialization = new string('x', units);
        if (rejected)
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
                XamlDiagnosticsInjector.BuildStartInfo("winapp.exe", 123, "agent.dll", source, initialization));
            Assert.DoesNotContain(initialization, error.Message);
        }
        else
        {
            var info = XamlDiagnosticsInjector.BuildStartInfo("winapp.exe", 123, "agent.dll", source, initialization);
            Assert.AreEqual(initialization, info.ArgumentList[^1]);
        }
    }

    [TestMethod]
    public void WorkerArguments_RejectEmbeddedNul()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            XamlDiagnosticsInjector.BuildStartInfo("winapp.exe", 123, "agent.dll", source, "before\0after"));
    }

    [TestMethod]
    [DataRow(257, false)]
    [DataRow(258, true)]
    public void WorkerArguments_CountSurrogatePairsAsTwoUnits(int padding, bool rejected)
    {
        var text = new string('\u00e9', padding) + "\U0001F600";
        if (rejected)
        {
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                XamlDiagnosticsInjector.BuildStartInfo("winapp.exe", 123, "agent.dll", source, text));
        }
        else
        {
            Assert.AreEqual(text, XamlDiagnosticsInjector.BuildStartInfo(
                "winapp.exe", 123, "agent.dll", source, text).ArgumentList[^1]);
        }
    }

    [TestMethod]
    public void Worker_RejectsOversizeBeforeLoaderPolicyOrExport()
    {
        Assert.AreEqual(1, XamlDiagnosticsInjector.RunWorker(
            [XamlDiagnosticsInjector.InternalVerb, "123", "agent.dll", source, new string('x', 260)],
            () => throw new AssertFailedException("Invalid envelope reached loader policy."),
            (_, _, _, _) => throw new AssertFailedException("Invalid envelope reached injection.")));
    }

    [TestMethod]
    public async Task ProgramWorker_RejectsOversizeWithoutStdoutOrPayloadDisclosure()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            Assert.AreEqual(1, await Program.RunAsync(
                [XamlDiagnosticsInjector.InternalVerb, "123", "agent.dll", source,
                    "private-token-not-for-output" + new string('x', 260)]));
            Assert.AreEqual(string.Empty, output.ToString());
            StringAssert.Contains(error.ToString(), "259 UTF-16");
            Assert.DoesNotContain("private-token", error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [TestMethod]
    [DataRow("agent")]
    [DataRow("framework")]
    public void WorkerArguments_BoundOtherFixedPacketPaths(string field)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            XamlDiagnosticsInjector.BuildStartInfo("winapp.exe", 123,
                field == "agent" ? new string('x', 260) : "agent.dll",
                field == "framework" ? new string('x', 260) : source, "{}"));
    }

    [TestMethod]
    public void WorkerArguments_AreSeparateTokens_WithSystemWorkingDirectory()
    {
        var info = XamlDiagnosticsInjector.BuildStartInfo(@"C:\winapp folder\winapp.exe", 123,
            @"C:\agent folder\agent.dll", source, """{"quoted":"a b"}""");
        Assert.IsFalse(info.UseShellExecute);
        Assert.IsTrue(info.CreateNoWindow);
        Assert.AreEqual(Environment.SystemDirectory, info.WorkingDirectory);
        CollectionAssert.AreEqual(new[] { XamlDiagnosticsInjector.InternalVerb, "123",
            @"C:\agent folder\agent.dll", source, """{"quoted":"a b"}""" }, info.ArgumentList.ToArray());
    }

    [TestMethod]
    [TestCategory("RequiresInstalledRuntime")]
    public void GenuineInstalledAndSelfContainedUdk_VerifyWithoutExecuting()
    {
        var runtime = Environment.GetEnvironmentVariable("WINAPP_TEST_FRAMEWORKUDK");
        if (string.IsNullOrEmpty(runtime))
        {
            Assert.Inconclusive("Set WINAPP_TEST_FRAMEWORKUDK to a genuine installed runtime UDK for this read-only test.");
        }
        using (var installed = FrameworkUdkSnapshot.Create(runtime))
        {
            Assert.AreNotEqual(runtime, installed.Path);
            CollectionAssert.AreEqual(File.ReadAllBytes(runtime), File.ReadAllBytes(installed.Path));
        }
        File.Copy(runtime, source, overwrite: true);
        File.WriteAllText(Path.Combine(root, "profapi.dll"), "untrusted sibling must not be copied or loaded");
        using var selfContained = FrameworkUdkSnapshot.Create(source);
        CollectionAssert.AreEqual(new[] { FrameworkUdkSnapshot.FileName },
            Directory.GetFiles(Path.GetDirectoryName(selfContained.Path)!).Select(Path.GetFileName).ToArray());
        Assert.AreEqual(17, XamlDiagnosticsInjector.WithAuthenticatedUdk(source, (path, flags) =>
        {
            Assert.AreNotEqual(source, path);
            Assert.AreEqual(0x800u, flags, "No DLL_LOAD_DIR, application, user or current-directory search.");
            Assert.ThrowsExactly<IOException>(() => File.WriteAllText(path, "tamper"));
            CollectionAssert.AreEqual(new[] { FrameworkUdkSnapshot.FileName },
                Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).ToArray());
            return 17;
        }));
    }

    [TestMethod]
    public void WrongSigner_RenamedMicrosoftRuntimeLookingFolder_IsNotAuthority()
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "Microsoft.WindowsAppRuntime.attacker")).FullName;
        var path = Path.Combine(directory, FrameworkUdkSnapshot.FileName);
        File.Copy(source, path);
        StringAssert.Contains(Assert.ThrowsExactly<IOException>(() => FrameworkUdkSnapshot.Create(path)).Message, "signature");
    }

    [TestMethod]
    [TestCategory("RequiresInstalledRuntime")]
    [DataRow("WINAPP_TEST_CATALOG_FRAMEWORKUDK", false)]
    [DataRow("WINAPP_TEST_FRAMEWORKUDK", true)]
    public void GenuineSignatureKinds_AuthenticateExactSnapshot_AndRejectTampering(string variable, bool embedded)
    {
        var runtime = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(runtime))
        {
            Assert.Inconclusive($"Set {variable} to a genuine runtime UDK for this read-only test.");
        }
        Assert.AreEqual(embedded, AuthenticodeVerifier.IsTrustedMicrosoftSigned(runtime, NullLogger.Instance));
        using (var snapshot = FrameworkUdkSnapshot.Create(runtime))
        {
            CollectionAssert.AreEqual(File.ReadAllBytes(runtime), File.ReadAllBytes(snapshot.Path));
            Assert.ThrowsExactly<IOException>(() => File.WriteAllText(snapshot.Path, "tamper"));
        }
        var bytes = File.ReadAllBytes(runtime);
        Assert.IsGreaterThan(4096, bytes.Length);
        bytes[4096] ^= 0x5a;
        File.WriteAllBytes(source, bytes);
        StringAssert.Contains(Assert.ThrowsExactly<IOException>(() => FrameworkUdkSnapshot.Create(source)).Message, "signature");
        Assert.AreEqual(0, Directory.GetDirectories(root).Length);
    }

    [TestMethod]
    public void GenuineSignedWrongProduct_RemainsRejectedWithCatalogSupport()
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), source, overwrite: true);
        Assert.IsTrue(AuthenticodeVerifier.IsTrustedMicrosoftSignedFileOrCatalog(source, NullLogger.Instance));
        StringAssert.Contains(Assert.ThrowsExactly<IOException>(() => FrameworkUdkSnapshot.Create(source)).Message,
            "not the Windows App Runtime");
    }

    [TestMethod]
    [TestCategory("RequiresInstalledRuntime")]
    public void GenuineWrongArchitecture_RemainsRejectedWithCatalogSupport()
    {
        var runtime = Environment.GetEnvironmentVariable("WINAPP_TEST_OTHER_ARCH_FRAMEWORKUDK");
        if (string.IsNullOrEmpty(runtime))
        {
            Assert.Inconclusive("Set WINAPP_TEST_OTHER_ARCH_FRAMEWORKUDK to a genuine runtime UDK of another architecture.");
        }
        Assert.AreNotEqual(RuntimeInformation.ProcessArchitecture.ToString(),
            PeHelper.DetectPeArchitecture(runtime), ignoreCase: true);
        Assert.IsTrue(AuthenticodeVerifier.IsTrustedMicrosoftSignedFileOrCatalog(runtime, NullLogger.Instance));
        StringAssert.Contains(Assert.ThrowsExactly<IOException>(() => FrameworkUdkSnapshot.Create(runtime)).Message, "architecture");
    }

    [TestMethod]
    public void PrimaryAndFallbackModulePaths_AreHintsNotAuthority()
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "Microsoft.WindowsAppRuntime.attacker")).FullName;
        var fallback = Path.Combine(directory, FrameworkUdkSnapshot.FileName);
        File.Copy(source, fallback);
        foreach (var hint in new[]
        {
            FrameworkUdkLocator.FindInModules([(FrameworkUdkSnapshot.FileName, source)]),
            FrameworkUdkLocator.FindInModules([("Microsoft.UI.Xaml.dll", Path.Combine(directory, "Microsoft.UI.Xaml.dll"))]),
        })
        {
            Assert.IsNotNull(hint);
            var called = false;
            StringAssert.Contains(Assert.ThrowsExactly<IOException>(() =>
                XamlDiagnosticsInjector.WithAuthenticatedUdk(hint, (_, _) => { called = true; return 0; })).Message, "signature");
            Assert.IsFalse(called);
        }
    }

    [TestMethod]
    public void NetworkModuleHint_IsRejectedRatherThanProbed()
    {
        Assert.ThrowsExactly<IOException>(() => FrameworkUdkLocator.FindInModules(
            [(FrameworkUdkSnapshot.FileName, @"\\attacker\share\Microsoft.Internal.FrameworkUdk.dll")]));
        Assert.ThrowsExactly<IOException>(() => FrameworkUdkLocator.FindInModules(
            [("Microsoft.UI.Xaml.dll", @"\\attacker\share\Microsoft.WindowsAppRuntime.fake\Microsoft.UI.Xaml.dll")]));
    }

    [TestMethod]
    public void Override_IsAuthoritativeButNeverBypassesTrust()
    {
        var previous = Environment.GetEnvironmentVariable(FrameworkUdkLocator.OverrideEnvironmentVariable);
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Environment.SetEnvironmentVariable(FrameworkUdkLocator.OverrideEnvironmentVariable, source);
            Assert.AreEqual(source, FrameworkUdkLocator.Resolve(process));
            StringAssert.Contains(Assert.ThrowsExactly<IOException>(() =>
                XamlDiagnosticsInjector.WithAuthenticatedUdk(source, (_, _) => throw new AssertFailedException("Loaded override"))).Message,
                "signature");
            Environment.SetEnvironmentVariable(FrameworkUdkLocator.OverrideEnvironmentVariable, @"\\attacker\share\Microsoft.Internal.FrameworkUdk.dll");
            Assert.ThrowsExactly<IOException>(() => FrameworkUdkLocator.Resolve(process));
        }
        finally
        {
            Environment.SetEnvironmentVariable(FrameworkUdkLocator.OverrideEnvironmentVariable, previous);
        }
    }

    [TestMethod]
    public void NoRuntimeCandidate_ReturnsMissing()
    {
        Assert.IsNull(FrameworkUdkLocator.FindInModules([("app.exe", Path.Combine(root, "app.exe"))]));
        Assert.IsNull(FrameworkUdkLocator.FindInModules(
            [("Microsoft.UI.Xaml.dll", Path.Combine(root, "Microsoft.WindowsAppRuntime.missing", "Microsoft.UI.Xaml.dll"))]));
    }
}
