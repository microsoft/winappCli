// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public class ManagedAgentLateLoadTests
{
    private const string Agent = @"C:\ProgramData\winapp\engine\abc\WinApp.DevTools.Managed.dll";

    [TestMethod]
    public void ApplyStartupHook_EncodesTheDiagnosticsIpcMessage()
    {
        var message = DiagnosticsIpc.ApplyStartupHook(Agent);

        CollectionAssert.AreEqual("DOTNET_IPC_V1\0"u8.ToArray(), message[..14]);
        Assert.AreEqual(message.Length, BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(14)));
        Assert.AreEqual(0x04, message[16], "Process command set");
        Assert.AreEqual(0x07, message[17], "ApplyStartupHook");
        Assert.AreEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(18)), "reserved");
        Assert.AreEqual((uint)(Agent.Length + 1), BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(20)), "characters including the terminator");
        Assert.AreEqual(Agent + "\0", Encoding.Unicode.GetString(message, 24, message.Length - 24));
    }

    [TestMethod]
    public void ParseResponse_ReadsSuccessAndErrors()
    {
        Assert.AreEqual(0u, DiagnosticsIpc.ParseResponse(Response(0x00, 0)));
        Assert.AreEqual(DiagnosticsIpc.UnknownCommand, DiagnosticsIpc.ParseResponse(Response(0xFF, DiagnosticsIpc.UnknownCommand)));
        Assert.IsNull(DiagnosticsIpc.ParseResponse(new byte[10]), "short");
        var badMagic = Response(0x00, 0);
        badMagic[0] = (byte)'X';
        Assert.IsNull(DiagnosticsIpc.ParseResponse(badMagic), "wrong magic");
        var otherSet = Response(0x00, 0);
        otherSet[16] = 0x04;
        Assert.IsNull(DiagnosticsIpc.ParseResponse(otherSet), "not a server response");
    }

    [TestMethod]
    public async Task Ensure_AgentAlreadyRunning_DoesNotTouchTheApp()
    {
        var sent = 0;
        var loader = Loader(present: () => true, send: () => { sent++; return 0; });

        var result = await loader.EnsureAsync(Process.GetCurrentProcess(), CancellationToken.None);

        Assert.AreEqual(ManagedAgentState.Present, result.State);
        Assert.AreEqual("present", result.ProtocolValue);
        Assert.AreEqual(0, sent);
        Assert.IsNull(result.Note);
    }

    [TestMethod]
    public async Task Ensure_NativeApp_ReportsNone()
    {
        var loader = Loader(managed: false);

        var result = await loader.EnsureAsync(Process.GetCurrentProcess(), CancellationToken.None);

        Assert.AreEqual(ManagedAgentState.NotManaged, result.State);
        Assert.AreEqual("none", result.ProtocolValue);
    }

    [TestMethod]
    public async Task Ensure_DifferentIdentity_RefusesBeforeContactingTheRuntime()
    {
        var sent = 0;
        var loader = Loader(trust: "it runs at a different integrity level (for example elevated)", send: () => { sent++; return 0; });

        var result = await loader.EnsureAsync(Process.GetCurrentProcess(), CancellationToken.None);

        Assert.AreEqual(ManagedAgentState.Unavailable, result.State);
        StringAssert.Contains(result.Reason, "same user and integrity level");
        StringAssert.Contains(result.Reason, "elevated");
        Assert.AreEqual(0, sent);
    }

    [TestMethod]
    [DataRow(false, "Binding diagnosis is unavailable")]
    [DataRow(true, "Binding diagnosis and the Reactor component tree are unavailable")]
    public async Task Ensure_StartupHooksDisabled_SaysHowToFixIt(bool reactor, string note)
    {
        var config = Path.Combine(Path.GetTempPath(), $"winapp-late-{Guid.NewGuid():N}.runtimeconfig.json");
        File.WriteAllText(config, """{"runtimeOptions":{"configProperties":{"System.StartupHookProvider.IsSupported":false}}}""");
        try
        {
            var sent = 0;
            var loader = Loader(runtimeConfig: config, send: () => { sent++; return 0; }, reactor: reactor);

            var result = await loader.EnsureAsync(Process.GetCurrentProcess(), CancellationToken.None);

            Assert.AreEqual(ManagedAgentState.Unavailable, result.State);
            StringAssert.Contains(result.Reason, "StartupHookSupport");
            Assert.AreEqual(0, sent, "the runtime silently ignores a hook it can't run, so DevTools doesn't send it");
            StringAssert.StartsWith(result.Note, note);
            Assert.AreEqual(reactor, result.Note!.Contains("Reactor", StringComparison.Ordinal), "only a Reactor app hears about Reactor");
        }
        finally
        {
            File.Delete(config);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StartupHooksDisabledWarning_NamesReactorOnlyForReactorApps(bool reactor) =>
        Assert.AreEqual(reactor, DevToolsStartupHooks.DisabledWarning(reactor).Contains("Reactor", StringComparison.Ordinal));

    [TestMethod]
    [DataRow(null, "DOTNET_EnableDiagnostics=0")]
    [DataRow(DiagnosticsIpc.UnknownCommand, ".NET 8 or later")]
    [DataRow(DiagnosticsIpc.NotSupported, ".NET 8 or later")]
    [DataRow(DiagnosticsIpc.InvalidArgument, "0x80070057")]
    public async Task Ensure_RuntimeRefusal_IsExplained(uint? hr, string expected)
    {
        var loader = Loader(send: () => hr);

        var result = await loader.EnsureAsync(Process.GetCurrentProcess(), CancellationToken.None);

        Assert.AreEqual(ManagedAgentState.Unavailable, result.State);
        StringAssert.Contains(result.Reason, expected);
    }

    [TestMethod]
    public async Task Ensure_LoadsTheStagedAgent_AndWaitsForItsPipe()
    {
        var checks = 0;
        string? path = null;
        var loader = Loader(present: () => ++checks > 3, send: () => 0, onSend: p => path = p);

        var result = await loader.EnsureAsync(Process.GetCurrentProcess(), CancellationToken.None);

        Assert.AreEqual(ManagedAgentState.Loaded, result.State);
        Assert.AreEqual("loaded", result.ProtocolValue);
        Assert.AreEqual(Agent, path);
        Assert.IsNull(result.Note);
    }

    [TestMethod]
    public async Task Ensure_AgentNeverStarts_TimesOut()
    {
        var loader = Loader(send: () => 0, timeout: TimeSpan.Zero);

        var result = await loader.EnsureAsync(Process.GetCurrentProcess(), CancellationToken.None);

        Assert.AreEqual(ManagedAgentState.Unavailable, result.State);
        StringAssert.Contains(result.Reason, "did not start");
    }

    [TestMethod]
    public async Task Ensure_AgentThatStartedFromItsStartupHookMeanwhile_IsPresent()
    {
        var checks = 0;
        // Absent at first; the runtime refuses the late load (the hook's copy is already loaded), and the pipe is up.
        var loader = Loader(present: () => ++checks > 1, send: () => DiagnosticsIpc.InvalidArgument);

        var result = await loader.EnsureAsync(Process.GetCurrentProcess(), CancellationToken.None);

        Assert.AreEqual(ManagedAgentState.Present, result.State);
        Assert.IsNull(result.Note);
    }

    [TestMethod]
    public async Task Ensure_PortOwnedByAnotherProcess_IsRefused()
    {
        var loader = new ManagedAgentLateLoad
        {
            IsManaged = _ => true,
            TrustProblem = _ => null,
            AgentPipePresent = _ => false,
            RuntimeConfigPath = _ => null,
            StageAgent = () => Agent,
            SendApplyStartupHook = (_, _, _) => throw new UnauthorizedAccessException("owned by another process"),
        };

        var result = await loader.EnsureAsync(Process.GetCurrentProcess(), CancellationToken.None);

        Assert.AreEqual(ManagedAgentState.Unavailable, result.State);
        StringAssert.Contains(result.Reason, "owned by another process");
    }

    [TestMethod]
    public async Task Ensure_ThisProcess_IsTrustedAndManaged_ButHasNoAgentPipe()
    {
        // The real identity and runtime checks against the test host itself, without sending anything.
        var sent = 0;
        var loader = new ManagedAgentLateLoad
        {
            StageAgent = () => Agent,
            SendApplyStartupHook = (_, _, _) => { sent++; return Task.FromResult<uint?>(DiagnosticsIpc.NotSupported); },
        };

        var result = await loader.EnsureAsync(Process.GetCurrentProcess(), CancellationToken.None);

        Assert.AreEqual(1, sent, "a .NET process of this user at this integrity level is eligible");
        StringAssert.Contains(result.Reason, ".NET 8 or later");
    }

    private static ManagedAgentLateLoad Loader(
        Func<bool>? present = null, bool managed = true, string? trust = null, string? runtimeConfig = null,
        Func<uint?>? send = null, Action<string>? onSend = null, TimeSpan? timeout = null, bool reactor = false) => new()
        {
            IsManaged = _ => managed,
            UsesReactor = _ => reactor,
            TrustProblem = _ => trust,
            AgentPipePresent = _ => present?.Invoke() ?? false,
            RuntimeConfigPath = _ => runtimeConfig,
            StageAgent = () => Agent,
            SendApplyStartupHook = (_, path, _) =>
            {
                onSend?.Invoke(path);
                return Task.FromResult(send is null ? 0u : send());
            },
            Delay = (_, _) => Task.CompletedTask,
            ReadyTimeout = timeout ?? TimeSpan.FromSeconds(10),
        };

    private static byte[] Response(byte command, uint hr)
    {
        var response = new byte[24];
        "DOTNET_IPC_V1\0"u8.CopyTo(response);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(14), 24);
        response[16] = 0xFF;
        response[17] = command;
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(20), hr);
        return response;
    }
}
