// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;
using WinApp.Cli.Telemetry;
using WinApp.Cli.Telemetry.Events;

using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

public partial class TargetRuntimeServiceTests
{
    /// <summary>
    /// A host runtime service, a real command channel, and a real guest file service over one
    /// in-memory transport, with the guest's winapp child process running the real hidden verb.
    /// </summary>
    /// <remarks>
    /// The guest child is executed in-process rather than launched, but it is the production
    /// <see cref="GuestRuntimeCommand.Handler"/> reading the plan the host actually staged, from the
    /// guest path the host actually computed. That is what makes the whole loop — staging, path
    /// resolution, install policy, verification, and the report contract — observable here.
    /// </remarks>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));
        private readonly Task _serverTask;
        private readonly RuntimeGuestProcessHostFactory _processes;
        private readonly TargetMutationLease _mutationLease;

        public Harness(
            string guestManagedRoot,
            string stateRoot,
            ExecutionTargetEpoch? epoch = null,
            string? sharedFrameworkRoot = null)
        {
            var currentEpoch = epoch ?? Epoch;

            GuestPackages = new GuestPackageState();
            _processes = new RuntimeGuestProcessHostFactory(GuestPackages, sharedFrameworkRoot);

            var pair = new LoopbackTransportPair();

            var server = new GuestCommandServer(
                pair.Guest,
                currentEpoch,
                _processes,
                new StaticGuestSessionProbe(new GuestSessionInfo(1, "WinSta0", true)),
                new GuestAgentIdentity("1.0.0", "hash", "x64", 1, 1),
                new GuestFileService(guestManagedRoot),
                guestWinapp: Path.Join(guestManagedRoot, "agent", "current", "winapp.exe"));

            _serverTask = server.RunAsync(_cancellation.Token);

            var channel = new GuestCommandChannel(pair.Host, currentEpoch);
            channel.Start();

            var directories = new FixedTargetStateDirectoryProvider(stateRoot);
            StateStore = new RuntimeProvisionStateStore(directories);

            // Runtime provisioning no longer acquires the mutation lock itself -- it trusts the
            // caller already holds it, exactly as production callers do via
            // ExecutionTargetOrchestrator.PrepareAsync(Mutating). The harness stands in for that
            // caller with a real, held lease over its own scratch lock file.
            var mutationLockPath = TestPaths.TempFile("runtime-mutation-lock", ".lock");
            var mutationStream = new FileStream(
                mutationLockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            _mutationLease = new TargetMutationLease(mutationStream, wasAbandoned: false);

            Prepared = new PreparedTarget(WindowsSandboxTarget.Default, 
                channel,
                currentEpoch,
                new ExecutionTargetCapabilities
                {
                    Architecture = "x64",
                    SupportsInteractiveDesktop = true,
                    SupportsRealInput = true,
                    SupportsScreenCapture = true,
                    CooperativeUiTurnsVersion = 1,
                    SupportsInternalSystemSetup = true,
                    PersistentStorage = false,
                    ManagedRoot = guestManagedRoot,
                },
                Reused: false,
                MutationLease: _mutationLease);

            Service = new TargetRuntimeService(StateStore, Resolver, Frameworks);
        }

        /// <summary>Payloads the host is allowed to find, keyed by package identity name.</summary>
        public ScriptedRuntimePayloadResolver Resolver { get; } = new();

        /// <summary>Shared framework layouts the host is allowed to find, keyed by framework name.</summary>
        public ScriptedRuntimeFrameworkResolver Frameworks { get; } = new();

        /// <summary>What the guest believes is registered, and what installing changes.</summary>
        public GuestPackageState GuestPackages { get; }

        /// <summary>The persisted provisioning journal.</summary>
        public RuntimeProvisionStateStore StateStore { get; }

        /// <summary>The service under test.</summary>
        public TargetRuntimeService Service { get; }

        /// <summary>The prepared target the host drives.</summary>
        public PreparedTarget Prepared { get; }

        /// <summary>How many guest winapp child processes the host started.</summary>
        public int GuestInvocations => _processes.Invocations;

        /// <summary>Roots the guest configured per-user .NET discovery for.</summary>
        public IReadOnlyList<string> ConfiguredDiscoveryRoots => _processes.ConfiguredDiscoveryRoots;

        public Task<RuntimeProvisionResult> EnsureAsync(string sourceRoot, CancellationToken cancellationToken) =>
            Service.EnsureAsync(
                Prepared,
                Target,
                new DirectoryInfo(sourceRoot),
                new DirectoryInfo(sourceRoot),
                CreateTaskContext(),
                cancellationToken);

        public RuntimeProvisionState? ReadState() => StateStore.Read(Target);

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

            await Prepared.DisposeAsync();

            // Already released by the line above, which disposes the lease it was handed as a
            // fail-safe. Repeated here because that indirection is invisible to the analyzer, and
            // because TargetMutationLease.Dispose is idempotent, so saying it twice costs nothing.
            _mutationLease.Dispose();

            _cancellation.Dispose();
        }

        private static TaskContext CreateTaskContext() =>
            new(new GroupableTask("runtime-test", null), null, new TestConsole(), NullLogger.Instance, new Lock());
    }

    /// <summary>A state directory provider rooted at a test-owned folder.</summary>
    private sealed class FixedTargetStateDirectoryProvider(string root) : ITargetStateDirectoryProvider
    {
        public DirectoryInfo GetTargetRoot(ExecutionTargetRef target, bool create)
        {
            var directory = new DirectoryInfo(TestPaths.Under(root, target.StateKey));

            if (create)
            {
                directory.Create();
            }

            return directory;
        }
    }

    /// <summary>A resolver that returns exactly the payloads a test decided exist.</summary>
    private sealed class ScriptedRuntimePayloadResolver : IRuntimePayloadResolver
    {
        /// <summary>Payloads for a declared requirement, keyed by identity name.</summary>
        public Dictionary<string, RuntimePayload> Payloads { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Extra packages one declared requirement expands into, keyed by the declared name.
        /// </summary>
        /// <remarks>
        /// Models what the real resolver does for a Windows App Runtime dependency: the manifest
        /// names only the Framework, and the runtime that satisfies it is the whole cached
        /// inventory beside it.
        /// </remarks>
        public Dictionary<string, List<RuntimePayload>> Derived { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<ResolvedRuntimePackage>> ResolveAsync(
            RuntimeRequirements requirements,
            DirectoryInfo projectRoot,
            TaskContext taskContext,
            CancellationToken cancellationToken)
        {
            var resolved = new List<ResolvedRuntimePackage>();

            foreach (var requirement in requirements.Packages)
            {
                resolved.Add(new ResolvedRuntimePackage(
                    requirement, Payloads.GetValueOrDefault(requirement.Name)));

                foreach (var sibling in Derived.GetValueOrDefault(requirement.Name) ?? [])
                {
                    resolved.Add(new ResolvedRuntimePackage(
                        new RuntimePackageRequirement
                        {
                            Name = sibling.PackageName,
                            MinVersion = sibling.Version,
                            Architecture = sibling.Architecture,
                            Publisher = sibling.Publisher,
                            Derived = true,
                        },
                        sibling));
                }
            }

            return Task.FromResult<IReadOnlyList<ResolvedRuntimePackage>>(resolved);
        }
    }

    /// <summary>A framework resolver that returns exactly the layouts a test decided exist.</summary>
    private sealed class ScriptedRuntimeFrameworkResolver : IRuntimeFrameworkResolver
    {
        public Dictionary<string, RuntimeFrameworkPayload> Layouts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<RuntimeFrameworkPayload?> ResolveAsync(
            RuntimeFrameworkRequirement requirement,
            DirectoryInfo projectRoot,
            TaskContext taskContext,
            CancellationToken cancellationToken) =>
            Task.FromResult(Layouts.GetValueOrDefault(requirement.Name));
    }

    /// <summary>
    /// The guest's package registration, where installing a payload actually changes what is
    /// registered.
    /// </summary>
    /// <remarks>
    /// A static "installed version" would let the verification pass succeed without the install
    /// having done anything, which is the exact failure this whole step exists to catch.
    /// </remarks>
    private sealed class GuestPackageState : IPackageRegistrationService
    {
        private readonly Dictionary<string, (string Version, string Architecture, string? Publisher)> _installs =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Default identity attributes a registration carries unless a test says otherwise.</summary>
        internal const string DefaultArchitecture = "x64";
        internal const string DefaultPublisher = "CN=Microsoft Corporation";

        /// <summary>Versions registered before anything is installed, at the default identity.</summary>
        public Dictionary<string, string> Present { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Registrations whose architecture or publisher a test set explicitly.</summary>
        public List<RegisteredPackageIdentity> Registrations { get; } = [];

        /// <summary>Payload paths the guest was asked to install, in order.</summary>
        public List<string> InstallPackageCalls { get; } = [];

        /// <summary>Declares that installing <paramref name="name"/> yields <paramref name="version"/>.</summary>
        public void Installs(string name, string version, string? architecture = null, string? publisher = null) =>
            _installs[name] = (version, architecture ?? DefaultArchitecture, publisher ?? DefaultPublisher);

        public Task InstallPackageAsync(
            string packagePath,
            CancellationToken cancellationToken = default) =>
            InstallPackageAsync(packagePath, forceApplicationShutdown: true, cancellationToken);

        public Task InstallPackageAsync(
            string packagePath,
            bool forceApplicationShutdown,
            CancellationToken cancellationToken = default)
        {
            InstallPackageCalls.Add(packagePath);

            foreach (var (name, identity) in _installs)
            {
                if (Path.GetFileName(packagePath).StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    Registrations.RemoveAll(existing =>
                        string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));

                    Registrations.Add(new RegisteredPackageIdentity(
                        name, identity.Version, identity.Publisher, identity.Architecture));
                }
            }

            return Task.CompletedTask;
        }

        public IReadOnlyList<RegisteredPackageIdentity> FindInstalledPackagesByName(string packageName)
        {
            var matches = new List<RegisteredPackageIdentity>();

            matches.AddRange(Registrations.Where(registration =>
                string.Equals(registration.Name, packageName, StringComparison.OrdinalIgnoreCase)));

            foreach (var (name, version) in Present)
            {
                if (string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(new RegisteredPackageIdentity(
                        name, version, DefaultPublisher, DefaultArchitecture));
                }
            }

            return matches;
        }

        public string? GetInstalledVersion(string packageName, string? architecture = null) =>
            FindInstalledPackagesByName(packageName) is [var first, ..] ? first.Version : null;

        public Task RegisterLooseLayoutAsync(string manifestPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RegisterSparseAsync(string manifestPath, string externalLocation, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> UnregisterAsync(string packageName, bool preserveAppData = true, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task UnregisterByFullNameAsync(string packageFullName, bool preserveAppData = true, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool IsPackageInstalled(string namePrefix, string? architecture = null, string? excludeNameSubstring = null) =>
            Present.Keys.Any(key => key.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase));

        public string? GetHighestInstalledVersion(string namePrefix, string? architecture = null, string? excludeNameSubstring = null) =>
            Present.FirstOrDefault(entry => entry.Key.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)).Value;

        public List<DevPackageInfo> FindDevPackages(string packageName) => [];
    }

    /// <summary>Runs the real hidden guest verb in place of launching a guest winapp process.</summary>
    private sealed class RuntimeGuestProcessHostFactory(
        IPackageRegistrationService packages,
        string? sharedFrameworkRoot) : IGuestProcessHostFactory
    {
        private int _nextProcessId = 5000;
        private int _invocations;

        public int Invocations => Volatile.Read(ref _invocations);

        /// <summary>Roots the guest verb configured per-user discovery for, in order.</summary>
        public List<string> ConfiguredDiscoveryRoots { get; } = [];

        public IGuestProcessHost Start(
            GuestExecRequest request,
            Action<GuestStreamId, ReadOnlyMemory<byte>> onOutput)
        {
            Interlocked.Increment(ref _invocations);

            var handler = new GuestRuntimeCommand.Handler(packages)
            {
                // Never writes the test machine's own user environment; the launch environment the
                // host derives from the report is what the framework tests assert on.
                ConfigureDiscovery = root => { ConfiguredDiscoveryRoots.Add(root); return true; },
            };

            if (sharedFrameworkRoot is not null)
            {
                handler.SharedFrameworkRoots = () => [sharedFrameworkRoot];
            }

            return new RuntimeGuestProcessHost(
                request, onOutput, Interlocked.Increment(ref _nextProcessId), handler);
        }
    }

    /// <summary>The in-process stand-in for one guest winapp runtime-provisioning child.</summary>
    private sealed class RuntimeGuestProcessHost : IGuestProcessHost
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RuntimeGuestProcessHost(
            GuestExecRequest request,
            Action<GuestStreamId, ReadOnlyMemory<byte>> onOutput,
            int processId,
            GuestRuntimeCommand.Handler handler)
        {
            ProcessId = processId;
            _ = RunAsync(request, onOutput, handler);
        }

        public int ProcessId { get; }

        public long StartTicksUtc { get; } = DateTime.UtcNow.Ticks;

        public Task WriteStandardInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void CloseStandardInput()
        {
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
            _exit.Task.WaitAsync(cancellationToken);

        public Task<int> StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken)
        {
            _exit.TrySetResult(-1);
            return Task.FromResult(-1);
        }

        public ValueTask DisposeAsync()
        {
            _exit.TrySetResult(-1);
            return ValueTask.CompletedTask;
        }

        private async Task RunAsync(
            GuestExecRequest request,
            Action<GuestStreamId, ReadOnlyMemory<byte>> onOutput,
            GuestRuntimeCommand.Handler handler)
        {
            try
            {
                // The real verb, parsed from the real argument vector the host sent. That covers the
                // option contract, the plan read from the path the host computed, and the report file
                // the host then fetches — none of which a hand-rolled stand-in would exercise.
                var command = new GuestRuntimeCommand();
                var parseResult = command.Parse(ReadArguments(request));

                var exitCode = await handler.InvokeAsync(parseResult, CancellationToken.None);
                _exit.TrySetResult(exitCode);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SystemException ex)
            {
                onOutput(GuestStreamId.StandardError, Encoding.UTF8.GetBytes(ex.Message));
                _exit.TrySetResult(1);
            }
        }

        private static string[] ReadArguments(GuestExecRequest request)
        {
            // The agent has already replaced the flagged request with its own winapp binary by this
            // point, which is exactly the guarantee that matters: the host never names the executable.
            StringAssert.EndsWith(
                request.Executable,
                "winapp.exe",
                "runtime provisioning must run the guest's own winapp, not a host-named binary");

            Assert.AreEqual(TargetRuntimeService.GuestVerb, request.Arguments[0]);
            Assert.AreEqual(TargetRuntimeService.GuestPlanOption, request.Arguments[1]);

            // The verb itself is consumed by the command, so only its options are parsed here.
            return [.. request.Arguments.Skip(1)];
        }
    }

    /// <summary>Captures the per-phase timings provisioning records.</summary>
    private sealed class RecordingTelemetry : ITelemetry
    {
        public List<(string EventName, uint Milliseconds)> TimeTaken { get; } = [];

        public bool IsTelemetryOn => false;

        public bool IsDiagnosticTelemetryOn { get; set; }

        public void AddSensitiveString(string name, string replaceWith)
        {
        }

        public void LogException(string action, Exception e, Guid? relatedActivityId = null)
        {
        }

        public void LogTimeTaken(string eventName, uint timeTakenMilliseconds, Guid? relatedActivityId = null) =>
            TimeTaken.Add((eventName, timeTakenMilliseconds));

        public void LogCritical(string eventName, bool isError = false, Guid? relatedActivityId = null)
        {
        }

        public void Log<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
            string eventName, LogLevel level, T data, Guid? relatedActivityId = null)
            where T : EventBase
        {
        }

        public void LogError<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
            string eventName, LogLevel level, T data, Guid? relatedActivityId = null)
            where T : EventBase
        {
        }
    }
}
