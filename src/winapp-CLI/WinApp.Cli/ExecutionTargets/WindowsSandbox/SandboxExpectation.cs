// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>
/// The one Windows Sandbox a caller has said winapp may use, from <c>--expect-sandbox</c> or
/// <c>WINAPP_EXPECT_SANDBOX</c>.
/// </summary>
/// <remarks>
/// <para>
/// Without an expectation, <c>--on sandbox</c> uses whichever Sandbox is running and starts one when
/// none is. Automation that created its own Sandbox needs the opposite: use that instance or fail, so
/// a replaced or foreign instance is never prepared and a new one is never started behind its back.
/// </para>
/// <para>
/// The value is either a Sandbox ID (what <c>wsb start --id</c> and <c>wsb list</c> use) or an
/// epoch from winapp's own JSON output. An epoch additionally pins the generation, so the same
/// instance taken over again with fresh winapp state is refused too.
/// </para>
/// </remarks>
/// <param name="InstanceId">The expected Sandbox ID.</param>
/// <param name="Epoch">The expected generation, when the caller gave an epoch.</param>
/// <param name="Source">Where the value came from, for error messages.</param>
internal sealed record SandboxExpectation(string InstanceId, ExecutionTargetEpoch? Epoch, string Source)
{
    /// <summary>The command-line option.</summary>
    public const string OptionName = "--expect-sandbox";

    /// <summary>
    /// The environment variable. <c>--expect-sandbox</c> is copied into it before dispatch, so the
    /// option takes precedence and the lifecycle has a single place to read.
    /// </summary>
    public const string EnvironmentVariable = "WINAPP_EXPECT_SANDBOX";

    /// <summary>Reads the current expectation, or null when none is set.</summary>
    /// <exception cref="ExecutionTargetException">The value is set but malformed.</exception>
    public static SandboxExpectation? FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var raw = getEnvironmentVariable(EnvironmentVariable);

        // An empty value is treated as unset so a harness can clear it with `$env:X = ''`.
        return string.IsNullOrWhiteSpace(raw) ? null : Parse(raw, EnvironmentVariable);
    }

    /// <summary>Parses a Sandbox ID or epoch.</summary>
    /// <param name="value">The value as given.</param>
    /// <param name="source">The option or variable it came from, named in errors.</param>
    /// <exception cref="ExecutionTargetException">The value is neither a Sandbox ID nor an epoch.</exception>
    public static SandboxExpectation Parse(string? value, string source)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        var separator = trimmed.IndexOf(':', StringComparison.Ordinal);
        var id = separator < 0 ? trimmed : trimmed[..separator];
        var nonce = separator < 0 ? null : trimmed[(separator + 1)..];

        // wsb identifies instances by GUID. Requiring one turns a mistake such as
        // `--expect-sandbox sandbox` into an immediate error instead of a confusing mismatch.
        if (!Guid.TryParseExact(id, "D", out _) ||
            (nonce is not null && (nonce.Length == 0 || nonce.Any(char.IsWhiteSpace))))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetInvalid,
                $"{source} must be a Windows Sandbox ID or an epoch reported by winapp, but was '{trimmed}'.",
                userAction:
                    "Pass the ID from 'wsb list' (for example 3f2b6c1e-0000-4000-8000-000000000000), or the " +
                    "ExecutionTarget.Epoch value from a previous --json result.",
                example: $"winapp run . --on sandbox {OptionName} <sandbox-id>",
                context: new Dictionary<string, string> { ["source"] = source });
        }

        return new SandboxExpectation(
            id,
            nonce is null ? null : ExecutionTargetEpoch.Create(id, nonce),
            source);
    }

    /// <summary>Whether <paramref name="instanceId"/> is the expected Sandbox.</summary>
    public bool IsInstance(string? instanceId) =>
        string.Equals(instanceId, InstanceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="epoch"/> satisfies this expectation.</summary>
    public bool Accepts(ExecutionTargetEpoch epoch) =>
        Epoch is not { } expected || string.Equals(expected.Value, epoch.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Fails unless the expected Sandbox is the only one running.</summary>
    /// <exception cref="ExecutionTargetException">It is not running, or another one is.</exception>
    public void RequireOnlyRunning(IReadOnlyList<string> running)
    {
        ArgumentNullException.ThrowIfNull(running);

        if (running.Count == 1 && IsInstance(running[0]))
        {
            return;
        }

        var expectedIsRunning = running.Any(IsInstance);
        var message = running.Count == 0
            ? $"The expected Windows Sandbox {InstanceId} is not running."
            : expectedIsRunning
                ? $"Another Windows Sandbox is running alongside the expected one ({InstanceId})."
                : $"A different Windows Sandbox is running instead of the expected one ({InstanceId}).";

        throw Mismatch(
            message,
            running.Count == 0
                ? $"Start the expected Sandbox, or check the ID passed with {OptionName} or {EnvironmentVariable}."
                : $"winapp will not use or stop a Sandbox you did not expect. Close it only if it is not in use, or update {OptionName} or {EnvironmentVariable}.",
            running);
    }

    /// <summary>
    /// The error for an expected Sandbox that is running but is no longer the generation the caller
    /// named.
    /// </summary>
    public ExecutionTargetException GenerationMismatch(ExecutionTargetEpoch? actual) =>
        Mismatch(
            $"The expected Windows Sandbox {InstanceId} is running, but not in the generation that was expected.",
            "Rediscover the current epoch with 'winapp target snapshot sandbox --json', or pass only the Sandbox ID.",
            running: null,
            actual);

    private ExecutionTargetException Mismatch(
        string message,
        string userAction,
        IReadOnlyList<string>? running,
        ExecutionTargetEpoch? actualEpoch = null)
    {
        var context = new Dictionary<string, string>
        {
            ["expectedSandboxId"] = InstanceId,
            ["source"] = Source,
        };

        if (Epoch is { } expectedEpoch)
        {
            context["expectedEpoch"] = expectedEpoch.Value;
        }

        if (actualEpoch is { IsNone: false } actual)
        {
            context["epoch"] = actual.Value;
        }

        if (running is not null)
        {
            context["sandboxIds"] = string.Join(',', running);
        }

        return ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.InstanceMismatch,
            message,
            userAction: userAction,
            context: context,
            nextCommand: running is { Count: > 0 }
                ? new ExecutionTargetNextCommand { Command = "wsb list", Advisory = true }
                : null);
    }
}
