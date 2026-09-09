// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

/// <summary>
/// Installs staged shared runtimes and verifies the required package graph, inside an execution
/// target (spec §"Runtime provisioning" steps 5 and 6).
/// </summary>
/// <remarks>
/// Hidden, like the guest agent verb, because it is an internal step of a host-driven workflow
/// rather than something to run by hand: its input is a plan the host staged through the verified
/// file channel, and its output is a machine-readable report only the host consumes.
/// <para>
/// It exists as an ordinary winapp command rather than as an agent operation on purpose. The agent
/// implements no application semantics — every operation it performs is a guest winapp child
/// process — so package installation belongs here, where it reuses the same
/// <see cref="IPackageRegistrationService"/> a local run uses and cannot drift from it.
/// </para>
/// <para>
/// Nothing is ever removed or downgraded. A package already registered at or above the required
/// version is left exactly as it is, and a shared framework already present is not unpacked over,
/// which is what makes provisioning safe in a guest several applications share.
/// </para>
/// </remarks>
internal class GuestRuntimeCommand : Command, IShortDescription
{
    /// <inheritdoc/>
    public string ShortDescription => "Install and verify staged shared runtimes in an execution target";

    /// <summary>The staged plan describing the complete required runtime graph.</summary>
    public static Option<string?> PlanOption { get; } = new(TargetRuntimeService.GuestPlanOption)
    {
        Description = "Staged runtime provisioning plan to apply and verify.",
    };

    /// <summary>Creates the hidden runtime provisioning verb.</summary>
    public GuestRuntimeCommand()
        : base(
            TargetRuntimeService.GuestVerb,
            "Install and verify staged shared runtimes for an execution target. Internal; not part of the public CLI.")
    {
        // Hidden, like the guest agent verb: it is an internal step of a host-driven workflow, not
        // something to run by hand, so it stays out of help, completions, and the published schema.
        Hidden = true;

        Options.Add(PlanOption);
    }

    /// <summary>Applies a staged plan and reports the resulting graph.</summary>
    public class Handler(IPackageRegistrationService packageRegistrationService) : AsynchronousCommandLineAction
    {
        /// <summary>
        /// Shared .NET roots probed for an installed runtime, besides the managed one.
        /// </summary>
        /// <remarks>
        /// Every standard location, not just the default one. A false "not installed" would install
        /// a runtime the guest already had, and a false "installed" would launch an application that
        /// cannot start — so the probe looks wherever an installation plausibly is.
        /// </remarks>
        internal Func<IEnumerable<string>> SharedFrameworkRoots { get; set; } = DotNetLayout.DefaultRoots;

        /// <summary>
        /// Makes the managed .NET root discoverable to processes winapp did not start.
        /// </summary>
        /// <remarks>
        /// Seamed because it writes the guest user's environment, which a test must not do to the
        /// machine running it.
        /// </remarks>
        internal Func<string, bool> ConfigureDiscovery { get; set; } = DotNetRuntimeInstaller.TryConfigureDiscovery;

        /// <inheritdoc/>
        public override async Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            var planPath = parseResult.GetValue(PlanOption);
            if (string.IsNullOrWhiteSpace(planPath) || !File.Exists(planPath))
            {
                parseResult.InvocationConfiguration.Error.WriteLine(
                    "winapp guest-runtime is started by winapp inside an execution target and cannot be run directly.");
                return 1;
            }

            RuntimeProvisionPlan? plan;
            try
            {
                await using var stream = File.OpenRead(planPath);
                plan = await JsonSerializer
                    .DeserializeAsync(stream, RuntimeProvisionJsonContext.Default.RuntimeProvisionPlan, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                parseResult.InvocationConfiguration.Error.WriteLine(
                    $"The staged runtime plan could not be read: {ex.Message}");
                return 1;
            }

            if (plan is null || plan.SchemaVersion != RuntimeProvisionPlan.CurrentSchemaVersion)
            {
                parseResult.InvocationConfiguration.Error.WriteLine(
                    "The staged runtime plan is not a version this winapp understands.");
                return 1;
            }

            var stagingDirectory = Path.GetDirectoryName(Path.GetFullPath(planPath))!;
            var report = await ApplyAsync(plan, stagingDirectory, cancellationToken).ConfigureAwait(false);

            // Published as a file beside the plan, because that is what the host reads: the verified
            // file channel has no ordering relationship with process exit, so a verdict written here
            // cannot be truncated by the process finishing first.
            try
            {
                await File.WriteAllTextAsync(
                    Path.Join(stagingDirectory, RuntimeProvisionReport.FileName),
                    report.ToJson(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                parseResult.InvocationConfiguration.Error.WriteLine(
                    $"The runtime provisioning report could not be written: {ex.Message}");
                return 1;
            }

            // Also on standard output, so the verb is readable when run by hand during diagnosis.
            parseResult.InvocationConfiguration.Output.WriteLine(report.ToJson());

            return report.Satisfied ? 0 : 1;
        }

        /// <summary>Installs what is missing, then reports the state of the whole graph.</summary>
        internal async Task<RuntimeProvisionReport> ApplyAsync(
            RuntimeProvisionPlan plan,
            string stagingDirectory,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(plan);

            var items = new List<RuntimeItemStatus>();
            var installWatch = Stopwatch.StartNew();

            foreach (var package in plan.Packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(await EnsurePackageAsync(package, stagingDirectory, cancellationToken).ConfigureAwait(false));
            }

            var frameworks = EnsureFrameworks(plan, stagingDirectory, cancellationToken);

            installWatch.Stop();

            var verifyWatch = Stopwatch.StartNew();

            // Re-read every framework package after the installs rather than trusting what each
            // install reported: the graph is what matters, and a package that installed successfully
            // but was then superseded or removed by something else in a shared guest would otherwise
            // still be counted as present.
            for (var index = 0; index < plan.Packages.Count; index++)
            {
                var package = plan.Packages[index];
                var previous = items[index];
                var present = FindInstalledVersion(package);
                var satisfied = present is not null;

                items[index] = new RuntimeItemStatus
                {
                    Name = previous.Name,
                    RequiredVersion = previous.RequiredVersion,
                    PresentVersion = present,
                    Installed = previous.Installed,
                    Satisfied = satisfied,
                    Detail = satisfied ? null : previous.Detail ?? DescribeRegistrations(package),
                };
            }

            foreach (var framework in frameworks)
            {
                items.Add(framework.Status);
            }

            verifyWatch.Stop();

            return new RuntimeProvisionReport
            {
                PlanId = plan.PlanId,
                Satisfied = items.TrueForAll(item => item.Satisfied),
                Items = items,

                // Reported only when the managed root is what actually satisfies something. A guest
                // whose own installation covers every framework must not have its launches pinned to
                // a root winapp created and left empty.
                DotNetRoot = frameworks.Any(framework => framework.UsesManagedRoot) ? plan.DotNetRoot : null,
                InstallMilliseconds = installWatch.ElapsedMilliseconds,
                VerifyMilliseconds = verifyWatch.ElapsedMilliseconds,
            };
        }

        /// <summary>One shared framework's outcome, and whether the managed root is what supplies it.</summary>
        private sealed record FrameworkOutcome(RuntimeItemStatus Status, bool UsesManagedRoot);

        /// <summary>
        /// Installs and verifies every shared .NET framework the plan requires.
        /// </summary>
        /// <remarks>
        /// All from one root, or none from it. <c>DOTNET_ROOT</c> is exclusive: an apphost pointed at
        /// a root resolves every framework from <em>there</em> and consults nothing else. So a guest
        /// that already has the core runtime but needs the desktop one cannot be served by installing
        /// only what is missing — pinning the launch to the managed root would then hide the core
        /// runtime the guest did have. When the managed root is needed at all, everything goes into
        /// it, and the whole graph is verified there.
        /// <para>
        /// Verification is a second, independent probe rather than a reading of what the install
        /// reported. The guest is shared, and the question that matters at launch is whether the
        /// framework is resolvable now.
        /// </para>
        /// </remarks>
        private List<FrameworkOutcome> EnsureFrameworks(
            RuntimeProvisionPlan plan,
            string stagingDirectory,
            CancellationToken cancellationToken)
        {
            var outcomes = new List<FrameworkOutcome>();

            if (plan.Frameworks.Count == 0)
            {
                return outcomes;
            }

            var guestRoots = SharedFrameworkRoots()
                .Where(root => !string.Equals(root, plan.DotNetRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // The guest's own installation is preferred whole. Only when it cannot serve every
            // framework does the managed root come into play — and then it has to serve all of them.
            var completeGuestRoot = guestRoots.FirstOrDefault(root =>
                plan.Frameworks.All(framework =>
                    DotNetRuntimeInstaller.FindSatisfying(framework, [root]) is not null));

            var useManagedRoot = completeGuestRoot is null;
            var probeRoots = useManagedRoot ? (List<string>)[plan.DotNetRoot] : [completeGuestRoot!];

            foreach (var framework in plan.Frameworks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var outcome = useManagedRoot
                    ? DotNetRuntimeInstaller.Ensure(
                        framework, plan.DotNetRoot, stagingDirectory, probeRoots, cancellationToken)
                    : new DotNetInstallOutcome(Installed: false, PresentVersion: null, Detail: null);

                var present = DotNetRuntimeInstaller.FindSatisfying(framework, probeRoots);

                outcomes.Add(new FrameworkOutcome(
                    new RuntimeItemStatus
                    {
                        Name = framework.Name,
                        RequiredVersion = framework.MinVersion,
                        PresentVersion = present?.ToString(),
                        Installed = outcome.Installed,
                        Satisfied = present is not null,
                        Detail = present is not null
                            ? null
                            : outcome.Detail ?? "no compatible version of that shared framework is installed",
                    },
                    useManagedRoot && present is not null));
            }

            // Only once the managed root really does serve the whole graph. Recording it per-user
            // before that would point every hand-started app in the guest at a root that cannot
            // resolve what the machine-wide installation could — breaking something that worked to
            // help something that did not.
            if (useManagedRoot && outcomes.TrueForAll(outcome => outcome.Status.Satisfied))
            {
                // Best-effort, and never on the critical path: the launch carries the same value in
                // the child's own environment. This only extends it to processes winapp did not
                // start, such as an app run by hand through `sandbox exec`.
                ConfigureDiscovery(plan.DotNetRoot);
            }

            return outcomes;
        }

        /// <summary>
        /// Installs one framework package if, and only if, nothing sufficient is already registered.
        /// </summary>
        private async Task<RuntimeItemStatus> EnsurePackageAsync(
            RuntimePackageRequirement package,
            string stagingDirectory,
            CancellationToken cancellationToken)
        {
            var present = FindInstalledVersion(package);

            if (present is not null)
            {
                // Already good enough. Installing over it would at best be wasted time and at worst
                // restart an application in this guest that is using it.
                return Status(package, present, installed: false, satisfied: true, detail: null);
            }

            if (package.PayloadFile is not { } payloadFile)
            {
                return Status(package, present, installed: false, satisfied: false, detail: null);
            }

            string payloadPath;
            try
            {
                payloadPath = TargetPathSafety.CombineInsideRoot(stagingDirectory, payloadFile);
            }
            catch (ExecutionTargetException ex)
            {
                return Status(package, present, installed: false, satisfied: false, detail: ex.Message);
            }

            if (!File.Exists(payloadPath))
            {
                return Status(
                    package, present, installed: false, satisfied: false,
                    detail: "the staged payload is missing");
            }

            try
            {
                await packageRegistrationService
                    .InstallPackageAsync(
                        payloadPath,
                        forceApplicationShutdown: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Recorded rather than thrown: the verification pass decides whether the graph is
                // usable, and a failed install of something another package already satisfies is not
                // by itself a reason to refuse the launch.
                return Status(package, present, installed: false, satisfied: false, detail: ex.Message);
            }

            // Re-read rather than assume: an install that reported success but did not raise the
            // registered version is a real outcome, and the report has to say so.
            var afterInstall = FindInstalledVersion(package);

            return Status(
                package,
                afterInstall,
                installed: true,
                satisfied: afterInstall is not null,
                detail: null);
        }

        /// <summary>
        /// Finds a registered package that genuinely satisfies a requirement, or null.
        /// </summary>
        /// <remarks>
        /// Name, architecture, publisher, and version are all checked against the same registration,
        /// which is what a version-only lookup with an unfiltered architecture fallback cannot do:
        /// there, an x86 package registered under the same name silently satisfies an x64
        /// dependency, and the failure surfaces later as a registration or startup error naming
        /// something the report said was present.
        /// </remarks>
        private string? FindInstalledVersion(RuntimePackageRequirement package) =>
            packageRegistrationService.FindInstalledPackagesByName(package.Name)
                .Where(candidate =>
                    package.AcceptsArchitecture(candidate.Architecture)
                    && package.AcceptsPublisher(candidate.Publisher)
                    && RuntimeRequirementDiscovery.ComparableVersion(candidate.Version)
                        >= RuntimeRequirementDiscovery.ComparableVersion(package.MinVersion))
                .OrderByDescending(candidate => RuntimeRequirementDiscovery.ComparableVersion(candidate.Version))
                .FirstOrDefault()
                ?.Version;

        private static RuntimeItemStatus Status(
            RuntimePackageRequirement package,
            string? present,
            bool installed,
            bool satisfied,
            string? detail) =>
            new()
            {
                Name = package.Name,
                RequiredVersion = package.MinVersion,
                PresentVersion = present,
                Installed = installed,
                Satisfied = satisfied,
                Detail = satisfied ? null : detail,
            };

        /// <summary>
        /// Says what <em>is</em> registered under a required name, when nothing satisfying is.
        /// </summary>
        /// <remarks>
        /// "Not installed" and "installed, but x86 where x64 was needed" are very different problems
        /// with very different fixes, and the second is invisible unless the report distinguishes
        /// them. Naming the near-misses is what turns a dead end into something actionable.
        /// </remarks>
        private string DescribeRegistrations(RuntimePackageRequirement package)
        {
            var registered = packageRegistrationService.FindInstalledPackagesByName(package.Name);

            if (registered.Count == 0)
            {
                return $"no package of that name is registered and no payload was available";
            }

            var summary = string.Join(
                ", ",
                registered.Select(candidate => $"{candidate.Version} ({candidate.Architecture})"));

            return $"{package.Architecture} {package.MinVersion} or newer is required; registered: {summary}";
        }
    }
}
