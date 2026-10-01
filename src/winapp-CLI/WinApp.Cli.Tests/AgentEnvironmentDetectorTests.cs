// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Telemetry;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests must not run in parallel because they modify process-wide environment variables
/// and the static AgentEnvironmentDetector cache.
/// </summary>
[TestClass]
[DoNotParallelize]
public class AgentEnvironmentDetectorTests
{
    // All env vars that the detector checks — must be cleaned between tests
    private static readonly string[] AllAgentEnvVars =
    [
        "AI_AGENT",
        "AGENT",
        "AMP_CURRENT_THREAD_ID",
        "CLAUDE_CODE_IS_COWORK",
        "CLAUDECODE",
        "CLAUDE_CODE",
        "CLAUDE_CODE_ENTRYPOINT",
        "CURSOR_AGENT",
        "CURSOR_SANDBOX",
        "CURSOR_EXTENSION_HOST_ROLE",
        "QWEN_CODE",
        "GEMINI_CLI",
        "CODEX_THREAD_ID",
        "CODEX_SANDBOX",
        "CODEX_CI",
        "ANTIGRAVITY_AGENT",
        "AUGMENT_AGENT",
        "CLINE_ACTIVE",
        "CLINE_TASK_ID",
        "ROO_CODE_TASK_ID",
        "CRUSH",
        "GROK_AGENT",
        "PI_CODING_AGENT",
        "KIRO_AGENT_PATH",
        "OPENCODE",
        "OPENCODE_CLIENT",
        "TRAE_AI_SHELL_ID",
        "GOOSE_TERMINAL",
        "COPILOT_CLI",
        "COPILOT_AGENT",
        "COPILOT_AGENT_SESSION_ID",
        "COPILOT_AGENT_JOB_ID",
        "CURSOR_CLI",
        "VSCODE_COPILOT_TERMINAL",
        "COPILOT_MODEL",

        // Not agent signals; cleared so tests can assert they are ignored
        "REPL_ID",
        "TERM_PROGRAM",
        "VSCODE_INJECTION",
    ];

    // CI env vars that CIEnvironmentDetectorForTelemetry checks (must stay in sync)
    private static readonly string[] CIEnvVars =
    [
        // BooleanVariables
        "TF_BUILD",
        "GITHUB_ACTIONS",
        "APPVEYOR",
        "CI",
        "TRAVIS",
        "CIRCLECI",
        // AllNotNullVariables
        "CODEBUILD_BUILD_ID",
        "AWS_REGION",
        "BUILD_ID",
        "BUILD_URL",
        "PROJECT_ID",
        // IfNonNullVariables
        "TEAMCITY_VERSION",
        "JB_SPACE_API_URL",
    ];

    [TestInitialize]
    public void ClearEnvironment()
    {
        foreach (var v in AllAgentEnvVars)
        {
            Environment.SetEnvironmentVariable(v, null);
        }

        foreach (var v in CIEnvVars)
        {
            Environment.SetEnvironmentVariable(v, null);
        }

        AgentEnvironmentDetector.ResetCache();
    }

    [TestCleanup]
    public void RestoreEnvironment()
    {
        // Ensure env vars are cleaned even if a test fails
        ClearEnvironment();
    }

    [TestMethod]
    public void Detect_NoEnvVars_ReturnsDirect()
    {
        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("direct", senderOrigin);
        Assert.IsNull(agentName);
    }

    [TestMethod]
    public void Detect_AI_AGENT_ReturnsAgentWithName()
    {
        Environment.SetEnvironmentVariable("AI_AGENT", "claude-code");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual("claude-code", agentName);
    }

    [TestMethod]
    public void Detect_AGENT_ReturnsAgentWithName()
    {
        Environment.SetEnvironmentVariable("AGENT", "goose");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual("goose", agentName);
    }

    [TestMethod]
    public void Detect_AI_AGENT_NormalizesToLowercase()
    {
        Environment.SetEnvironmentVariable("AI_AGENT", "Claude-Code");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual("claude-code", agentName);
    }

    [TestMethod]
    public void Detect_AI_AGENT_TrimsWhitespace()
    {
        Environment.SetEnvironmentVariable("AI_AGENT", "  claude-code  ");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual("claude-code", agentName);
    }

    [TestMethod]
    public void Detect_GenericVarTakesPriorityOverToolSpecific()
    {
        Environment.SetEnvironmentVariable("AI_AGENT", "codex@1.2.3");
        Environment.SetEnvironmentVariable("CLAUDECODE", "1");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual("codex@1.2.3", agentName, "A named AI_AGENT should take priority over tool-specific CLAUDECODE");
    }

    [TestMethod]
    [DataRow("claude-code_2-1-141_agent", "claude-code_2-1-141_agent")]
    [DataRow("GitHub_Copilot_VSCode_Agent", "github_copilot_vscode_agent")]
    [DataRow("github_copilot_app_agent", "github_copilot_app_agent")]
    [DataRow("codex@1.2.3", "codex@1.2.3")]
    [DataRow("hermes-agent", "hermes-agent")]
    public void Detect_AI_AGENT_ReportsValueAsIs(string value, string expectedAgentName)
    {
        Environment.SetEnvironmentVariable("AI_AGENT", value);
        Environment.SetEnvironmentVariable("CLAUDECODE", "1");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual(expectedAgentName, agentName, "A named AI_AGENT value should be reported lowercased and take priority over tool markers");
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("true")]
    [DataRow("YES")]
    [DataRow("on")]
    public void Detect_AI_AGENT_FlagValue_YieldsToToolSpecificMarker(string value)
    {
        Environment.SetEnvironmentVariable("AI_AGENT", value);
        Environment.SetEnvironmentVariable("OPENCODE", "1");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual("opencode", agentName);
    }

    [TestMethod]
    public void Detect_AI_AGENT_FlagValue_WithoutMarker_ReportsValue()
    {
        Environment.SetEnvironmentVariable("AI_AGENT", "1");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual("1", agentName);
    }

    [TestMethod]
    [DataRow("AI_AGENT", "")]
    [DataRow("AI_AGENT", "  ")]
    [DataRow("AI_AGENT", "0")]
    [DataRow("AI_AGENT", "false")]
    [DataRow("AI_AGENT", "FALSE")]
    [DataRow("AI_AGENT", "no")]
    [DataRow("AI_AGENT", "off")]
    [DataRow("AGENT", "0")]
    [DataRow("CLAUDECODE", "0")]
    [DataRow("CLAUDECODE", "false")]
    [DataRow("CLAUDE_CODE_IS_COWORK", "off")]
    [DataRow("OPENCODE", "no")]
    [DataRow("COPILOT_CLI", "0")]
    public void Detect_FalseLikeValue_IsTreatedAsNotSet(string envVar, string value)
    {
        Environment.SetEnvironmentVariable(envVar, value);

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("direct", senderOrigin);
        Assert.IsNull(agentName);
    }

    [TestMethod]
    [DataRow("goose", "goose")]
    [DataRow("amp", "amp")]
    [DataRow("Goose", "goose")]
    [DataRow("codex@1.2.3", "codex@1.2.3")]
    public void Detect_AGENT_KnownValue_ReturnsAgent(string value, string expectedAgentName)
    {
        Environment.SetEnvironmentVariable("AGENT", value);

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual(expectedAgentName, agentName);
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("true")]
    [DataRow("build-runner")]
    [DataRow("ssh-agent")]
    public void Detect_AGENT_UnknownValue_IsIgnored(string value)
    {
        Environment.SetEnvironmentVariable("AGENT", value);

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("direct", senderOrigin);
        Assert.IsNull(agentName);
    }

    [TestMethod]
    [DataRow("AMP_CURRENT_THREAD_ID", "amp")]
    [DataRow("CLAUDE_CODE_IS_COWORK", "claude-cowork")]
    [DataRow("CLAUDECODE", "claude-code")]
    [DataRow("CLAUDE_CODE", "claude-code")]
    [DataRow("CLAUDE_CODE_ENTRYPOINT", "claude-code")]
    [DataRow("CURSOR_AGENT", "cursor")]
    [DataRow("CURSOR_SANDBOX", "cursor")]
    [DataRow("CURSOR_CLI", "cursor")]
    [DataRow("QWEN_CODE", "qwen-code")]
    [DataRow("GEMINI_CLI", "gemini-cli")]
    [DataRow("CODEX_THREAD_ID", "codex")]
    [DataRow("CODEX_SANDBOX", "codex")]
    [DataRow("CODEX_CI", "codex")]
    [DataRow("ANTIGRAVITY_AGENT", "antigravity")]
    [DataRow("AUGMENT_AGENT", "augment")]
    [DataRow("CLINE_ACTIVE", "cline")]
    [DataRow("CLINE_TASK_ID", "cline")]
    [DataRow("ROO_CODE_TASK_ID", "roo-code")]
    [DataRow("CRUSH", "crush")]
    [DataRow("GROK_AGENT", "grok")]
    [DataRow("PI_CODING_AGENT", "pi")]
    [DataRow("KIRO_AGENT_PATH", "kiro")]
    [DataRow("OPENCODE", "opencode")]
    [DataRow("OPENCODE_CLIENT", "opencode")]
    [DataRow("TRAE_AI_SHELL_ID", "trae")]
    [DataRow("GOOSE_TERMINAL", "goose")]
    [DataRow("COPILOT_CLI", "copilot-cli")]
    [DataRow("COPILOT_AGENT", "copilot")]
    [DataRow("COPILOT_AGENT_SESSION_ID", "copilot")]
    [DataRow("COPILOT_AGENT_JOB_ID", "copilot")]
    [DataRow("VSCODE_COPILOT_TERMINAL", "copilot-vscode")]
    [DataRow("COPILOT_MODEL", "copilot")]
    public void Detect_ToolSpecificEnvVar_ReturnsExpectedAgent(string envVar, string expectedAgentName)
    {
        Environment.SetEnvironmentVariable(envVar, "1");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual(expectedAgentName, agentName);
    }

    [TestMethod]
    public void Detect_CurrentCopilotMarkerTakesPriorityOverLegacy()
    {
        Environment.SetEnvironmentVariable("COPILOT_MODEL", "gpt-5");
        Environment.SetEnvironmentVariable("COPILOT_CLI", "1");

        var (_, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("copilot-cli", agentName);
    }

    [TestMethod]
    public void Detect_CoworkMarkerTakesPriorityOverClaudeCode()
    {
        Environment.SetEnvironmentVariable("CLAUDECODE", "1");
        Environment.SetEnvironmentVariable("CLAUDE_CODE_IS_COWORK", "1");

        var (_, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("claude-cowork", agentName);
    }

    [TestMethod]
    public void Detect_QwenMarkerTakesPriorityOverGemini()
    {
        Environment.SetEnvironmentVariable("GEMINI_CLI", "1");
        Environment.SetEnvironmentVariable("QWEN_CODE", "1");

        var (_, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("qwen-code", agentName);
    }

    [TestMethod]
    public void Detect_CursorExtensionHostAgentExec_ReturnsCursor()
    {
        Environment.SetEnvironmentVariable("CURSOR_EXTENSION_HOST_ROLE", "agent-exec");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual("cursor", agentName);
    }

    [TestMethod]
    public void Detect_CursorExtensionHostOtherRole_IsIgnored()
    {
        Environment.SetEnvironmentVariable("CURSOR_EXTENSION_HOST_ROLE", "user");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("direct", senderOrigin);
        Assert.IsNull(agentName);
    }

    [TestMethod]
    [DataRow("REPL_ID", "workspace-1")]
    [DataRow("TERM_PROGRAM", "vscode")]
    [DataRow("VSCODE_INJECTION", "1")]
    public void Detect_NonAgentEnvVar_IsNotDetected(string envVar, string value)
    {
        Environment.SetEnvironmentVariable(envVar, value);

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("direct", senderOrigin);
        Assert.IsNull(agentName);
    }

    [TestMethod]
    public void Detect_CIEnvironment_ReturnsCi()
    {
        Environment.SetEnvironmentVariable("TF_BUILD", "true");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("ci", senderOrigin);
        Assert.IsNull(agentName);
    }

    [TestMethod]
    public void Detect_AgentTakesPriorityOverCI()
    {
        Environment.SetEnvironmentVariable("CLAUDECODE", "1");
        Environment.SetEnvironmentVariable("TF_BUILD", "true");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin, "Agent detection should take priority over CI");
        Assert.AreEqual("claude-code", agentName);
    }

    [TestMethod]
    public void Detect_CachesResult()
    {
        var first = AgentEnvironmentDetector.Detect();

        // Set an env var after first detection — should still return cached result
        Environment.SetEnvironmentVariable("AI_AGENT", "late-agent");
        var second = AgentEnvironmentDetector.Detect();

        Assert.AreEqual(first.SenderOrigin, second.SenderOrigin);
        Assert.AreEqual(first.AgentName, second.AgentName);
    }

    [TestMethod]
    public void Detect_ResetCacheThenDetectsNewValue()
    {
        var first = AgentEnvironmentDetector.Detect();
        Assert.AreEqual("direct", first.SenderOrigin);

        Environment.SetEnvironmentVariable("AI_AGENT", "codex");
        AgentEnvironmentDetector.ResetCache();

        var second = AgentEnvironmentDetector.Detect();
        Assert.AreEqual("agent", second.SenderOrigin);
        Assert.AreEqual("codex", second.AgentName);
    }
}
