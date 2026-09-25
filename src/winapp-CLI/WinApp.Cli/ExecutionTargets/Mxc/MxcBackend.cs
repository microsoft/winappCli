// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.ExecutionTargets.Mxc;

internal sealed class MxcBackend(
    ExecutionTargetRef target,
    ITargetStateDirectoryProvider directoryProvider,
    IHostWinappBinaryProvider binaryProvider,
    IProcessRunner processRunner,
    ILogger<MxcBackend> logger) : IExecutionTargetBackend, IInspectableTarget, IDeletableTarget
{
    private const string StateFileName = "mxc.json";
    private const string SchemaVersion = "0.9.0-alpha";
    private MxcState? _state;

    public ExecutionTargetRef Target => target;

    public async Task<TargetSupportResult> ProbeSupportAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunAsync(["--probe"], cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (result.ExitCode == 0 &&
                document.RootElement.GetProperty("probes").GetProperty("isolationSessionAvailable").GetBoolean())
            {
                return TargetSupportResult.Supported;
            }
            return TargetSupportResult.Unsupported(Failure(
                "MXC reports that isolation sessions are unavailable on this Windows installation.",
                "Use an MXC-supported Windows build with isolation sessions enabled.").Error);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw Failure($"MXC did not return a valid availability probe: {ex.Message}",
                "Set WINAPP_MXC_PATH to a current wxc-exec.exe supporting schema 0.9.0-alpha.", ex);
        }
    }

    public async Task<TargetConnection> EnsureConnectedAsync(
        EnsureTargetOptions options, CancellationToken cancellationToken)
    {
        var state = ReadState();
        if (state is not null)
        {
            var connection = await ConnectAsync(state, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Reusing experimental MXC target {Target}.", Target.Selector);
            return connection;
        }

        logger.LogWarning(
            "Creating experimental MXC target {Target}. It shares the host OS and has unrestricted networking; " +
            "no registry settings or Windows features are changed.", Target.Selector);
        var provision = await CallAsync("provision", null, new JsonObject
        {
            ["containment"] = "isolation_session",
            ["network"] = new JsonObject
            {
                ["egress"] = new JsonObject { ["default"] = "allow" },
                ["ingress"] = new JsonObject { ["default"] = "allow", ["hostLoopback"] = "allow" },
            },
        }, cancellationToken).ConfigureAwait(false);
        using var response = JsonDocument.Parse(provision.StandardOutput);
        var result = response.RootElement.GetProperty("result");
        var metadata = result.GetProperty("metadata");
        state = new MxcState
        {
            Selector = Target.Selector,
            SandboxId = result.GetProperty("sandboxId").GetString()!,
            Workspace = metadata.GetProperty("ephemeralWorkspacePath").GetString()!,
            UserSid = metadata.GetProperty("agentUserSid").GetString()!,
            Epoch = Guid.NewGuid().ToString("N"),
        };
        // Retain the owned ID even if startup fails, so target delete can remove that exact user.
        WriteState(state);
        await CallAsync("start", state, null, cancellationToken).ConfigureAwait(false);
        state = await BootstrapAsync(state, cancellationToken).ConfigureAwait(false);
        var created = await ConnectAsync(state, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Created experimental MXC target {Target}.", Target.Selector);
        return created with { Reused = false };
    }

    public async Task<TargetAttachment> TryAttachAsync(CancellationToken cancellationToken)
    {
        var state = ReadState();
        if (state is null)
        {
            return TargetAttachment.NotRunning;
        }
        var connection = await ConnectAsync(state, cancellationToken).ConfigureAwait(false);
        return new TargetAttachment(true, connection.Epoch, connection);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        var state = ReadState();
        if (state is null)
        {
            logger.LogInformation("MXC target {Target} does not exist.", Target.Selector);
            return;
        }
        var managedRoot = Path.Join(state.Workspace, "winapp");
        var cleanup = Guard(state) + $$"""
            $root = {{PsQuote(managedRoot + Path.DirectorySeparatorChar)}};
            Get-AppxPackage | Where-Object {
                $_.IsDevelopmentMode -and $_.InstallLocation -and
                $_.InstallLocation.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
            } | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction Stop };
            """;
        var cleanupResult = await ExecAsync(state, cleanup, cancellationToken, allowStale: true).ConfigureAwait(false);
        if (!IsStale(cleanupResult))
        {
            await CallAsync("deprovision", state, null, cancellationToken, allowStale: true).ConfigureAwait(false);
        }
        File.Delete(StatePath(create: false));
        logger.LogInformation("Deleted MXC target {Target}.", Target.Selector);
    }

    public IReadOnlyDictionary<string, string> DescribeForDiagnostics()
    {
        var state = _state ?? ReadState();
        return new Dictionary<string, string>
        {
            ["target"] = Target.Selector,
            ["provider"] = "mxc-isolation-session-poc",
            ["mxcSandboxId"] = state?.SandboxId ?? string.Empty,
            ["workspace"] = state?.Workspace ?? string.Empty,
        };
    }

    private async Task<MxcState> BootstrapAsync(MxcState state, CancellationToken cancellationToken)
    {
        var source = binaryProvider.GetBinary();
        if (File.Exists(Path.ChangeExtension(source.FullName, ".dll")))
        {
            throw Failure("The MXC PoC requires a published self-contained winapp executable.",
                "Publish winapp first, then run the executable from the publish directory.");
        }
        var bootstrap = Path.Join(state.Workspace, "winapp-bootstrap");
        var results = Path.Join(state.Workspace, "winapp-results");
        Directory.CreateDirectory(bootstrap);
        Directory.CreateDirectory(results);
        var binary = Path.Join(bootstrap, "winapp.exe");
        File.Copy(source.FullName, binary);
        var skia = Path.Join(source.DirectoryName!, "libSkiaSharp.dll");
        if (File.Exists(skia))
        {
            File.Copy(skia, Path.Join(bootstrap, "libSkiaSharp.dll"));
        }
        state = state with
        {
            BinaryHash = await GuestAgentIdentity.ComputeBinaryHashAsync(source.FullName, cancellationToken).ConfigureAwait(false),
            Material = GuestBootstrapMaterial.Create(Target, new ExecutionTargetEpoch(state.Epoch), port: 0),
        };
        await File.WriteAllTextAsync(Path.Join(bootstrap, GuestBootstrapMaterial.FileName),
            state.Material.ToJson(), cancellationToken).ConfigureAwait(false);
        ProtectDirectory(bootstrap, state.UserSid);
        WriteState(state);

        var arguments = WindowsCommandLine.JoinArguments([
            "guest-agent", "--bootstrap-dir", bootstrap, "--result-dir", results,
            "--managed-root", Path.Join(state.Workspace, "winapp"), "--loopback",
        ]) ?? throw new InvalidOperationException("The guest-agent argument list is empty.");
        var script = Guard(state) + $$"""
            $env:WINAPP_CLI_TELEMETRY_OPTOUT = '1';
            $agent = Start-Process -FilePath {{PsQuote(binary)}} -ArgumentList {{PsQuote(arguments)}} -WindowStyle Hidden -PassThru -RedirectStandardOutput {{PsQuote(Path.Join(results, "stdout.txt"))}} -RedirectStandardError {{PsQuote(Path.Join(results, "stderr.txt"))}};
            $deadline = [DateTime]::UtcNow.AddSeconds(30);
            while (!(Test-Path -LiteralPath {{PsQuote(Path.Join(results, "heartbeat.json"))}})) {
                if ($agent.HasExited -or [DateTime]::UtcNow -gt $deadline) {
                    Get-Content -LiteralPath {{PsQuote(Path.Join(results, "stderr.txt"))}};
                    throw 'The MXC guest agent did not publish readiness.';
                }
                Start-Sleep -Milliseconds 100;
            };
            """;
        await ExecAsync(state, script, cancellationToken).ConfigureAwait(false);
        return state;
    }

    private async Task<TargetConnection> ConnectAsync(MxcState state, CancellationToken cancellationToken)
    {
        if (state.Material is null || state.BinaryHash is null || !Directory.Exists(state.Workspace))
        {
            throw StaleTarget("The recorded MXC session is missing or its startup did not finish.");
        }
        var currentHash = await GuestAgentIdentity.ComputeBinaryHashAsync(
            binaryProvider.GetBinary().FullName, cancellationToken).ConfigureAwait(false);
        if (currentHash != state.BinaryHash)
        {
            throw StaleTarget("The host binary changed since this MXC agent was started.");
        }
        var heartbeatPath = Path.Join(state.Workspace, "winapp-results", "heartbeat.json");
        GuestAgentHeartbeat? heartbeat;
        try
        {
            heartbeat = GuestAgentHeartbeat.TryParse(await File.ReadAllTextAsync(heartbeatPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw StaleTarget($"Cannot read the recorded MXC agent heartbeat: {ex.Message}");
        }
        if (heartbeat is null || heartbeat.TargetEpoch != state.Epoch ||
            heartbeat.BinaryHash != state.BinaryHash || heartbeat.Port is < 1 or > 65535)
        {
            throw StaleTarget("The MXC agent heartbeat does not match this target.");
        }
        if (!heartbeat.Ready)
        {
            throw Failure($"MXC's desktop is not ready: {heartbeat.NotReadyReason}.",
                "Check the Windows isolation-session configuration. The PoC does not change it automatically.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var transport = await GuestTcpTransport.ConnectAsync("127.0.0.1",
                state.Material with { Port = heartbeat.Port }, timeout.Token).ConfigureAwait(false);
            return new TargetConnection(new ExecutionTargetEpoch(state.Epoch), transport, Reused: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw StaleTarget("The recorded MXC agent did not answer. It was not replaced.");
        }
        catch (ExecutionTargetException ex)
        {
            throw StaleTarget($"The recorded MXC agent could not be reached: {ex.Error.Message}");
        }
    }

    private static string Guard(MxcState state)
    {
        using var host = Process.GetCurrentProcess();
        return $$"""
            $ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';
            if ((Get-Process -Id $PID).SessionId -in @(0, {{host.SessionId}}) -or
                [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne {{PsQuote(state.UserSid)}}) {
                throw 'Refusing to act outside the owned MXC user/session.';
            };
            """;
    }

    private Task<ProcessRunResult> ExecAsync(MxcState state, string script, CancellationToken cancellationToken, bool allowStale = false) =>
        CallAsync("exec", state, new JsonObject
        {
            ["process"] = new JsonObject
            {
                ["commandLine"] = "powershell.exe -NoProfile -NonInteractive -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                ["timeout"] = 60000,
            },
        }, cancellationToken, allowStale);

    private async Task<ProcessRunResult> CallAsync(
        string phase, MxcState? state, JsonObject? fields, CancellationToken cancellationToken, bool allowStale = false)
    {
        var request = fields ?? new JsonObject();
        request["version"] = SchemaVersion;
        request["phase"] = phase;
        if (state is not null)
        {
            request["sandboxId"] = state.SandboxId;
        }
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.ToJsonString()));
        var result = await RunAsync(["--config-base64", encoded], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && !(allowStale && IsStale(result)))
        {
            throw Failure($"MXC {phase} failed (exit {result.ExitCode}): {result.StandardOutput.Trim()} {result.StandardError.Trim()}",
                $"Check the MXC error. To discard this named session explicitly, run 'winapp target delete {Target.Selector}'.");
        }
        return result;
    }

    private async Task<ProcessRunResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var executable = Environment.GetEnvironmentVariable("WINAPP_MXC_PATH");
        if (string.IsNullOrWhiteSpace(executable))
        {
            executable = "wxc-exec.exe";
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            return await processRunner.RunAsync(new ProcessRunRequest(executable, arguments)
            {
                CloseStandardInput = true,
            }, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            throw Failure($"Could not run MXC: {ex.Message}",
                "Set WINAPP_MXC_PATH to the full path of a current wxc-exec.exe supporting schema 0.9.0-alpha.", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("The MXC operation timed out.", $"Inspect or delete the recorded target '{Target.Selector}' before retrying.");
        }
    }

    private static bool IsStale(ProcessRunResult result)
    {
        if (result.ExitCode == 0 || !result.StandardOutput.TrimStart().StartsWith('{'))
        {
            return false;
        }
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            return json.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("code", out var code) && code.GetString() == "stale_id";
        }
        catch (JsonException) { return false; } // Non-JSON process failures are reported by CallAsync.
    }

    private MxcState? ReadState()
    {
        var path = StatePath(create: false);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var state = JsonSerializer.Deserialize(File.ReadAllText(path), MxcJsonContext.Default.MxcState);
            if (state is null || state.Version != 1 || state.Selector != Target.Selector ||
                string.IsNullOrEmpty(state.SandboxId) || !state.SandboxId.StartsWith("iso:", StringComparison.Ordinal) ||
                !Guid.TryParseExact(state.Epoch, "N", out _) || !Path.IsPathFullyQualified(state.Workspace) ||
                TargetPathSafety.PathsEqual(state.Workspace, Path.GetPathRoot(state.Workspace)!))
            {
                throw Failure("The MXC ownership record is invalid.", $"Inspect '{path}' before modifying this target.");
            }
            _state = state;
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw Failure($"Cannot read MXC ownership: {ex.Message}", $"Inspect '{path}' before modifying this target.", ex);
        }
    }

    private void WriteState(MxcState state)
    {
        var path = StatePath(create: true);
        ProtectDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(state, MxcJsonContext.Default.MxcState));
        _state = state;
    }

    private string StatePath(bool create) => Path.Join(directoryProvider.GetTargetRoot(Target, create).FullName, StateFileName);

    private static void ProtectDirectory(string path, string? guestSid = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (guestSid is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(guestSid),
                FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private ExecutionTargetException StaleTarget(string message) =>
        Failure(message, $"Run 'winapp target delete {Target.Selector}' before creating a fresh session, or use a new name.");

    private static ExecutionTargetException Failure(string message, string action, Exception? exception = null) =>
        ExecutionTargetException.Create(ExecutionTargetErrorCodes.StartFailed, message, userAction: action, innerException: exception);
}

internal sealed record MxcState
{
    public int Version { get; init; } = 1;
    public required string Selector { get; init; }
    public required string SandboxId { get; init; }
    public required string Workspace { get; init; }
    public required string UserSid { get; init; }
    public required string Epoch { get; init; }
    public string? BinaryHash { get; init; }
    public GuestBootstrapMaterial? Material { get; init; }
}

[JsonSerializable(typeof(MxcState))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class MxcJsonContext : JsonSerializerContext;
