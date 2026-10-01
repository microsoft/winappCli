// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Telemetry;

/// <summary>
/// Detects whether the CLI is being invoked by an AI coding agent, a CI system, or directly by a human.
/// Mirrors the pattern of <see cref="CIEnvironmentDetectorForTelemetry"/> for agent detection.
/// </summary>
internal sealed class AgentEnvironmentDetector
{
    /// <summary>
    /// Sender origin values for telemetry segmentation.
    /// </summary>
    internal static class SenderOrigins
    {
        public const string Direct = "direct";
        public const string Agent = "agent";
        public const string CI = "ci";
    }

    /// <summary>
    /// Agent name reported when an agent is detected but cannot be identified. Unrecognized
    /// <c>AI_AGENT</c> values are collapsed to this rather than recorded, because the variable is
    /// free-form: recording it would fragment telemetry and could capture arbitrary user data.
    /// </summary>
    internal const string OtherAgentName = "other";

    /// <summary>
    /// Vendor-neutral variable whose value names the agent (Vercel/detect-agent convention).
    /// Values vary by version and surface, e.g. <c>claude-code_2-1-141_agent</c>,
    /// <c>github_copilot_vscode_agent</c>, or <c>codex@1.2.3</c>, so they are normalized.
    /// </summary>
    private const string GenericAgentVariable = "AI_AGENT";

    /// <summary>
    /// AGENTS.md community convention modeled on CI=true (e.g., AGENT=goose, AGENT=amp).
    /// Non-AI tools also use this name, so it is accepted only when its value is a known agent.
    /// See: https://github.com/agentsmd/agents.md/issues/136
    /// </summary>
    private const string AmbiguousAgentVariable = "AGENT";

    private const string GitHubCopilotPrefix = "github_copilot_";

    /// <summary>
    /// Maps the name portion of a generic agent value (before any '@' or '_') to a stable agent name.
    /// </summary>
    private static readonly Dictionary<string, string> KnownAgentNames = new(StringComparer.Ordinal)
    {
        ["amp"] = "amp",
        ["amazonq"] = "amazon-q",
        ["amazon-q"] = "amazon-q",
        ["amazon-q-cli"] = "amazon-q",
        ["antigravity"] = "antigravity",
        ["augment"] = "augment",
        ["augment-cli"] = "augment",
        ["claude"] = "claude-code",
        ["claude-code"] = "claude-code",
        ["claudecode"] = "claude-code",
        ["cowork"] = "claude-cowork",
        ["claude-cowork"] = "claude-cowork",
        ["cline"] = "cline",
        ["codex"] = "codex",
        ["codex-cli"] = "codex",
        ["copilot"] = "copilot",
        ["github-copilot"] = "copilot",
        ["copilot-cli"] = "copilot-cli",
        ["github-copilot-cli"] = "copilot-cli",
        ["crush"] = "crush",
        ["cursor"] = "cursor",
        ["cursor-cli"] = "cursor",
        ["gemini"] = "gemini-cli",
        ["gemini-cli"] = "gemini-cli",
        ["goose"] = "goose",
        ["grok"] = "grok",
        ["grok-cli"] = "grok",
        ["kiro"] = "kiro",
        ["kiro-cli"] = "kiro",
        ["opencode"] = "opencode",
        ["openhands"] = "openhands",
        ["pi"] = "pi",
        ["qwen"] = "qwen-code",
        ["qwen-code"] = "qwen-code",
        ["qwencode"] = "qwen-code",
        ["roo"] = "roo-code",
        ["roo-code"] = "roo-code",
        ["roocode"] = "roo-code",
        ["trae"] = "trae",
    };

    /// <summary>
    /// Maps the surface in <c>github_copilot_&lt;surface&gt;_agent</c> to a stable agent name.
    /// </summary>
    private static readonly Dictionary<string, string> GitHubCopilotSurfaces = new(StringComparer.Ordinal)
    {
        ["vscode"] = "copilot-vscode",
        ["app"] = "copilot-app",
        ["cli"] = "copilot-cli",
    };

    /// <summary>
    /// Tool-specific environment variables. Each entry maps an env var to a normalized agent name.
    /// Checked after the generic variables. More-specific derivatives precede the agents whose
    /// compatibility variables they inherit (e.g. Qwen Code is a Gemini CLI fork).
    /// </summary>
    private static readonly (string EnvVar, string AgentName)[] ToolSpecificAgentVariables =
    [
        // Amp - https://ampcode.com
        ("AMP_CURRENT_THREAD_ID", "amp"),

        // Claude Code - https://github.com/anthropics/claude-code
        ("CLAUDE_CODE_IS_COWORK", "claude-cowork"),
        ("CLAUDECODE", "claude-code"),
        ("CLAUDE_CODE", "claude-code"),
        ("CLAUDE_CODE_ENTRYPOINT", "claude-code"),

        // Cursor - https://cursor.com
        ("CURSOR_AGENT", "cursor"),
        ("CURSOR_SANDBOX", "cursor"),

        // Qwen Code - https://github.com/QwenLM/qwen-code
        ("QWEN_CODE", "qwen-code"),

        // Gemini CLI - https://github.com/google-gemini/gemini-cli
        ("GEMINI_CLI", "gemini-cli"),

        // OpenAI Codex CLI - https://github.com/openai/codex
        ("CODEX_THREAD_ID", "codex"),
        ("CODEX_SANDBOX", "codex"),
        ("CODEX_CI", "codex"),

        ("ANTIGRAVITY_AGENT", "antigravity"),
        ("AUGMENT_AGENT", "augment"),

        // Cline - https://github.com/cline/cline
        ("CLINE_ACTIVE", "cline"),
        ("CLINE_TASK_ID", "cline"),

        // Roo Code - https://github.com/RooCodeInc/Roo-Code
        ("ROO_CODE_TASK_ID", "roo-code"),

        // Crush - https://github.com/charmbracelet/crush
        ("CRUSH", "crush"),

        ("GROK_AGENT", "grok"),
        ("PI_CODING_AGENT", "pi"),
        ("KIRO_AGENT_PATH", "kiro"),

        // OpenCode - https://github.com/sst/opencode
        ("OPENCODE", "opencode"),
        ("OPENCODE_CLIENT", "opencode"),

        ("TRAE_AI_SHELL_ID", "trae"),

        // Goose (Block) - https://github.com/block/goose
        ("GOOSE_TERMINAL", "goose"),

        // GitHub Copilot CLI
        ("COPILOT_CLI", "copilot-cli"),

        // GitHub Copilot (VS Code agent terminals, Copilot coding agent)
        ("COPILOT_AGENT", "copilot"),
        ("COPILOT_AGENT_SESSION_ID", "copilot"),
        ("COPILOT_AGENT_JOB_ID", "copilot"),
    ];

    private static readonly Lock CacheLock = new();
    private static (string SenderOrigin, string? AgentName)? cachedResult;

    /// <summary>
    /// Detects the sender origin and agent name from environment variables.
    /// Results are cached for the lifetime of the process.
    /// </summary>
    /// <returns>
    /// A tuple of (SenderOrigin, AgentName) where SenderOrigin is one of "direct", "agent", or "ci",
    /// and AgentName is the normalized agent name when detected, or null otherwise.
    /// </returns>
    public static (string SenderOrigin, string? AgentName) Detect()
    {
        if (cachedResult.HasValue)
        {
            return cachedResult.Value;
        }

        lock (CacheLock)
        {
            if (cachedResult.HasValue)
            {
                return cachedResult.Value;
            }

            var result = DetectInternal();
            cachedResult = result;
            return result;
        }
    }

    /// <summary>
    /// Clears the cached detection result. Intended for unit testing only.
    /// </summary>
    internal static void ResetCache()
    {
        lock (CacheLock)
        {
            cachedResult = null;
        }
    }

    /// <summary>
    /// Returns the variable's value when it is set to something other than an empty or
    /// false-like value ("0", "false", "no", "off"); otherwise <c>null</c>.
    /// </summary>
    private static string? GetEnabledValue(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable)?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.ToLowerInvariant() switch
        {
            "0" or "false" or "no" or "off" => null,
            _ => value,
        };
    }

    /// <summary>
    /// Maps a generic agent value to a stable agent name, stripping version and surface suffixes
    /// (e.g. "claude-code_2-1-141_agent" -> "claude-code", "codex@1.2.3" -> "codex").
    /// Returns <c>null</c> when the value is not a known agent.
    /// </summary>
    private static string? ClassifyAgentValue(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();

        if (normalized.StartsWith(GitHubCopilotPrefix, StringComparison.Ordinal))
        {
            var surface = normalized[GitHubCopilotPrefix.Length..].Split('@', '_')[0];
            return GitHubCopilotSurfaces.GetValueOrDefault(surface, "copilot");
        }

        var name = normalized.Split('@')[0].Split('_')[0];
        return KnownAgentNames.GetValueOrDefault(name);
    }

    private static (string SenderOrigin, string? AgentName) DetectInternal()
    {
        // 1. A recognized AI_AGENT value is authoritative. A flag-like or unknown value proves an
        //    agent is present but lets a tool-specific marker identify it first.
        var genericValue = GetEnabledValue(GenericAgentVariable);
        if (genericValue is not null)
        {
            var agentName = ClassifyAgentValue(genericValue);
            if (agentName == "claude-code" && GetEnabledValue("CLAUDE_CODE_IS_COWORK") is not null)
            {
                agentName = "claude-cowork";
            }

            if (agentName is not null)
            {
                return (SenderOrigins.Agent, agentName);
            }
        }

        // 2. AGENT is shared with non-AI tools, so only known agent names count.
        var ambiguousValue = GetEnabledValue(AmbiguousAgentVariable);
        if (ambiguousValue is not null && ClassifyAgentValue(ambiguousValue) is { } knownAgentName)
        {
            return (SenderOrigins.Agent, knownAgentName);
        }

        // 3. Check tool-specific agent environment variables
        foreach (var (envVar, agentName) in ToolSpecificAgentVariables)
        {
            if (GetEnabledValue(envVar) is not null)
            {
                return (SenderOrigins.Agent, agentName);
            }
        }

        if (string.Equals(GetEnabledValue("CURSOR_EXTENSION_HOST_ROLE"), "agent-exec", StringComparison.OrdinalIgnoreCase))
        {
            return (SenderOrigins.Agent, "cursor");
        }

        if (genericValue is not null)
        {
            return (SenderOrigins.Agent, OtherAgentName);
        }

        // 4. Fall back to CI detection
        if (CIEnvironmentDetectorForTelemetry.IsCIEnvironment())
        {
            return (SenderOrigins.CI, null);
        }

        // 5. Default: direct human invocation
        return (SenderOrigins.Direct, null);
    }
}
