// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class RunCommand
{
    public partial class Handler
    {
        /// <summary>
        /// Logs a user-facing error, emits the error-shaped JSON envelope in <c>--json</c> mode, and
        /// returns exit code 1. Consolidates the repeated log + PrintJson + return pattern.
        /// </summary>
        private int Fail(string message, bool isJson)
        {
            logger.LogError("{UISymbol} {Message}", UiSymbols.Error, message);
            if (isJson)
            {
                PrintJson(aumid: null, processId: null, message);
            }
            return 1;
        }

        /// <summary>
        /// Collects the launch/identity options that are only valid for a packaged (MSIX) app and so
        /// must be rejected for an unpackaged one. Shared by the pre-build fast-fail in
        /// <see cref="RunProjectModeAsync"/> and the authoritative post-build gate in
        /// <see cref="RunUnpackagedProjectAsync"/> so both reject the exact same set (issue #676).
        /// </summary>
        private static List<string> CollectUnpackagedIncompatibleOptions(
            bool noLaunch, bool withAlias, bool unregisterOnExit, bool clean, FileInfo? manifest, DirectoryInfo? outputAppXDirectory, string? executable)
        {
            var rejected = new List<string>();
            if (noLaunch)
            {
                rejected.Add("--no-launch");
            }
            if (withAlias)
            {
                rejected.Add("--with-alias");
            }
            if (unregisterOnExit)
            {
                rejected.Add("--unregister-on-exit");
            }
            if (clean)
            {
                rejected.Add("--clean");
            }
            if (manifest != null)
            {
                rejected.Add("--manifest");
            }
            if (outputAppXDirectory != null)
            {
                rejected.Add("--output-appx-directory");
            }
            if (!string.IsNullOrWhiteSpace(executable))
            {
                // --executable selects an entry within an MSIX layout; unusable for an unpackaged app.
                rejected.Add("--executable");
            }
            return rejected;
        }

        /// <summary>Builds the user-facing message listing the packaged-only options rejected for an unpackaged app.</summary>
        private static string BuildUnpackagedIncompatibleMessage(IReadOnlyList<string> rejected, string csprojName)
            => $"The option(s) {string.Join(", ", rejected)} don't apply to unpackaged apps — they're only valid for packaged (MSIX) apps. " +
               $"'{csprojName}' resolves to an unpackaged WinUI app (WindowsPackageType=None). Remove them, or make the app packaged to use them.";

        /// <summary>
        /// Project-mode entry point: build the <c>.csproj</c>, resolve its MSBuild
        /// output properties, then launch it as packaged (loose-layout register + AUMID, reusing the
        /// shared folder pipeline) or unpackaged (launch the apphost <c>.exe</c> directly). Folder
        /// mode never reaches here.
        /// </summary>
        private async Task<int> RunProjectModeAsync(
            ParseResult parseResult,
            FileInfo csproj,
            FileInfo? solution,
            string? selectionReason,
            string? appArgs,
            bool isJson,
            CancellationToken cancellationToken)
        {
            // Project-mode build inputs.
            var configuration = parseResult.GetValue(ConfigurationOption) ?? "Debug";
            var archOption = parseResult.GetValue(ArchOption);
            var runtimeOption = parseResult.GetValue(RuntimeOption);
            var noBuild = parseResult.GetValue(NoBuildOption);
            var noRestore = parseResult.GetValue(NoRestoreOption);
            var properties = parseResult.GetValue(PropertyOption) ?? [];

            // Resolve the explicit effective framework ONCE (--framework > bare -p:TargetFramework) so the
            // build and the classification pass (BuildClassificationInputs) share the SAME TFM and never
            // evaluate a different one for a multi-targeted project. The lower-precedence multi-target
            // first-TFM auto-pin still happens in build resolution when this is null.
            var framework = ProjectRunService.ResolveExplicitFramework(parseResult.GetValue(FrameworkOption), properties);

            // Reject malformed -p values early so they never become a nonsensical MSBuild argument.
            foreach (var property in properties)
            {
                // MSBuild splits a -p token on ';' into MULTIPLE properties, which would smuggle a
                // dedicated-flag property (e.g. RuntimeIdentifier) past the name-only ForwardableProperties
                // filter and override the arch winapp conveys via the RID. Reject packing; '%3B' escapes a
                // literal ';' in a value.
                if (property.Contains(';'))
                {
                    // Show the name only — the value may hold a secret.
                    var name = property[..property.IndexOfAny(['=', ';'])];
                    return Fail(
                        $"Invalid --property '{name}'. A single -p cannot pack multiple properties with ';'. " +
                        "Pass one property per repeatable -p (for example: -p A=1 -p B=2), or escape a literal ';' in a value as '%3B'.",
                        isJson);
                }

                var separator = property.IndexOf('=');
                if (separator <= 0 || string.IsNullOrWhiteSpace(property[..separator]))
                {
                    // Show the name only — the value may hold a secret.
                    var shown = separator > 0 ? property[..separator] : (separator == 0 ? "(empty)" : property);
                    return Fail($"Invalid --property '{shown}'. Expected Name=Value (for example: -p WindowsPackageType=None).", isJson);
                }
            }

            // Shared launch/identity options (validity depends on packaging, checked below).
            var noLaunch = parseResult.GetValue(NoLaunchOption);
            var withAlias = parseResult.GetValue(WithAliasOption);
            var debugOutput = parseResult.GetValue(DebugOutputOption);
            var unregisterOnExit = parseResult.GetValue(UnregisterOnExitOption);
            var detach = parseResult.GetValue(DetachOption);
            var clean = parseResult.GetValue(CleanOption);
            var useSymbols = parseResult.GetValue(SymbolsOption);
            var executable = parseResult.GetValue(ExecutableOption);
            var manifest = parseResult.GetValue(ManifestOption);
            var outputAppXDirectory = parseResult.GetValue(OutputAppXDirectoryOption);

            // Resolve the target architecture: --runtime's arch beats --arch; else the process arch.
            if (!TryResolveArchitecture(archOption, runtimeOption, out var architecture, out var archError))
            {
                return Fail(archError!, isJson);
            }

            // Immediate, persistent context line (UX): the pre-build steps below each spawn dotnet and can
            // take several silent seconds. Print WHAT we're about to run — and, when the input was
            // ambiguous, WHY this project was chosen — so the run never looks hung. Suppressed for --json
            // (stdout must stay pure) and --quiet (Information off).
            if (!isJson && logger.IsEnabled(LogLevel.Information))
            {
                var context = new StringBuilder($"{csproj.Name}  ·  {configuration} | {architecture}");
                if (solution != null)
                {
                    context.Append($"  ·  {solution.Name}");
                }

                if (!string.IsNullOrWhiteSpace(selectionReason))
                {
                    context.Append($" ({selectionReason})");
                }

                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Search} {context}");
            }

            // A capable SDK (≥ 8.0.100) is required for MSBuild --getProperty.
            var workingDir = csproj.Directory ?? new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());
            var sdkError = await projectRunService.CheckSdkAsync(workingDir, cancellationToken);
            if (sdkError != null)
            {
                return Fail(sdkError, isJson);
            }

            // Build (unless --no-build) and resolve the output properties. ProjectRunService owns the build
            // UX: it streams dotnet's output live and prints the exact invocation; in --json/--quiet mode it
            // routes both to stderr to keep stdout pure.
            var buildOptions = new ProjectRunOptions(configuration, architecture, framework, noBuild, noRestore, properties, isJson, solution);

            // Fail fast (issue #676): identity-only options like --no-launch are meaningless for an
            // unpackaged app but are only rejected authoritatively AFTER packaging is known (post-build).
            // Cheaply evaluate WindowsPackageType first and reject now when the project is DEFINITIVELY
            // unpackaged, so the user doesn't pay the full build cost only to be rejected. Skipped under
            // --no-build (no build cost to save).
            if (!noBuild)
            {
                var incompatible = CollectUnpackagedIncompatibleOptions(noLaunch, withAlias, unregisterOnExit, clean, manifest, outputAppXDirectory, executable);
                if (incompatible.Count > 0
                    && await projectRunService.IsDefinitivelyUnpackagedAsync(csproj, buildOptions, cancellationToken))
                {
                    return Fail(BuildUnpackagedIncompatibleMessage(incompatible, csproj.Name), isJson);
                }
            }

            ProjectBuildOutcome outcome;
            try
            {
                outcome = await projectRunService.BuildAndResolveAsync(csproj, buildOptions, cancellationToken);
            }
            catch (ProjectRunException ex)
            {
                return Fail(ex.Message, isJson);
            }

            if (outcome.Resolution is null)
            {
                // Build failed — dotnet already surfaced its diagnostics. Propagate its exit code.
                var code = outcome.ExitCode == 0 ? 1 : outcome.ExitCode;
                if (isJson)
                {
                    PrintJson(aumid: null, processId: null, $"Build failed (exit code {code}).");
                }
                return code;
            }

            var resolution = outcome.Resolution;

            return resolution.Packaging == ProjectPackaging.Packaged
                ? await RunPackagedProjectAsync(
                    resolution, csproj, manifest, outputAppXDirectory, appArgs,
                    noLaunch, withAlias, debugOutput, unregisterOnExit, detach, clean, useSymbols, executable, noBuild, isJson,
                    cancellationToken)
                : await RunUnpackagedProjectAsync(
                    resolution, csproj, appArgs,
                    noLaunch, withAlias, debugOutput, unregisterOnExit, detach, clean, useSymbols, executable, manifest, outputAppXDirectory, isJson,
                    cancellationToken);
        }

        /// <summary>
        /// Packaged project-mode launch: point the shared folder pipeline at the build's
        /// TargetDir (which contains the MSBuild-generated <c>AppxManifest.xml</c> + recipe) and pass
        /// the resolved arch + project file so the correct-arch Windows App Runtime is installed.
        /// </summary>
        private async Task<int> RunPackagedProjectAsync(
            ProjectRunResolution resolution,
            FileInfo csproj,
            FileInfo? manifest,
            DirectoryInfo? outputAppXDirectory,
            string? appArgs,
            bool noLaunch,
            bool withAlias,
            bool debugOutput,
            bool unregisterOnExit,
            bool detach,
            bool clean,
            bool useSymbols,
            string? executable,
            bool noBuild,
            bool isJson,
            CancellationToken cancellationToken)
        {
            var targetDir = new DirectoryInfo(resolution.TargetDir);

            // Guardrail: packaged (per the evaluated WindowsPackageType) but no manifest in the
            // build output is a misconfiguration — surface it clearly instead of the generic
            // "manifest not found" from the shared pipeline (which would also probe the cwd).
            if (manifest == null && !FindManifest(targetDir.FullName).Exists)
            {
                // Under --no-build the missing manifest most often means --no-build is pointing at a stale
                // or unpackaged build output, not that the project is misconfigured — lead with that.
                var message = noBuild
                    ? $"'{csproj.Name}' resolves to a packaged (MSIX) app but no AppxManifest.xml was found in the build output ({targetDir.FullName}). " +
                      "Remove --no-build to rebuild the packaged layout, or point the run at an up-to-date packaged build."
                    : $"'{csproj.Name}' resolves to a packaged (MSIX) app but no AppxManifest.xml was found in the build output ({targetDir.FullName}). " +
                      "Ensure the project is a packaged WinUI app (EnableMsixTooling=true with a Package.appxmanifest), or force an unpackaged run with -p:WindowsPackageType=None.";
                return Fail(message, isJson);
            }

            return await ExecuteRunPipelineAsync(
                targetDir, manifest, outputAppXDirectory, appArgs,
                noLaunch, withAlias, debugOutput, unregisterOnExit, detach, clean, useSymbols, executable, isJson,
                runtimeArch: resolution.Architecture, projectFile: csproj, framework: resolution.Framework, noRestore: resolution.NoRestore, cancellationToken);
        }

        /// <summary>
        /// Unpackaged project-mode launch: ensure the (framework-dependent) Windows App
        /// Runtime is installed for the app's arch, then launch the apphost <c>.exe</c> directly.
        /// Identity-only options are rejected since there is no MSIX package.
        /// </summary>
        private async Task<int> RunUnpackagedProjectAsync(
            ProjectRunResolution resolution,
            FileInfo csproj,
            string? appArgs,
            bool noLaunch,
            bool withAlias,
            bool debugOutput,
            bool unregisterOnExit,
            bool detach,
            bool clean,
            bool useSymbols,
            string? executable,
            FileInfo? manifest,
            DirectoryInfo? outputAppXDirectory,
            bool isJson,
            CancellationToken cancellationToken)
        {
            // AUTHORITATIVE gate — rejects packaged-only options once packaging is definitively known.
            // RunProjectModeAsync fails fast on the definitively-unpackaged case before building (issue
            // #676); this still catches the indeterminate-then-unpackaged case that only resolves here.
            var rejected = CollectUnpackagedIncompatibleOptions(noLaunch, withAlias, unregisterOnExit, clean, manifest, outputAppXDirectory, executable);
            if (rejected.Count > 0)
            {
                return Fail(BuildUnpackagedIncompatibleMessage(rejected, csproj.Name), isJson);
            }

            var exePath = resolution.RunCommand!; // guaranteed non-null for unpackaged by BuildAndResolveAsync

            // A non-apphost RunCommand (e.g. dotnet) carries its own leading args (exec "<app>.dll") that
            // must precede the user's app args.
            var launchArgs = CombineLaunchArguments(resolution.RunArguments, appArgs);

            // Launch with the caller's current directory (like `dotnet run`), NOT the build-output
            // directory, so apps that resolve config/data files relative to the working directory behave
            // the same. Assembly/resource lookup is unaffected (the apphost resolves those from its own
            // location).
            var workingDirectory = currentDirectoryProvider.GetCurrentDirectory();

            // Install the framework-dependent Windows App Runtime (Framework + DDLM) the app's
            // bootstrapper needs, for the resolved arch. Self-contained apps carry their own copy.
            if (!resolution.SelfContained)
            {
                string? runtimeErrorMessage = null;
                var runtimeResult = await statusService.ExecuteWithStatusAsync(
                    "Preparing Windows App Runtime...",
                    async (taskContext, ct) =>
                    {
                        try
                        {
                            // Report whether the runtime was actually prepared: a plain console/desktop app
                            // with no Windows App SDK reference is skipped inside, so claiming "ready" would
                            // be a lie. Surface the skip honestly instead.
                            var prepared = await msixService.EnsureWindowsAppRuntimeInstalledAsync(csproj, resolution.Architecture, resolution.Framework, resolution.NoRestore, taskContext, ct);
                            return (0, prepared ? "Windows App Runtime ready" : "No Windows App SDK reference — runtime not needed");
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            // Honor Ctrl+C instead of translating it into a runtime-prep failure message.
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // Capture the actionable detail so --json can surface it too (the status
                            // service consumes the tuple message for the console line only).
                            runtimeErrorMessage = ex.Message;
                            return (1, $"{UiSymbols.Error} Failed to prepare the Windows App Runtime: {ex.Message}");
                        }
                    },
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    // Ctrl+C during runtime prep — exit as cancelled (matching the launched-process wait)
                    // rather than reporting a genuine "Failed to prepare the Windows App Runtime" error.
                    return -1;
                }

                if (runtimeResult != 0)
                {
                    if (isJson)
                    {
                        var jsonError = string.IsNullOrWhiteSpace(runtimeErrorMessage)
                            ? "Failed to prepare the Windows App Runtime."
                            : $"Failed to prepare the Windows App Runtime: {runtimeErrorMessage}";
                        PrintJson(aumid: null, processId: null, jsonError);
                    }
                    return runtimeResult;
                }
            }

            ILaunchedProcess launched;
            try
            {
                // For --detach and --json the child must not inherit winapp's standard handles: inheritance
                // would keep the npm wrapper's captured stdout pipe open (blocking a detached launch) and let
                // app output corrupt --json stdout. A foreground, non-JSON run streams inline like `dotnet run`.
                var stdioMode = (detach || isJson) ? LaunchStdioMode.Suppress : LaunchStdioMode.Inherit;
                launched = appLauncherService.LaunchExecutable(exePath, launchArgs, workingDirectory, stdioMode);
            }
            catch (Exception ex)
            {
                // A cross-arch apphost (e.g. an arm64 build on an x64 host) fails here with an opaque
                // Win32 "not a valid application" error. If the resolved arch can't run on this machine,
                // enrich the message with actionable guidance instead of surfacing the raw OS error.
                var detail = resolution.Architecture is { Length: > 0 } arch && !CanCurrentOsRunArchitecture(arch)
                    ? BuildArchMismatchMessage(arch, ex.Message)
                    : ex.Message;
                logger.LogError("{UISymbol} Failed to launch '{Exe}': {Message}", UiSymbols.Error, exePath, detail);
                if (isJson)
                {
                    PrintJson(aumid: null, processId: null, detail);
                }
                return 1;
            }

            // Own the handle for the lifetime of the wait so the exit code survives the process exiting
            // and the PID can't be reused. Disposing doesn't kill the OS process, so --detach can return
            // while the app keeps running.
            using (launched)
            {
                var processId = launched.ProcessId;

                // --detach: return immediately, surfacing the PID for automation.
                if (detach)
                {
                    if (isJson)
                    {
                        PrintJson(aumid: null, processId, errorMessage: null);
                    }
                    else
                    {
                        ansiConsole.WriteLine(processId.ToString());
                    }
                    return 0;
                }

                if (isJson)
                {
                    PrintJson(aumid: null, processId, errorMessage: null);
                }
                else if (logger.IsEnabled(LogLevel.Information))
                {
                    // Confirm the launch so a windowed app (which streams no console output) doesn't look
                    // stuck while winapp waits for it to exit. Mirrors packaged mode's "launched (PID)" line.
                    // Gated on Information so --quiet stays silent.
                    ansiConsole.MarkupLineInterpolated(
                        $"{UiSymbols.Check} Launched {Path.GetFileNameWithoutExtension(csproj.Name)} (PID: {processId})");
                }

                // --debug-output: attach the debug event loop instead of a plain wait.
                if (debugOutput)
                {
                    var debugExit = await debugOutputService.RunDebugLoopAsync(processId, cancellationToken, useSymbols,
                        symbolSearchPaths: [resolution.TargetDir]);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        launched.Kill();
                    }
                    return debugExit;
                }

                return await WaitForLaunchedProcessAsync(launched, cancellationToken);
            }
        }

        /// <summary>
        /// True when the current OS can execute a process of <paramref name="targetArch"/>, accounting for
        /// Windows emulation: an arm64 host runs arm64/x64/x86; an x64 host runs x64/x86 but NOT arm64; an
        /// x86 host runs x86 only. Unknown monikers are treated as runnable so a genuine launch error still
        /// surfaces normally rather than being masked by a false "wrong architecture" message.
        /// </summary>
        internal static bool CanCurrentOsRunArchitecture(string targetArch)
        {
            var os = RuntimeInformation.OSArchitecture;
            return targetArch.ToLowerInvariant() switch
            {
                "arm64" => os == Architecture.Arm64,
                "x64" => os is Architecture.X64 or Architecture.Arm64,
                "x86" => os is Architecture.X86 or Architecture.X64 or Architecture.Arm64,
                _ => true,
            };
        }

        /// <summary>
        /// Builds an actionable message for an app built for an architecture this machine can't execute,
        /// replacing the opaque OS "InvalidApplication" error. <paramref name="detail"/> carries the raw
        /// OS error for diagnostics.
        /// </summary>
        private static string BuildArchMismatchMessage(string targetArch, string detail)
        {
            var host = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
            return $"The app was built for {targetArch} but this machine runs {host}, which can't execute {targetArch} binaries. " +
                   $"Rebuild for {host} (for example: --arch {host}) or run the app on an {targetArch} machine. ({detail})";
        }

        /// <summary>
        /// Prepends a non-apphost RunCommand's own launch args (e.g. <c>exec "&lt;app&gt;.dll"</c>) before the
        /// user's app args, so both reach the launcher in the right order. Either side may be null/empty.
        /// </summary>
        internal static string? CombineLaunchArguments(string? runArguments, string? appArgs)
        {
            var parts = new[] { runArguments, appArgs }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            var combined = string.Join(" ", parts);
            return string.IsNullOrWhiteSpace(combined) ? null : combined;
        }

        /// <summary>
        /// Waits for a directly-launched (unpackaged) process to exit and returns its exit code,
        /// mirroring the folder-mode wait semantics (already-exited returns the real code and Ctrl+C
        /// kills the child). The owned handle keeps the exit code valid even if the process exited
        /// before the wait began, so we never misreport a crash as success.
        /// </summary>
        private static async Task<int> WaitForLaunchedProcessAsync(ILaunchedProcess launched, CancellationToken cancellationToken)
        {
            try
            {
                await launched.WaitForExitAsync(cancellationToken);
                return launched.ExitCode;
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C — kill the launched process before exiting.
                launched.Kill();
                return -1;
            }
        }

        /// <summary>
        /// Computes the effective build inputs threaded into candidate classification so a multi-
        /// <c>.csproj</c> directory or solution is resolved under the SAME Configuration/arch/TFM/user
        /// <c>-p</c> the build will use. Returns null when the architecture can't be resolved
        /// (classification then uses defaults and <see cref="RunProjectModeAsync"/> surfaces the arch
        /// error). No-op for folder mode, so evaluating it before mode is known is harmless.
        /// </summary>
        internal static ProjectClassificationInputs? BuildClassificationInputs(ParseResult parseResult)
        {
            var archOption = parseResult.GetValue(ArchOption);
            var runtimeOption = parseResult.GetValue(RuntimeOption);
            if (!TryResolveArchitecture(archOption, runtimeOption, out var architecture, out _))
            {
                return null;
            }

            var configuration = parseResult.GetValue(ConfigurationOption) ?? "Debug";
            var properties = parseResult.GetValue(PropertyOption) ?? [];
            // Share the SAME explicit effective framework the build uses (--framework > bare
            // -p:TargetFramework) so classification and build never evaluate a different TFM.
            var framework = ProjectRunService.ResolveExplicitFramework(parseResult.GetValue(FrameworkOption), properties);
            return new ProjectClassificationInputs(configuration, architecture, framework, properties);
        }

        /// <summary>
        /// Resolves the canonical target architecture from <c>--arch</c> / <c>--runtime</c>.
        /// <c>--runtime</c>'s architecture wins over <c>--arch</c> (mirrors dotnet, where a RID is
        /// more specific); when neither is given, the current process architecture is used.
        /// </summary>
        internal static bool TryResolveArchitecture(string? archOption, string? runtimeOption, out string architecture, out string? error)
            => RunArchHelper.TryResolveArchitecture(archOption, runtimeOption, out architecture, out error);
    }
}
