// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.DevTools;

/// <summary>What happened to the managed DevTools agent while connecting to an app.</summary>
internal enum ManagedAgentState
{
    /// <summary>The agent was already running (the app was launched with <c>winapp run --devtools on</c>).</summary>
    Present,
    /// <summary>The agent was loaded into the running app now.</summary>
    Loaded,
    /// <summary>The app has no .NET runtime (Native AOT or native code), so there is nothing to load the agent into.</summary>
    NotManaged,
    /// <summary>The app is a .NET app, but the agent could not be loaded; <see cref="ManagedAgentResult.Reason"/> says why.</summary>
    Unavailable,
}

internal sealed record ManagedAgentResult(ManagedAgentState State, string? Reason = null, long ElapsedMs = 0)
{
    public string ProtocolValue => State switch
    {
        ManagedAgentState.Present => "present",
        ManagedAgentState.Loaded => "loaded",
        ManagedAgentState.NotManaged => "none",
        _ => "unavailable",
    };

    /// <summary>A one-line note for people when the agent is missing, or null when it runs.</summary>
    public string? Note => State == ManagedAgentState.Unavailable
        ? $"Binding diagnosis and the Reactor component tree are unavailable: {Reason}"
        : null;
}

/// <summary>
/// The .NET diagnostics IPC protocol messages DevTools uses: <c>ApplyStartupHook</c> (command set Process 0x04,
/// command 0x07, .NET 8 and later), which runs a startup hook's <c>Initialize</c> in a process that is already running.
/// </summary>
internal static partial class DiagnosticsIpc
{
    private static readonly byte[] Magic = "DOTNET_IPC_V1\0"u8.ToArray();
    private const int HeaderSize = 20;
    private const byte ProcessCommandSet = 0x04;
    private const byte ApplyStartupHookCommand = 0x07;
    private const byte ServerCommandSet = 0xFF;
    private const byte OkCommand = 0x00;
    private const byte ErrorCommand = 0xFF;

    internal const uint UnknownCommand = 0x80131385;
    internal const uint NotSupported = 0x80131515;
    internal const uint InvalidArgument = 0x80070057;

    internal static string PipeName(int pid) => $"dotnet-diagnostic-{pid}";

    internal static byte[] ApplyStartupHook(string path)
    {
        var text = Encoding.Unicode.GetBytes(path + "\0");
        var message = new byte[HeaderSize + 4 + text.Length];
        Magic.CopyTo(message, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(14), checked((ushort)message.Length));
        message[16] = ProcessCommandSet;
        message[17] = ApplyStartupHookCommand;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(HeaderSize), (uint)(path.Length + 1));
        text.CopyTo(message, HeaderSize + 4);
        return message;
    }

    /// <summary>Reads a response: success, or the runtime's error HRESULT. Null for a malformed response.</summary>
    internal static uint? ParseResponse(ReadOnlySpan<byte> response)
    {
        if (response.Length < HeaderSize || !response[..Magic.Length].SequenceEqual(Magic) || response[16] != ServerCommandSet)
        {
            return null;
        }
        if (response[17] == OkCommand)
        {
            return 0;
        }
        return response[17] == ErrorCommand && response.Length >= HeaderSize + 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(response[HeaderSize..])
            : null;
    }

    /// <summary>
    /// Sends <c>ApplyStartupHook</c> to <paramref name="pid"/>'s diagnostics port. Null when the process has no
    /// port; otherwise the runtime's HRESULT (0 on success).
    /// </summary>
    internal static async Task<uint?> SendApplyStartupHookAsync(int pid, string hookPath, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName(pid), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return null;
        }
        // The port must belong to the target: another process could have created a pipe with its name.
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != (uint)pid)
        {
            throw new UnauthorizedAccessException($"The .NET diagnostics port for process {pid} is owned by another process.");
        }
        await pipe.WriteAsync(ApplyStartupHook(hookPath), cancellationToken).ConfigureAwait(false);
        var buffer = new byte[64];
        var read = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (read < HeaderSize + 4)
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(read), timeout.Token).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            read += n;
        }
        return ParseResponse(buffer.AsSpan(0, read)) ?? throw new InvalidDataException("The .NET diagnostics port sent an invalid response.");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}

/// <summary>
/// Loads the managed DevTools agent (binding diagnosis, the Reactor component tree) into a .NET app that is already
/// running, so <c>winapp devtools attach</c> gets the same managed features as <c>winapp run --devtools on</c>. The agent
/// is a .NET startup hook; the runtime's diagnostics IPC <c>ApplyStartupHook</c> command runs it in a live .NET 8+
/// process. Only for processes of this user at this integrity level, and only after the native engine attached.
/// </summary>
internal sealed partial class ManagedAgentLateLoad
{
    internal static string AgentPipeName(int pid) => $"winapp-devtools-binding-{pid}";

    internal Func<Process, bool> IsManaged { get; init; } = DefaultIsManaged;
    internal Func<Process, string?> TrustProblem { get; init; } =
        process => ProcessTrust.IsSameUserAndIntegrity(process, out var reason) ? null : reason;
    internal Func<int, bool> AgentPipePresent { get; init; } = DefaultAgentPipePresent;
    internal Func<Process, string?> RuntimeConfigPath { get; init; } = DefaultRuntimeConfigPath;
    internal Func<int, string, CancellationToken, Task<uint?>> SendApplyStartupHook { get; init; } = DiagnosticsIpc.SendApplyStartupHookAsync;
    internal Func<string> StageAgent { get; init; } = () => EngineStaging.StageForForeignLoad(DevToolsArtifacts.BindingHostPath);
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    internal TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<ManagedAgentResult> EnsureAsync(Process target, CancellationToken cancellationToken)
    {
        var result = await LoadAsync(target, cancellationToken).ConfigureAwait(false);
        // An app launched with the agent as a startup hook may bring its pipe up while this ran; that agent counts.
        return result.State == ManagedAgentState.Unavailable && AgentPipePresent(target.Id) ? new(ManagedAgentState.Present) : result;
    }

    private async Task<ManagedAgentResult> LoadAsync(Process target, CancellationToken cancellationToken)
    {
        var pid = target.Id;
        if (AgentPipePresent(pid))
        {
            return new(ManagedAgentState.Present);
        }
        bool managed;
        try
        {
            managed = IsManaged(target);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return new(ManagedAgentState.Unavailable, "DevTools can't read which runtime the app uses.");
        }
        if (!managed)
        {
            return new(ManagedAgentState.NotManaged);
        }
        if (TrustProblem(target) is { } trust)
        {
            return new(ManagedAgentState.Unavailable, $"DevTools loads managed code only into apps of the same user and integrity level, and {trust}.");
        }
        if (DevToolsStartupHooks.DisabledByRuntimeConfig(RuntimeConfigPath(target)))
        {
            return new(ManagedAgentState.Unavailable,
                "this build turns off .NET startup hooks (System.StartupHookProvider.IsSupported=false, usually from PublishTrimmed). " +
                "Run a Debug build, or set <StartupHookSupport>true</StartupHookSupport> for this configuration.");
        }

        var watch = Stopwatch.StartNew();
        uint? hr;
        try
        {
            hr = await SendApplyStartupHook(pid, StageAgent(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            return new(ManagedAgentState.Unavailable, $"the app's .NET diagnostics port refused the agent: {ex.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ManagedAgentState.Unavailable, "the app's .NET diagnostics port did not answer within 10 seconds.");
        }
        switch (hr)
        {
            case null:
                return new(ManagedAgentState.Unavailable,
                    "the app has no .NET diagnostics port (it may set DOTNET_EnableDiagnostics=0). Relaunch it with `winapp run --devtools on`.");
            case DiagnosticsIpc.UnknownCommand or DiagnosticsIpc.NotSupported:
                return new(ManagedAgentState.Unavailable,
                    "loading it into a running app needs .NET 8 or later. Relaunch the app with `winapp run --devtools on`.");
            case not 0:
                return new(ManagedAgentState.Unavailable, $"the app's .NET runtime refused the agent (0x{hr:X8}).");
        }
        while (watch.Elapsed < ReadyTimeout)
        {
            if (AgentPipePresent(pid))
            {
                return new(ManagedAgentState.Loaded, null, watch.ElapsedMilliseconds);
            }
            await Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
        return new(ManagedAgentState.Unavailable,
            $"the app accepted the agent, but it did not start within {(int)ReadyTimeout.TotalSeconds} seconds. " +
            "Relaunch the app with `winapp run --devtools on`.");
    }

    private static bool DefaultIsManaged(Process target) =>
        target.Modules.Cast<ProcessModule>().Any(module =>
            string.Equals(module.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase));

    private static string? DefaultRuntimeConfigPath(Process target)
    {
        try
        {
            return target.MainModule?.FileName is { } exe ? Path.ChangeExtension(exe, ".runtimeconfig.json") : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    // A pipe name that was never created fails immediately with ERROR_FILE_NOT_FOUND; a busy pipe still exists.
    private static bool DefaultAgentPipePresent(int pid) =>
        WaitNamedPipeW($@"\\.\pipe\{AgentPipeName(pid)}", 1) || Marshal.GetLastWin32Error() != 2;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipeW(string name, uint timeout);
}
