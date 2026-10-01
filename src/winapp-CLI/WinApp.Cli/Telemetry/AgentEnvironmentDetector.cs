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
    /// Vendor-neutral variable whose value names the agent (Vercel/detect-agent convention),
    /// e.g. <c>claude-code_2-1-141_agent</c> or <c>github_copilot_vscode_agent</c>. The value is
    /// reported as-is (trimmed, lowercased); versions and surfaces are grouped downstream.
    /// </summary>
    private const string GenericAgentVariable = "AI_AGENT";

    /// <summary>
    /// AGENTS.md community convention modeled on CI=true (e.g., AGENT=goose, AGENT=amp).
    /// Non-AI tools also use this name, so it is accepted only when its value is a known agent.
    /// See: https://github.com/agentsmd/agents.md/issues/136
    /// </summary>
    private const string AmbiguousAgentVariable = "AGENT";

    /// <summary>
    /// Agent names accepted from <see cref="AmbiguousAgentVariable"/>. Matched exactly, because
    /// prefixed values such as "codex_worker" are just as likely to come from non-AI tools.
    /// </summary>
    private static readonly HashSet<string> KnownAgentNames = new(StringComparer.Ordinal)
    {
        "amp",
        "amazon-q",
        "antigravity",
        "augment",
        "claude",
        "claude-code",
        "cline",
        "codex",
        "copilot",
        "copilot-cli",
        "crush",
        "cursor",
        "gemini",
        "gemini-cli",
        "goose",
        "grok",
        "kiro",
        "opencode",
        "openhands",
        "pi",
        "qwen-code",
        "replit",
        "roo-code",
        "trae",
    };

    /// <summary>
    /// Tool-specific environment variables. Each entry maps an env var to a normalized agent name.
    /// Checked after the generic variables. More-specific derivatives precede the agents whose
    /// compatibility variables they inherit (e.g. Qwen Code is a Gemini CLI fork).
    /// Legacy markers stay at the end so they only apply when no current marker is present.
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
        ("CURSOR_CLI", "cursor"),

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

        // Legacy markers, kept for continuity with existing telemetry
        ("VSCODE_COPILOT_TERMINAL", "copilot-vscode"),
        ("COPILOT_MODEL", "copilot"),
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

    private static bool IsFlagLike(string value)
    {
        return value.ToLowerInvariant() is "1" or "true" or "yes" or "on";
    }

    private static (string SenderOrigin, string? AgentName) DetectInternal()
    {
        // 1. AI_AGENT names the agent. A flag-like value (e.g. "1") only proves an agent is
        //    present, so a tool-specific marker below gets to identify it first.
        var genericValue = GetEnabledValue(GenericAgentVariable)?.ToLowerInvariant();
        if (genericValue is not null && !IsFlagLike(genericValue))
        {
            return (SenderOrigins.Agent, genericValue);
        }

        // 2. AGENT is shared with non-AI tools, so only known agent names count.
        var ambiguousValue = GetEnabledValue(AmbiguousAgentVariable)?.ToLowerInvariant();
        if (ambiguousValue is not null && KnownAgentNames.Contains(ambiguousValue))
        {
            return (SenderOrigins.Agent, ambiguousValue);
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

        if (string.Equals(GetEnabledValue("REPLIT_MODE"), "assistant", StringComparison.OrdinalIgnoreCase) ||
            GetEnabledValue("REPL_ID") is not null)
        {
            return (SenderOrigins.Agent, "replit");
        }

        if (genericValue is not null)
        {
            return (SenderOrigins.Agent, genericValue);
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
