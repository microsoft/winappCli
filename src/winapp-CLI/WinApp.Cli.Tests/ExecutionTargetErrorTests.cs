// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.Tests;

/// <summary>
/// Pins the released execution-target failure contract. The spec makes <c>code</c> values stable
/// once released and pins the envelope shape, so these tests are intentionally change-detectors:
/// renaming a code or reshaping the envelope is a breaking change for every consumer parsing
/// <c>--json</c> output and must be a deliberate, reviewed act.
/// </summary>
[TestClass]
public class ExecutionTargetErrorTests
{
    /// <summary>
    /// The exact released code set, in spec order. Update this only when intentionally adding a
    /// code — never to make a rename compile.
    /// </summary>
    private static readonly string[] ExpectedCodes =
    [
        "sandbox_unsupported",
        "sandbox_unmanaged_instance",
        "sandbox_start_failed",
        "sandbox_no_interactive_session",
        "sandbox_input_not_ready",
        "sandbox_terminated",
        "sandbox_agent_incompatible",
        "sandbox_agent_upgrade_failed",
        "sandbox_agent_busy",
        "sandbox_transport_failed",
        "sandbox_transfer_interrupted",
        "sandbox_runtime_provision_failed",
        "sandbox_deployment_dirty",
        "sandbox_package_conflict",
        "sandbox_provisioned_package_conflict",
        "sandbox_target_ambiguous",
        "sandbox_target_stale",
        "sandbox_stale_handle",
        "sandbox_artifact_failed",
        "sandbox_setup_requires_elevation",
        "sandbox_setup_requires_restart",
        "sandbox_setup_failed",
        "sandbox_setup_incomplete",
        "target_invalid",
        "target_invalid_arguments",
    ];

    [TestMethod]
    public void AllCodes_MatchTheReleasedSnapshot()
    {
        CollectionAssert.AreEqual(
            ExpectedCodes,
            ExecutionTargetErrorCodes.All.ToArray(),
            "Execution-target error codes are a stable public contract. Adding a code is allowed; " +
            "renaming, reordering, or removing one breaks released consumers.");
    }

    [TestMethod]
    public void AllCodes_AreUniqueAndCorrectlyNamespaced()
    {
        var codes = ExecutionTargetErrorCodes.All;

        Assert.AreEqual(codes.Length, codes.Distinct(StringComparer.Ordinal).Count(), "Codes must be unique.");

        // Two namespaces, and which one a code belongs in is a claim about when it can happen.
        // A `sandbox_` code means a provider was already selected and something about it failed;
        // a `target_` code is raised before any provider is chosen. Naming Sandbox in a failure
        // that has nothing to do with Sandbox would send the reader to the wrong place.
        foreach (var code in codes)
        {
            Assert.IsTrue(
                code.StartsWith("sandbox_", StringComparison.Ordinal) ||
                code.StartsWith("target_", StringComparison.Ordinal),
                $"'{code}' must be either provider-specific (sandbox_) or target-neutral (target_).");
        }
    }

    [TestMethod]
    public void Serialize_FullEnvelope_MatchesSpecShape()
    {
        var error = new ExecutionTargetErrorInfo
        {
            Code = ExecutionTargetErrorCodes.UnmanagedInstance,
            Message = "Another Windows Sandbox instance is already running.",
            Context = new Dictionary<string, string> { ["sandboxId"] = "abc" },
            UserAction = "Close the existing Sandbox if it is safe to do so, then retry.",
            NextCommand = new ExecutionTargetNextCommand { Command = "wsb stop --id abc", Advisory = true },
            Example = "winapp run . --on sandbox",
        };

        var json = ExecutionTargetErrorSerializer.Serialize(error);

        using var document = JsonDocument.Parse(json);
        var errorElement = document.RootElement.GetProperty("error");

        Assert.AreEqual("sandbox_unmanaged_instance", errorElement.GetProperty("code").GetString());
        Assert.AreEqual(
            "Another Windows Sandbox instance is already running.",
            errorElement.GetProperty("message").GetString());
        Assert.AreEqual("abc", errorElement.GetProperty("context").GetProperty("sandboxId").GetString());
        Assert.AreEqual(
            "Close the existing Sandbox if it is safe to do so, then retry.",
            errorElement.GetProperty("userAction").GetString());
        Assert.AreEqual("wsb stop --id abc", errorElement.GetProperty("nextCommand").GetProperty("command").GetString());
        Assert.IsTrue(
            errorElement.GetProperty("nextCommand").GetProperty("advisory").GetBoolean(),
            "Commands needing user judgement must be advisory so they are never run automatically.");
        Assert.AreEqual("winapp run . --on sandbox", errorElement.GetProperty("example").GetString());
    }

    [TestMethod]
    public void Serialize_OmitsNullOptionalFields()
    {
        var error = new ExecutionTargetErrorInfo
        {
            Code = ExecutionTargetErrorCodes.Terminated,
            Message = "The Windows Sandbox was terminated.",
        };

        var json = ExecutionTargetErrorSerializer.Serialize(error);

        using var document = JsonDocument.Parse(json);
        var errorElement = document.RootElement.GetProperty("error");

        // Optional members carry no information when unset; emitting them as null would add noise
        // to every envelope and force consumers to distinguish "absent" from "null".
        foreach (var optional in new[] { "context", "userAction", "nextCommand", "validValues", "example", "recoveredFrom" })
        {
            Assert.IsFalse(
                errorElement.TryGetProperty(optional, out _),
                $"Unset optional field '{optional}' should be omitted, not serialized as null.");
        }
    }

    [TestMethod]
    public void Serialize_RecoveredFrom_NestsTheOriginalFailure()
    {
        var error = new ExecutionTargetErrorInfo
        {
            Code = ExecutionTargetErrorCodes.TransportFailed,
            Message = "Reconnected after a transient transport failure.",
            RecoveredFrom = new ExecutionTargetErrorInfo
            {
                Code = ExecutionTargetErrorCodes.Terminated,
                Message = "The Windows Sandbox was terminated.",
            },
        };

        var json = ExecutionTargetErrorSerializer.Serialize(error);

        using var document = JsonDocument.Parse(json);
        var recovered = document.RootElement.GetProperty("error").GetProperty("recoveredFrom");

        Assert.AreEqual("sandbox_terminated", recovered.GetProperty("code").GetString());
    }

    [TestMethod]
    public void CreateException_CarriesStructuredError()
    {
        var exception = ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.Unsupported,
            "Windows Sandbox is not available on this host.",
            userAction: "Enable the Windows Sandbox optional feature.");

        Assert.AreEqual("sandbox_unsupported", exception.Error.Code);
        Assert.AreEqual("Windows Sandbox is not available on this host.", exception.Message);
        Assert.AreEqual("Enable the Windows Sandbox optional feature.", exception.Error.UserAction);
    }
}
