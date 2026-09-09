// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// One framework MSIX package the application needs before it can register or start
/// (spec §"Runtime provisioning").
/// </summary>
/// <remarks>
/// A <em>compatible constraint</em>, not a pinned identity: the guest satisfies it with any
/// registered package of the same identity name, publisher, and architecture at
/// <see cref="MinVersion"/> or above. That is what keeps provisioning from ever needing to remove or
/// downgrade a shared runtime another application in the same guest is already using.
/// </remarks>
internal sealed class RuntimePackageRequirement
{
    /// <summary>Package identity name, exactly as the declaring manifest states it.</summary>
    public required string Name { get; init; }

    /// <summary>Lowest version that satisfies the constraint.</summary>
    public required string MinVersion { get; init; }

    /// <summary>
    /// Processor architecture the requirement applies to.
    /// </summary>
    /// <remarks>
    /// Carried explicitly and matched exactly, because an x86 package does not satisfy an x64
    /// dependency. <see cref="NeutralArchitecture"/> is the one value that matches anything, and it
    /// is only ever produced by reading a payload that really is architecture-neutral.
    /// </remarks>
    public required string Architecture { get; init; }

    /// <summary>
    /// Publisher the satisfying package must carry, when the declaration named one.
    /// </summary>
    /// <remarks>
    /// A framework dependency is resolved by Windows on (name, publisher), not on name alone, so a
    /// same-named package from a different publisher is a different package. Carrying it means the
    /// guest's verification asks the same question registration will.
    /// </remarks>
    public string? Publisher { get; init; }

    /// <summary>
    /// File name of the staged payload inside the runtime scope, or null when the host found none.
    /// </summary>
    /// <remarks>
    /// Null is not a failure by itself. Some framework dependencies have no payload in any cache the
    /// host can reach, and the guest may well already have them. Those are verified rather than
    /// installed, and only an unsatisfied verification fails the command.
    /// </remarks>
    public string? PayloadFile { get; init; }

    /// <summary>
    /// True when the requirement was derived from a resolved runtime's own inventory rather than
    /// declared by the application.
    /// </summary>
    /// <remarks>
    /// A Windows App Runtime manifest dependency names only the Framework package, but a working
    /// runtime is the Framework, its DDLM, Main, and Singleton together. Those siblings are recorded
    /// as derived requirements so they are staged, installed, and — above all — verified, instead of
    /// being installed hopefully and never checked.
    /// </remarks>
    public bool Derived { get; init; }

    /// <summary>The architecture value that satisfies any requirement.</summary>
    internal const string NeutralArchitecture = "neutral";

    /// <summary>
    /// Whether <paramref name="candidateArchitecture"/> can satisfy this requirement.
    /// </summary>
    /// <remarks>
    /// Exact match, or a genuinely neutral package. Deliberately not a "try the filtered lookup then
    /// fall back to any architecture" shape: that fallback is precisely how an x86 package comes to
    /// satisfy an x64 dependency, and the launch then fails for a reason nothing reported.
    /// </remarks>
    public bool AcceptsArchitecture(string? candidateArchitecture) =>
        candidateArchitecture is not null
        && (string.Equals(candidateArchitecture, Architecture, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidateArchitecture, NeutralArchitecture, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether <paramref name="candidatePublisher"/> can satisfy this requirement.
    /// </summary>
    /// <remarks>
    /// A requirement that named no publisher accepts any, because the declaration itself did not
    /// discriminate. When one was named, it has to match.
    /// </remarks>
    public bool AcceptsPublisher(string? candidatePublisher) =>
        Publisher is null
        || (candidatePublisher is not null
            && string.Equals(
                NormalizePublisher(candidatePublisher),
                NormalizePublisher(Publisher),
                StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Canonicalizes a distinguished name for comparison.
    /// </summary>
    /// <remarks>
    /// Manifests and registrations spell the same publisher with different spacing after the
    /// separators, so the raw strings differ where the identities do not. Only whitespace around
    /// each relative distinguished name is normalized; nothing is reordered or dropped, so two
    /// genuinely different publishers still compare as different.
    /// </remarks>
    internal static string NormalizePublisher(string publisher) =>
        string.Join(',', publisher.Split(',').Select(part => part.Trim()));
}

/// <summary>
/// One shared .NET framework the application needs, from its <c>.runtimeconfig.json</c>.
/// </summary>
/// <remarks>
/// Provisioned, not merely reported. The payload is a portable layout the host builds from an
/// official runtime pack, or from an official installation the host already has, and the guest
/// unpacks it side-by-side into a per-user root it owns — so no machine-wide <c>dotnet</c>
/// installation is touched, nothing is ever replaced, and the guest needs no network of its own.
/// </remarks>
internal sealed class RuntimeFrameworkRequirement
{
    /// <summary>Framework name, for example <c>Microsoft.WindowsDesktop.App</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Lowest version that satisfies the constraint.</summary>
    public required string MinVersion { get; init; }

    /// <summary>Architecture the apphost will look for.</summary>
    /// <remarks>
    /// A .NET installation is per-architecture: an x86 apphost cannot load an x64 shared framework,
    /// however new it is. Carrying the architecture is what keeps the resolved pack matched to the
    /// binary that will actually run.
    /// </remarks>
    public required string Architecture { get; init; }

    /// <summary>Staged portable-layout archive, or null when the host resolved none.</summary>
    public string? PayloadFile { get; init; }

    /// <summary>Exact framework version <see cref="PayloadFile"/> contains, when there is one.</summary>
    public string? PayloadVersion { get; init; }

    /// <summary>
    /// Whether an installed <paramref name="candidate"/> version satisfies this requirement.
    /// </summary>
    /// <remarks>
    /// The .NET roll-forward default: a newer patch or minor of the same major is compatible, a
    /// different major is not. Accepting any higher version would let a guest with only .NET 10
    /// "satisfy" an application built against .NET 8 — exactly the case that fails at startup with
    /// an error naming a framework the report claimed was present.
    /// </remarks>
    public bool IsSatisfiedBy(Version candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var required = RuntimeRequirementDiscovery.ComparableVersion(MinVersion);
        return candidate.Major == required.Major && candidate >= required;
    }
}

/// <summary>
/// The complete required runtime graph for one application, as handed to the guest.
/// </summary>
/// <remarks>
/// Travels through the verified file channel rather than the command line: it is content the guest
/// verb reads from a managed root it already trusts, which keeps the argument vector fixed and short
/// regardless of how many dependencies an application declares.
/// </remarks>
internal sealed class RuntimeProvisionPlan
{
    /// <summary>Schema version. A guest that reads a newer one refuses rather than guessing.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Content identity of this requirement set, used to scope guest staging.</summary>
    public required string PlanId { get; init; }

    /// <summary>Architecture the application was built for.</summary>
    public required string Architecture { get; init; }

    /// <summary>Framework MSIX packages to install or verify.</summary>
    public required List<RuntimePackageRequirement> Packages { get; init; }

    /// <summary>Shared .NET frameworks to install or verify.</summary>
    public required List<RuntimeFrameworkRequirement> Frameworks { get; init; }

    /// <summary>
    /// Guest folder the managed per-user .NET installation lives in.
    /// </summary>
    /// <remarks>
    /// Named by the host because the host has to put the same value in the launched application's
    /// environment for the apphost to find it, and two independently computed paths are two things
    /// that can disagree.
    /// </remarks>
    public required string DotNetRoot { get; init; }

    /// <summary>File name the plan is staged under inside the runtime scope.</summary>
    internal const string FileName = "runtime-plan.json";

    /// <summary>Schema version this build writes and reads.</summary>
    internal const int CurrentSchemaVersion = 2;

    /// <summary>Serializes the plan for transfer.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(this, RuntimeProvisionJsonContext.Default.RuntimeProvisionPlan);
}

/// <summary>What the guest found, and did, for one requirement.</summary>
internal sealed class RuntimeItemStatus
{
    /// <summary>Requirement identity, echoed so a report reads on its own.</summary>
    public required string Name { get; init; }

    /// <summary>Version the requirement asked for.</summary>
    public required string RequiredVersion { get; init; }

    /// <summary>Version present in the guest afterwards, when one is.</summary>
    public string? PresentVersion { get; init; }

    /// <summary>True when this run installed the payload rather than finding it already present.</summary>
    public bool Installed { get; init; }

    /// <summary>Whether the requirement is satisfied in the guest now.</summary>
    public required bool Satisfied { get; init; }

    /// <summary>Why it is not satisfied, when it is not.</summary>
    public string? Detail { get; init; }
}

/// <summary>The guest's verdict on the complete required graph.</summary>
internal sealed class RuntimeProvisionReport
{
    /// <summary>Plan this report answers, so a stale one cannot be mistaken for this pass's verdict.</summary>
    public required string PlanId { get; init; }

    /// <summary>True only when every requirement is satisfied.</summary>
    public required bool Satisfied { get; init; }

    /// <summary>Per-requirement outcome, in plan order.</summary>
    public required List<RuntimeItemStatus> Items { get; init; }

    /// <summary>
    /// The .NET root the application must be launched against, when one is needed.
    /// </summary>
    /// <remarks>
    /// Reported rather than assumed. A guest whose own installation already satisfies every
    /// framework returns null, and the launch then inherits the guest's ordinary resolution instead
    /// of being pinned to a managed root that has nothing in it.
    /// </remarks>
    public string? DotNetRoot { get; init; }

    /// <summary>Milliseconds the guest spent installing payloads.</summary>
    /// <remarks>
    /// Measured in the guest and returned rather than timed from the host, because the host's view
    /// of one exec cannot separate installing from verifying — and the spec asks for both.
    /// </remarks>
    public long InstallMilliseconds { get; init; }

    /// <summary>Milliseconds the guest spent verifying the complete required graph.</summary>
    public long VerifyMilliseconds { get; init; }

    /// <summary>Serializes the report for the host to read off standard output.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(this, RuntimeProvisionJsonContext.Default.RuntimeProvisionReport);

    /// <summary>
    /// File name the report is written under, beside the plan it answers.
    /// </summary>
    /// <remarks>
    /// The verdict travels back through the same verified file channel the payloads went out on
    /// rather than on standard output. Output frames and the exit notification are two independent
    /// sends, so a report read from a stream could be truncated by a process that exited first —
    /// and a truncated verdict is one the host would have to treat as unverified.
    /// </remarks>
    internal const string FileName = "runtime-report.json";

    /// <summary>Parses a report, returning null when the payload is not one.</summary>
    /// <remarks>
    /// Total rather than throwing: the host treats an unreadable report as an unverified graph and
    /// says so, which is a better failure than an unhandled exception on the launch path.
    /// </remarks>
    public static RuntimeProvisionReport? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, RuntimeProvisionJsonContext.Default.RuntimeProvisionReport);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Source-generated serializer context for runtime provisioning payloads.
/// </summary>
/// <remarks>
/// Source-generated because the guest half runs inside NativeAOT-published winapp, where reflection
/// based serialization is not available.
/// </remarks>
[JsonSerializable(typeof(RuntimeProvisionPlan))]
[JsonSerializable(typeof(RuntimeProvisionReport))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class RuntimeProvisionJsonContext : JsonSerializerContext
{
}
