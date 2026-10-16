import React from "react";
import { render } from "ink";
import { App } from "./app.js";
import { DEFAULT_BUILD_MODEL } from "./runner/config.js";

const args = process.argv.slice(2);
const showResults = args.includes("--results");
const runNameIdx = args.indexOf("--run");
const runName = runNameIdx >= 0 ? args[runNameIdx + 1] : undefined;
const maxBuildIdx = args.indexOf("--max-build-minutes");
const maxBuildMinutes = maxBuildIdx >= 0 ? parseInt(args[maxBuildIdx + 1]) : 60;
const concurrencyIdx = args.indexOf("--concurrency");
const concurrency = concurrencyIdx >= 0 ? parseInt(args[concurrencyIdx + 1]) : 3;

// Quick-run mode: --scenario X --agent Y [--model Z] skips the setup wizard
const scenarioIdx = args.indexOf("--scenario");
const agentIdx = args.indexOf("--agent");
const modelIdx = args.indexOf("--model");
const quickScenario = scenarioIdx >= 0 ? args[scenarioIdx + 1] : undefined;
const quickAgent = agentIdx >= 0 ? args[agentIdx + 1] : undefined;
const quickModel = modelIdx >= 0 ? args[modelIdx + 1] : DEFAULT_BUILD_MODEL;

render(
  <App
    showResultsOnly={showResults}
    runName={runName}
    maxBuildMinutes={maxBuildMinutes}
    concurrency={concurrency}
    quickRun={quickScenario && quickAgent ? { scenario: quickScenario, agent: quickAgent, model: quickModel } : undefined}
  />
);
