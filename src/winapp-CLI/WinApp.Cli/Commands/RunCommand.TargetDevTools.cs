// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

// A local launch reports only the connection; the source fields describe a Sandbox launch's snapshot.
internal sealed record GuestDevToolsRunInfo(
    int NodeCount, bool OverlayShown, string Comments,
    string? HostProject = null, string? SourceSnapshot = null, string? SourceHash = null);

internal partial class RunCommand
{
    public partial class Handler
    {
        internal Func<CancellationToken, Task<GuestDevToolsCapabilities?>> ReadGuestDevToolsCapabilities { get; set; } =
            cancellationToken => GuestDevTools.ReadCapabilitiesAsync(Environment.ProcessPath, cancellationToken);

        private sealed record GuestInspectorRun(
            FileInfo Project, IReadOnlyList<string> Sources, string Executable, string? Arguments, bool ShowOverlay, bool Detach,
            MsixIdentityResult? Package = null, bool Managed = true, Services.DevTools.XamlCompilerArtifacts? Compiler = null);

        private sealed record PreparedGuestInspector(string Id, GuestSourceManifest Sources);

        private async Task<PreparedGuestInspector> PrepareGuestInspectorAsync(
            PreparedTarget target, GuestInspectorRun run, GuestDevToolsCapabilities expected, DirectoryInfo payload, CancellationToken cancellationToken)
        {
            var host = guestDevToolsHost ?? throw new InvalidOperationException("The host DevTools launch owner is unavailable.");
            GuestDevTools.RequireMatchingEngines(expected, target.Capabilities);
            var id = Guid.NewGuid();
            var directory = host.Create(target.Reference, id.ToString("N"));
            var snapshot = new DirectoryInfo(Path.Combine(directory.FullName, "sources"));
            try
            {
                var sources = await GuestSourceSnapshot.CreateAsync(run.Project, run.Sources, snapshot, cancellationToken, run.Compiler);
                sources = await BindCoordinatePayloadAsync(sources, payload.FullName, cancellationToken);
                LogSourceExclusions(sources.CoordinateExclusions);
                sources = await GuestSourceSnapshot.DeployAsync(target, sources, snapshot, id, cancellationToken);
                return new(id.ToString("N"), sources);
            }
            finally
            {
                snapshot.Refresh();
                if (snapshot.Exists)
                {
                    snapshot.Delete(recursive: true);
                }
            }
        }

        private async Task<int> RunGuestInspectorAsync(
            PreparedTarget target, GuestDeployment deployment, DeploymentState state, GuestInspectorRun run,
            PreparedGuestInspector prepared, Dictionary<string, string> environment, bool json, CancellationToken cancellationToken)
        {
            var request = CreateGuestInspectorRequest(deployment, prepared.Sources,
                run.Executable, run.Arguments, run.ShowOverlay, environment, run.Package, run.Managed);
            var plan = new GuestDevToolsHostPlan(target.Epoch.Value, prepared.Sources, request,
                state.DeploymentId, state.Revision);
            var result = await guestDevToolsHost!.StartAsync(target.Reference, prepared.Id, plan, run.Detach,
                cancellationToken, ready =>
                {
                    var frame = ready.App!;
                    var selector = GuestDevToolsSelector.ForLaunch(prepared.Id);
                    var output = CreateDirectGuestResult(target.Reference, target.Capabilities.Architecture, target.Epoch.Value);
                    output.ProcessId = checked((uint)frame.Process!.ProcessId);
                    if (run.Package is { } package)
                    {
                        output.AUMID = $"{appLauncherService.ComputePackageFamilyName(package.PackageName, package.Publisher)}!{package.ApplicationId}";
                    }
                    output.AppSelector = selector;
                    output.SourceWarnings = prepared.Sources.CoordinateExclusions is { Count: > 0 } exclusions ? exclusions : null;
                    output.SourceError = prepared.Sources.CoordinateError;
                    output.DevTools = new(frame.NodeCount!.Value, frame.OverlayShown == true, "host",
                        prepared.Sources.ProjectPath, prepared.Sources.GuestRoot!, prepared.Sources.ManifestHash!);
                    if (json)
                    {
                        ansiConsole.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(output, RunCommandJsonContext.Default.RunCommandResult));
                    }
                    else
                    {
                        ansiConsole.WriteLine($"DevTools connected to guest process {frame.Process.ProcessId}.");
                        ansiConsole.WriteLine($"winapp devtools inspect --on sandbox --app {frame.Process.ProcessId}");
                    }
                });
            return result.ExitCode ?? 0;
        }

        internal static GuestExecRequest CreateGuestInspectorRequest(
            GuestDeployment deployment, GuestSourceManifest sources, string executable, string? arguments,
            bool showOverlay, IReadOnlyDictionary<string, string> environment, MsixIdentityResult? package = null, bool managed = true)
        {
            if (sources.GuestRoot is null || sources.ManifestHash is null)
            {
                throw new InvalidOperationException("The guest source snapshot has not been transferred and verified.");
            }
            List<string> command =
            [
                "guest-devtools-launch",
                "--source-root=" + sources.GuestRoot,
                "--source-hash=" + sources.ManifestHash,
            ];
            if (managed)
            {
                command.Add("--managed");
            }
            if (package is null)
            {
                command.Add("--exe=" + TargetPathSafety.CombineInsideRoot(deployment.PayloadPath, executable));
            }
            else
            {
                command.Add("--package=" + package.PackageName);
                command.Add("--publisher=" + package.Publisher);
                command.Add("--application-id=" + package.ApplicationId);
                command.Add("--expected-layout=" + deployment.LayoutPath);
            }
            if (arguments is not null)
            {
                command.Add("--args=" + arguments);
            }
            if (!showOverlay)
            {
                command.Add("--no-overlay");
            }
            return new GuestExecRequest
            {
                UseGuestWinapp = true,
                Arguments = command,
                Environment = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase),
                WorkingDirectory = deployment.PayloadPath,
                RequiresRealInput = true,
            };
        }
    }
}
