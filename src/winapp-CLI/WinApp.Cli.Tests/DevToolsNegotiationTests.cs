// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class DevToolsNegotiationTests
{
    private static readonly string[] NegotiationRequests = ["DevTools.ping", "DevTools.negotiate"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("NativeIntegration")]
    [DataRow("exact")]
    [DataRow("start")]
    [DataRow("binding")]
    [DataRow("epoch")]
    [DataRow("unbound")]
    [DataRow("first-local")]
    [DataRow("inspect-unavailable")]
    [DataRow("missing-writer")]
    [DataRow("writer-directory")]
    [DataRow("wrong-marker")]
    [DataRow("mixed-writer")]
    [DataRow("missing-binding")]
    [DataRow("numeric-start")]
    [DataRow("invalid-binding")]
    [DataRow("local-marker")]
    [DataRow("escaped-epoch")]
    [DataRow("first-guest")]
    [DataRow("first-guest-invalid")]
    [DataRow("boundary259")]
    [DataRow("boundary260")]
    [DataRow("unicode-boundary259")]
    [DataRow("unicode-boundary260")]
    [DataRow("readonly-fixture")]
    public async Task ManagedInitialization_RealNativeSetSiteAndHello_PreserveStrictAuthority(string scenario)
    {
        var fixture = NativeTestFixture.Resolve();
        using var expected = new GuestCommentContext();
        using var current = Process.GetCurrentProcess();
        var identity = new GuestProcessStart(current.Id, current.StartTime.ToUniversalTime().Ticks);
        var session = new GuestCommentSession(Guid.NewGuid().ToString("N"), "target",
            scenario == "escaped-epoch" || scenario.StartsWith("unicode-", StringComparison.Ordinal) ? "epoch|\"\\\u00e9\U0001F600" :
                "a4aaec93-60d0-46b5-af22-acf6f652e905:AFBCA2D4A8239863E7A6AAD1722A4644", identity);
        if (scenario == "inspect-unavailable")
        {
            expected.ActivateInspectionToken($"{identity.ProcessId}.{identity.StartTicksUtc}..{session.Epoch}",
                null, identity.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), false, default);
        }
        else
        {
            expected.Activate(identity, Path.GetTempPath(), default);
            expected.Bind(session);
        }
        using var actual = new GuestCommentContext();
        var actualIdentity = scenario == "start" ? identity with { StartTicksUtc = identity.StartTicksUtc + 1 } : identity;
        actual.Activate(actualIdentity, Path.GetTempPath(), default);
        actual.Bind(session with
        {
            Process = actualIdentity,
            BindingId = scenario is "unbound" or "inspect-unavailable" ? string.Empty :
                scenario == "binding" ? Guid.NewGuid().ToString("N") : session.BindingId,
            Epoch = scenario == "epoch" ? session.Epoch + ".different" : session.Epoch,
        });
        var directory = Directory.CreateTempSubdirectory("winapp-native-negotiation-");
        var isolatedFixture = Path.Combine(directory.FullName, "native-runtime-tests.exe");
        File.Copy(fixture, isolatedFixture);
        if (scenario == "readonly-fixture")
        {
            File.SetAttributes(isolatedFixture, File.GetAttributes(isolatedFixture) | FileAttributes.ReadOnly);
        }
        if (scenario == "writer-directory")
        {
            directory.CreateSubdirectory("winapp.exe");
        }
        else if (scenario != "missing-writer")
        {
            File.WriteAllText(Path.Combine(directory.FullName, "winapp.exe"), "not executed");
        }
        var foreignWorkingDirectory = directory.CreateSubdirectory("working");
        File.WriteAllText(Path.Combine(foreignWorkingDirectory.FullName, "winapp.exe"), "must never be selected");
        var info = new ProcessStartInfo(isolatedFixture)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = foreignWorkingDirectory.FullName,
        };
        info.ArgumentList.Add("--guest-negotiation");
        if (scenario == "first-local")
        {
            info.ArgumentList.Add(DevToolsService.BuildInitializationData(DevToolsAccess.Mutation, null, cliPath: ""));
        }
        var initialization = JsonNode.Parse(DevToolsService.BuildInitializationData(DevToolsAccess.Mutation, null, actual))!.AsObject();
        if (scenario is "first-guest" or "first-guest-invalid")
        {
            info.ArgumentList.Add(initialization.ToJsonString());
        }
        switch (scenario)
        {
            case "first-guest": initialization["guestCommentBinding"] = new string('c', 32); break;
            case "first-guest-invalid": initialization["cliSibling"] = false; break;
            case "wrong-marker": initialization["cliSibling"] = "true"; break;
            case "mixed-writer": initialization["cliExe"] = "x"; break;
            case "missing-binding": initialization.Remove("guestCommentBinding"); break;
            case "numeric-start": initialization["guestCommentStart"] = actualIdentity.StartTicksUtc; break;
            case "invalid-binding": initialization["guestCommentBinding"] = "invalid"; break;
            case "local-marker":
                initialization.Remove("guestCommentStart");
                initialization.Remove("guestCommentBinding");
                initialization.Remove("guestCommentEpoch");
                break;
        }
        var serialized = initialization.ToJsonString();
        Assert.IsLessThanOrEqualTo(259, serialized.Length, "Parser controls must actually fit the transport.");
        if (scenario.StartsWith("unicode-", StringComparison.Ordinal))
        {
            serialized = serialized.Replace("\\u00E9", "\u00e9", StringComparison.OrdinalIgnoreCase)
                .Replace("\\uD83D\\uDE00", "\U0001F600", StringComparison.OrdinalIgnoreCase);
            StringAssert.Contains(serialized, "\U0001F600");
        }
        var overflowing = scenario.EndsWith("260", StringComparison.Ordinal);
        if (scenario.Contains("boundary", StringComparison.Ordinal))
        {
            serialized = serialized.PadRight(overflowing ? 260 : 259);
        }
        info.ArgumentList.Add(serialized);
        info.Environment.Remove("WINAPP_DEVTOOLS_LOG");
        using var process = Process.Start(info)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, process.ExitCode, await error);
            using var reply = JsonDocument.Parse(await output);
            var metadata = reply.RootElement.GetProperty("result").GetProperty("guestComments");
            var rejectedWriter = scenario is "missing-writer" or "writer-directory" or "wrong-marker" or "mixed-writer" or
                "missing-binding" or "numeric-start" or "invalid-binding" or "local-marker";
            Assert.AreEqual(rejectedWriter, reply.RootElement.GetProperty("result").TryGetProperty("guestInitializationError", out _));
            if (!rejectedWriter)
            {
                Assert.AreEqual(scenario is "exact" or "inspect-unavailable" or "escaped-epoch" or "first-guest" or "first-guest-invalid" or
                    "boundary259" or "unicode-boundary259" or "readonly-fixture",
                    expected.MatchesAgent(metadata), await error);
            }
            StringAssert.Contains(await error,
                scenario == "first-local" || rejectedWriter || overflowing ? "writer-sibling=0" : "writer-sibling=1");
            if (scenario.Contains("boundary", StringComparison.Ordinal))
            {
                StringAssert.Contains(await error, overflowing ? "errno=34 returned=0" : "errno=0 returned=259");
            }
            if (scenario == "exact")
            {
                Assert.AreEqual(identity.StartTicksUtc.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    metadata.GetProperty("start").GetString(), "Native JSON must retain every tick, not a floating-point approximation.");
            }
            else if (scenario == "first-local")
            {
                Assert.AreEqual(JsonValueKind.Null, metadata.ValueKind, "A later attach must not rebind existing native authority.");
            }
            if (scenario is "unbound" or "inspect-unavailable")
            {
                Assert.AreEqual("unavailable", metadata.GetProperty("mode").GetString());
                Assert.AreEqual(string.Empty, metadata.GetProperty("binding").GetString());
                StringAssert.Contains(await error, "comment-unavailable=1");
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(output, error);
            }
            finally
            {
                var exitCode = process.HasExited ? process.ExitCode : (int?)null;
                process.Dispose();
                var attributes = File.GetAttributes(isolatedFixture);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(isolatedFixture, attributes & ~FileAttributes.ReadOnly);
                }
                TestContext.WriteLine($"Native fixture cleanup: exit code {exitCode}, " +
                    $"original attributes {attributes}, current attributes {File.GetAttributes(isolatedFixture)}.");
                await NativeFixtureCleanup.DeleteAsync(directory, TestContext.WriteLine);
            }
        }
        Assert.IsFalse(Directory.Exists(directory.FullName), "The exited fixture and both readers must be disposed before deleting its owned directory.");
    }
    [TestMethod]
    [DataRow("protocol", "negotiation failed")]
    [DataRow("shape", "capabilities")]
    [DataRow("missing", "missing")]
    [DataRow("mode", "mode is")]
    [DataRow("binding", "binding is")]
    [DataRow("epoch", "epoch is")]
    [DataRow("start", "start is")]
    [DataRow("type", "start is not a string")]
    [DataRow("missing-start", "start is missing")]
    [DataRow("unknown-error", "protocol-error")]
    [DataRow("no-response", "no-response")]
    [DataRow("writer-error", "bundled comment writer")]
    public async Task Connect_ClassifiesNegotiationWithoutDisclosingAuthority(string scenario, string expected)
    {
        using var process = Process.GetCurrentProcess();
        using var context = new GuestCommentContext();
        var identity = new GuestProcessStart(process.Id, process.StartTime.ToUniversalTime().Ticks);
        context.Activate(identity, Path.GetTempPath(), default);
        var binding = Guid.NewGuid().ToString("N");
        const string epoch = "private-epoch-that-must-not-be-printed";
        context.Bind(new(binding, "target", epoch, identity));
        using var agent = new FakeDevToolsProtocolAgent().Answer("DevTools.ping", """{"count":1}""");
        if (scenario is "protocol" or "unknown-error" or "no-response")
        {
            agent.Error("DevTools.negotiate", -32004,
                scenario == "protocol" ? "unauthorized" : scenario == "no-response" ? "no-response" : "private-token",
                "private-token-that-must-not-be-printed");
        }
        else
        {
            var mode = scenario == "mode" ? "unavailable" : "host";
            var actualBinding = scenario == "binding" ? "foreign-private-binding" : binding;
            var actualEpoch = scenario == "epoch" ? "foreign-private-epoch" : epoch;
            var start = scenario == "start" ? "0" : identity.StartTicksUtc.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var startJson = scenario == "type" ? start : JsonSerializer.Serialize(start);
            agent.Answer("DevTools.negotiate", scenario switch
            {
                "shape" => "[]",
                "missing" => "{}",
                "writer-error" => """{"guestInitializationError":"private-token-that-must-not-be-printed","guestComments":null}""",
                "missing-start" => $$$"""{"guestComments":{"mode":"host","binding":"{{{binding}}}","epoch":"{{{epoch}}}"}}""",
                _ => $$$"""{"guestComments":{"mode":"{{{mode}}}","binding":"{{{actualBinding}}}","epoch":"{{{actualEpoch}}}","start":{{{startJson}}}}}""",
            });
        }
        var service = new DevToolsService(NullLogger<DevToolsService>.Instance, new NoComments(), context);
        var result = await service.ConnectAsync((uint)process.Id, false, DevToolsAccess.Mutation, CancellationToken.None);
        Assert.IsFalse(result.Connected);
        StringAssert.Contains(result.Error!, expected);
        Assert.DoesNotContain(binding, result.Error!);
        Assert.DoesNotContain("private-", result.Error!);
        if (scenario == "protocol")
        {
            StringAssert.Contains(result.Error!, "-32004");
            StringAssert.Contains(result.Error!, "unauthorized");
            Assert.DoesNotContain("binding", result.Error!);
        }
        CollectionAssert.AreEqual(NegotiationRequests, agent.Received);
    }

    [TestMethod]
    public void GuestInitialization_BoundsFullAuthorityWithoutSerializingWriterPath()
    {
        using var context = new GuestCommentContext();
        var identity = new GuestProcessStart(123, 639253078372696999);
        context.Activate(identity, Path.GetTempPath(), default);
        context.Bind(new(new string('a', 32), "target", new string('b', 69), identity));
        var serialized = DevToolsService.BuildInitializationData(DevToolsAccess.Mutation, null, context);
        Assert.IsLessThanOrEqualTo(259, serialized.Length);
        using var document = JsonDocument.Parse(serialized);
        Assert.IsTrue(document.RootElement.GetProperty("cliSibling").GetBoolean());
        Assert.IsFalse(document.RootElement.TryGetProperty("cliExe", out _));
        Assert.AreEqual(new string('b', 69), document.RootElement.GetProperty("guestCommentEpoch").GetString());
        Assert.AreEqual(new string('a', 32), document.RootElement.GetProperty("guestCommentBinding").GetString());
        XamlDiagnosticsInjector.ValidateTransport("tap.dll", "udk.dll", serialized);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DevToolsService.BuildInitializationData(DevToolsAccess.Mutation, null, context,
                stagedTapPath: Path.Combine(Path.GetTempPath(), "WinApp.DevTools.Native.dll")));
    }

    [TestMethod]
    public void Initialization_PreservesLocalWriterAndRefusesLongOpaqueGuestEpoch()
    {
        var directory = Directory.CreateTempSubdirectory("winapp-init-");
        try
        {
            var cli = Path.Combine(directory.FullName, "winapp.exe");
            File.WriteAllText(cli, "not executed");
            var local = DevToolsService.BuildInitializationData(DevToolsAccess.Mutation, null, cliPath: cli);
            using var document = JsonDocument.Parse(local);
            Assert.AreEqual(cli, document.RootElement.GetProperty("cliExe").GetString());
            Assert.IsFalse(document.RootElement.TryGetProperty("cliSibling", out _));
            var longDirectory = directory.CreateSubdirectory(new string('x', 240 - directory.FullName.Length - 12));
            var longCli = Path.Combine(longDirectory.FullName, "winapp.exe");
            File.WriteAllText(longCli, "not executed");
            var longLocal = DevToolsService.BuildInitializationData(DevToolsAccess.Mutation, null, cliPath: longCli);
            using var longDocument = JsonDocument.Parse(longLocal);
            Assert.AreEqual(longCli, longDocument.RootElement.GetProperty("cliExe").GetString());
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                XamlDiagnosticsInjector.ValidateTransport("tap.dll", "udk.dll", longLocal));
            using var context = new GuestCommentContext();
            var identity = new GuestProcessStart(123, 639253078372696999);
            context.Activate(identity, directory.FullName, default);
            context.Bind(new(new string('a', 32), "target", new string('b', 260), identity));
            var guest = DevToolsService.BuildInitializationData(DevToolsAccess.Mutation, null, context);
            var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
                XamlDiagnosticsInjector.BuildStartInfo("winapp.exe", 123, "tap.dll", "udk.dll", guest));
            Assert.DoesNotContain(new string('b', 32), error.Message);
            using var unicode = new GuestCommentContext();
            unicode.Activate(identity, directory.FullName, default);
            unicode.Bind(new(new string('a', 32), "target", new string('\u00e9', 60), identity));
            var serializedUnicode = DevToolsService.BuildInitializationData(DevToolsAccess.Mutation, null, unicode);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                XamlDiagnosticsInjector.ValidateTransport("tap.dll", "udk.dll", serializedUnicode));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class NoComments : ICommentPusher
    {
        public (int Total, int Placed)? Push(uint pid, string fallbackRoot, string? sourceRootOverride = null,
            CancellationToken cancellationToken = default) => throw new AssertFailedException("Failed negotiation must not push comments.");

        public (int Refreshed, int Failed) PushStore(string storePath, uint? skipPid, CancellationToken cancellationToken = default) => (0, 0);
    }
}
