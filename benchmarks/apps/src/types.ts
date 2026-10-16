export type RunStatus =
  | "queued"
  | "setup"
  | "building"
  | "build_done"
  | "dotnet_build"
  | "launching"
  | "validating"
  | "retrospective"
  | "done"
  | "failed"
  | "interrupted"
  | "timeout";

export interface RunEntry {
  id: string;
  scenario: string;
  scenarioPath: string;
  scenarioConfigName: string;
  condition: string;
  pluginPath: string;
  model: string;
  trialName: string;
  /**
   * Human-readable task name declared on the (flat) task config. The trial
   * folder is `{NNN}_{name}` (NNN = the task's 0-based index in the run, e.g.
   * `000_AdaptiveStreaming`). Optional for back-compat with older runs whose
   * trialName followed the legacy `scenario_agent_..._model_iter` scheme.
   */
  name?: string;
  /**
   * Per-task build timeout (minutes). When unset, the queue's global
   * `maxBuildMinutes` is used. Lets a flat task list mix long/short budgets.
   */
  maxBuildMinutes?: number;
  /**
   * Per-task switch to skip the LLM-judge validation/scoring step. When unset,
   * the global `SKIP_VALIDATION` env (set via `--skip-validation`) applies.
   * Lets advanced, hand-authored/scripted flat runs do their own scoring as
   * separate tasks without wasting grader tokens on the built-in judge.
   */
  skipValidation?: boolean;
  iteration?: number;
  totalIterations?: number;
  status: RunStatus;
  score?: number;
  qualityBreakdown?: string; // scoring v2: "R89% Q100% D7" (requirements %, quality-check %, design 0–10)
  builds?: boolean;
  runs?: boolean;
  sessionTime?: string;
  apiTime?: string;
  codeChanges?: string;
  inputTokens?: string;
  outputTokens?: string;
  cachedTokens?: string;
  tokenDisplay?: string; // Rich real-time display: "out: 37.8k (main: 35k, subs: 2.8k)"
  premiumRequests?: number;
  credits?: number;               // precise AI Unit (AIU) credit cost of the build session
  subAgentInputTokens?: string;   // sum of sub-agent inputTokens from session.shutdown
  subAgentOutputTokens?: string;  // sum of sub-agent outputTokens from session.shutdown
  subAgentCachedTokens?: string;  // sum of sub-agent cacheReadTokens from session.shutdown
  subAgentCount?: number;         // number of sub-agents spawned
  subAgentDetails?: Array<{ name: string; totalTokens: number; durationMs: number }>;
  failReason?: string;
  currentOutput: string;
  startedAt?: Date;
  finishedAt?: Date;
  buildSessionId?: string;
  validationSessionId?: string;
  completionReason?: "sdk_idle" | "terminal_turn" | "hard_timeout" | "session_error";
  /**
   * If this trial's agent was materialized from a GitHub repo, the requested
   * ref. Set by the TUI or `--upstream-ref` CLI flag; propagated through to
   * the installer and recorded in results.json.
   */
  upstreamRef?: string;
  /** Resolved provenance after the upstream clone — set by the installer. */
  agentSource?: AgentSource;
}

/**
 * One explicit unit of work in a flat run. The run's `run-meta.json` carries a
 * `tasks: FlatTask[]` list (one entry per trial), instead of the legacy
 * scenario × agent × model × iteration cross-product. This makes a run fully
 * editable / scriptable: a hand-authored or generated run-meta can stack
 * arbitrary tasks, each with its own agent / model / timeout / validation
 * behaviour. The UI still collects the same inputs and simply expands them into
 * this flat list.
 */
export interface FlatTask {
  /** Human-readable name; the trial folder is `{NNN}_{name}` (NNN = list index). */
  name: string;
  /** Scenario config name to run this task against. */
  scenario: string;
  /** Agent setup name. */
  agent: string;
  /** Model to run this task with. Required — each task is self-contained. */
  model: string;
  /** Build-timeout in minutes. Optional; falls back to DEFAULT_MAX_BUILD_MINUTES. */
  maxBuildMinutes?: number;
  /** Per-task upstream ref for from_repo agents. */
  upstreamRef?: string | null;
  /** Skip the LLM-judge step for this task. Optional; absent = run validation. */
  skipValidation?: boolean;
  /** Optional 1-based iteration label for display when a scenario repeats. */
  iteration?: number;
  /** Optional total-iterations label paired with `iteration`. */
  totalIterations?: number;
}

/**
 * Flat `run-meta.json` shape (version 2). The top level carries ONLY whole-list
 * controls that a single task cannot own: `concurrency`. Everything per-trial
 * lives in `tasks[]`, which is the single source of truth — there is NO
 * `defaults` block, so a hand-edited or generated meta can never drift and has
 * no footgun where a top-level value is silently shadowed by per-task values.
 * Each task is fully self-contained (see FlatTask); required fields are checked
 * by `validateFlatTasks` before a run is created. Older v1 metas (top-level
 * summary arrays) and early v2 metas (with a `defaults` block) are still read
 * for back-compat, but neither is written anymore.
 */
export interface RunMetaV2 {
  version: 2;
  timestamp: string;
  /** The only whole-list control — how many trials run in parallel. */
  concurrency: number;
  /** The single source of truth — one self-contained entry per trial. */
  tasks: FlatTask[];
}

export interface ScenarioConfig {
  name: string;
  description: string;
  type: "new" | "convert" | "improve";
  app_name?: string;
  /**
   * UI framework the scenario asks for. Drives gate G0 (right framework) and
   * which automatic quality checks apply. Defaults to "winui".
   */
  framework?: string;
  /**
   * Extra artifact gates (rules v2, G3). `native_aot`: the harness runs
   * `winapp run --aot -c Release` itself; the publish must succeed without
   * IL2xxx/IL3xxx warnings (and without suppressing them project-wide), and
   * the published app must show a window. A failed gate scores 0.
   */
  gates?: Array<"native_aot">;
  requirements?: string[];
  test_notes?: string;
  test_assets?: Array<{
    name: string;
    path: string;
    description?: string;
    include_in_build?: boolean;
  }>;
  original_app?: {
    source_dir?: string;
    build_command?: string;
    run_command?: string;
    run_args?: string;
    app_name?: string;
  };
}

export interface GlobalConfig {
  conditions?: {
    starter?: { template_command: string; prompt_addendum: string };
    agentsetup?: {
      template_command: string;
      prompt_addendum: string;
    };
  };
  agentsetups?: { root: string };
  /**
   * Model that validates and scores every trial. Fixed per run and independent
   * of the model under test, so model comparisons share one grader.
   * `BENCH_JUDGE_MODEL` overrides it.
   */
  judge?: { model?: string };
  /** Build command for non-Windows hosts only; Windows builds with `dotnet build -r win-<host arch>`. */
  build?: { command: string; fallback_command?: string };
}

export type ScriptEntry = string | { name: string; timeout_minutes?: number };

/**
 * Declares a NuGet package whose payload contains agent-kit assets.
 * The harness resolves the latest restored version under `~/.nuget/packages/<package>/`
 * and treats the joined subpath as an additional source root for skills / agent files.
 */
export interface FromNugetPackage {
  /** Lowercase package id, e.g. "contoso.ui.toolkit". */
  package: string;
  /**
   * Path inside the package root pointing to the resource. For skills this is the
   * folder containing one subfolder per skill (each with SKILL.md); for agent
   * files this is the full relative path to the .agent.md.
   */
  subpath: string;
}

/**
 * Declares a GitHub repository whose checkout contains a plugin (agent +
 * skills). The harness shallow-clones the repo at the resolved ref into a
 * local cache and treats the resolved subpath as a plugin root; see
 * `runner/plugin-layout.ts` for the supported layouts. Trials fail if the
 * plugin yields no agent or no skills.
 *
 * Used for benchmarking the WinUI and WinApp CLI plugins in
 * microsoft/winappCli (or any fork/branch/PR) without copying files into this
 * repo.
 */
export interface FromRepoSource {
  /** GitHub repo in "owner/name" form, e.g. "microsoft/winappCli". */
  repo: string;
  /**
   * Default git ref (branch, tag, or 40-char SHA) when none is supplied at
   * runtime via the TUI or `--upstream-ref`. Defaults to "main" if omitted.
   */
  default_ref?: string;
  /**
   * Legacy mode: path inside the repo to a single plugin root, e.g.
   * "plugins/winui/agent-plugin". The plugin's agent is installed as a custom
   * agent and its skills are passed as skill directories.
   */
  plugin_subpath?: string;
  /**
   * Plugin mode: paths inside the repo to plugin roots that are loaded as real
   * plugins through the SDK's `pluginDirectories`, the same way a marketplace
   * install loads them. No agent is selected unless `agent` is set.
   */
  plugins?: string[];
  /** Plugin mode only: name of a plugin agent to select (e.g. "winui-dev"). */
  agent?: string;
  /**
   * Catalog mode: path inside `repo` to a plugin marketplace catalog (e.g.
   * ".github/plugin/marketplace.json"). `plugins` then lists plugin *names*
   * from the catalog; each is loaded from the source the catalog pins
   * (`source.repo` + `source.path` at `source.sha`/`source.ref`, or a
   * relative path inside `repo`). Use it to benchmark what users get from
   * the published catalog; `ref` selects the catalog version.
   */
  catalog?: string;
}

export interface AgentSetupConfig {
  description?: string;
  skills?: {
    /**
     * Skill names to **make available** to the agent. The agent can load any of
     * these on demand via the `skill(name)` tool call. On the SDK path, skills
     * outside this list found in any active `skillDirectory` are added to
     * `disabledSkills` so they don't pollute the discoverable set.
     */
    include?: string[];
    /** Skill names to exclude (vs include + all). */
    exclude?: string[];
    /** Make every skill discoverable. */
    all?: boolean;
    /**
     * Skill names to **preload** (eagerly inject) into the agent's context at
     * session start. Subset of `include`. Defaults to none — the agent loads
     * on demand. Maps to the SDK's `customAgents[0].skills`.
     */
    preload?: string[];
    /** Extra skill source roots resolved from NuGet caches at install time. */
    from_packages?: FromNugetPackage[];
  };
  mcp?: { include?: string[]; exclude?: string[]; all?: boolean };
  preset_scripts?: ScriptEntry[];
  /**
   * Paths to prepend to `PATH` for the trial. Applied to preset_scripts, build,
   * copilot, validation, etc. Supports `${HOME}` and `${arch}` (lowercase process.arch).
   */
  env?: {
    path_prepend?: string[];
    /**
     * Tools to hide from the trial's PATH (e.g. ["winapp"] for the `bare`
     * baseline). PATH directories containing them are dropped; the other
     * executables in those directories stay reachable through generated shims.
     */
    hide_tools?: string[];
    /**
     * Give the trial a fresh, empty `DOTNET_CLI_HOME` (no preinstalled
     * `dotnet new` templates or global tools). Default true. Set false only as
     * a temporary workaround for tools that ignore `DOTNET_CLI_HOME` (WinApp
     * CLI before microsoft/winappCli#1068), and on every agent being compared.
     */
    isolate_dotnet_home?: boolean;
  };
  /**
   * Resolve `agent` from a NuGet package payload at install time. Mutually exclusive
   * with `agent`. If both are set, `agent_from_package` wins.
   */
  agent_from_package?: FromNugetPackage;
  /**
   * Resolve the entire plugin (agent + skills + optional MCP) from a GitHub
   * repo at a specific ref. The cached plugin folder is used as the
   * `pluginPath`, its `agents/*.agent.md` becomes the agent file, and its
   * `skills/` directory is added as a skill source root.
   *
   * Mutually exclusive with `agent` / `agent_from_package` / `sections`.
   * `default_ref` may be overridden at runtime via the TUI branch picker or
   * `--upstream-ref` on the headless CLI.
   */
  from_repo?: FromRepoSource;
  /**
   * Plugins from this winappCli checkout (paths relative to the repo root), loaded
   * through the SDK's `pluginDirectories` like a marketplace install. Benchmarks the
   * branch you have checked out, including uncommitted changes (recorded as `dirty`).
   */
  from_local?: { plugins: string[]; agent?: string };
  /**
   * Trial-blocking preflight checks. Run after preset_scripts + the PATH
   * refresh that follows them, before any agent / scaffold / build step.
   * If a check fails, the trial is marked failed with the supplied guidance
   * and the harness stops before paying for a copilot session.
   */
  prechecks?: {
    /**
     * Tools that must be discoverable on the trial PATH (e.g. "tool.exe").
     *
     * Optional `verify` runs the tool after it's found and asserts something
     * about its output. Use this when "the binary exists" is not enough —
     * e.g. `TOOL_HOME` may point at a *stale* checkout whose `tool.exe` predates
     * a required subcommand. Without `verify`, the harness can't tell, and
     * every trial of that agent silently wastes its first build attempt on
     * `Unknown option: check` before falling back to bare `dotnet build`.
     */
    required_on_path?: Array<{
      tool: string;
      guidance?: string;
      verify?: {
        /** Args to invoke the tool with. Defaults to ["--help"]. */
        args?: string[];
        /** Substring that must appear in stdout (case-insensitive). */
        stdout_contains?: string;
        /** Optional override for the failure guidance shown to the user. */
        guidance?: string;
      };
    }>;
    /**
     * Environment variables that must be set. If `must_exist_path` is true the
     * value is also checked as a filesystem path. Use this for things like
     * `TOOL_HOME` where a tool's discoverability depends on the var being set
     * before the trial PATH is composed.
     */
    required_env?: Array<{ name: string; must_exist_path?: boolean; guidance?: string }>;
  };
  /** Custom scaffold command (e.g., "duct.exe new {app_name}"). Replaces dotnet new winui. */
  scaffold_command?: string;
  /** Custom build command. Replaces MSBuild/dotnet build. */
  build_command?: string;
  /** Custom launch command (e.g., "npm start"). Replaces default winapp flow. */
  launch_command?: string;
  /** App name for window detection when using launch_command. */
  launch_detect?: string;
  /** Framework hint appended to prompt if not already present (e.g., "WinUI 3"). */
  framework_hint?: string;
  /** Launch mode: "packaged" (default) or "unpackaged" (direct exe launch). */
  launch_mode?: "packaged" | "unpackaged";
  /** Extra text appended to the prompt for this agent setup. */
  prompt_addendum?: string;
  /** Sections for slot-based agent assembly. */
  sections?: string[];
  /** Root directory for section .md files. */
  sections_root?: string;
  /** Whether to inline skill content into agent.md. */
  inline_skills?: boolean;
  /** When false, skip appending condition+iteration suffix to the app name (default: true). */
  unique_app_name?: boolean;
  /** v2: Path to a pre-built agent.md file (relative to repo root). Skips section composition. */
  agent?: string;
}

export interface AgentSetupInfo {
  name: string;
  path: string;  // path to the agent variant folder (agents/<name>/)
  config?: AgentSetupConfig;
}

/**
 * Per-trial upstream provenance recorded into `results.json` when the agent
 * setup was materialized from a GitHub repo via `from_repo`.
 */
export interface AgentSource {
  type: "from_repo" | "local";
  repo: string;
  ref: string;       // human-friendly ref as requested ("main", "pr-123", "v1.2.0", or 40-char SHA)
  sha: string;       // resolved 40-char SHA of the checkout actually used
  /** Legacy single-plugin mode; comma-joined subpaths in plugin mode. */
  plugin_subpath: string;
  /** Skill names the plugin(s) shipped at this SHA (absent in older results). */
  skills?: string[];
  /** Per-plugin provenance (absent in older results). `repo`/`sha` differ from the top level in catalog mode. */
  plugins?: Array<{ subpath: string; name?: string; version?: string; skills: string[]; repo?: string; sha?: string }>;
  /** Catalog mode: catalog path inside `repo` (at `sha`) the plugins were resolved from. */
  catalog?: string;
  /** "agent" = custom agent + skill dirs (legacy); "plugin" = SDK pluginDirectories. */
  load_mode?: "agent" | "plugin";
  /** Local mode: the plugin folders had uncommitted changes. */
  dirty?: boolean;
}

export type ViewName = "setup" | "live" | "progress" | "results" | "charts" | "summary";
