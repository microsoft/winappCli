import React from "react";
import { Box, Text } from "ink";
import Spinner from "ink-spinner";
import { LogPanel } from "../components/log-panel.js";
import { getGrade } from "../components/grades.js";
import { useNow } from "../components/clock.js";
import type { RunEntry } from "../types.js";

interface Props {
  selectedRun?: RunEntry;
  output: string;
  scrollOffset: number;
  runIndex: number;
  totalRuns: number;
}

export function LiveView({ selectedRun, output, scrollOffset, runIndex, totalRuns }: Props) {
  const ticking = !!selectedRun?.startedAt && !selectedRun.finishedAt &&
    ["setup", "building", "dotnet_build", "launching", "validating", "retrospective"].includes(selectedRun.status);
  const now = useNow(1000, ticking);
  if (!selectedRun) {
    return (
      <Box padding={1}>
        <Text color="gray">Waiting for first run to start...</Text>
      </Box>
    );
  }

  const statusText: Record<string, string> = {
    queued: "Queued",
    setup: "Setting up...",
    building: "Building with Copilot...",
    build_done: "Build complete",
    dotnet_build: "Running dotnet build...",
    launching: "Launching app...",
    validating: "Validating with Copilot...",
    retrospective: "Running retrospective...",
    done: "Complete",
    failed: "Failed",
    timeout: "Timed out"
  };

  const displayStatus = statusText[selectedRun.status] || selectedRun.status;
  const isActive = ["setup", "building", "dotnet_build", "launching", "validating", "retrospective"].includes(selectedRun.status);
  const isDone = ["done", "failed", "interrupted", "timeout"].includes(selectedRun.status);

  // Calculate elapsed for this run
  const runElapsedMs = selectedRun.startedAt
    ? (selectedRun.finishedAt?.getTime() ?? now) - selectedRun.startedAt.getTime()
    : 0;
  const runElapsedStr = runElapsedMs > 0 ? formatRunElapsed(runElapsedMs) : "";

  return (
    <Box flexDirection="column" flexGrow={1}>
      <Box paddingX={1} gap={1}>
        {isActive && <Spinner type="dots" />}
        <Text color="gray">[{runIndex + 1}/{totalRuns}]</Text>
        <Text color="cyan" bold>{selectedRun.scenario}</Text>
        <Text color="gray">/</Text>
        <Text color="yellow">{selectedRun.condition}</Text>
        <Text color="gray">/</Text>
        <Text>{selectedRun.model.replace("claude-", "")}</Text>
        <Text color="gray">—</Text>
        <Text color={isActive ? "cyan" : selectedRun.status === "done" ? "green" : "red"}>{displayStatus}</Text>
        {runElapsedStr && <Text color="gray">({runElapsedStr})</Text>}
        {(selectedRun.tokenDisplay || selectedRun.outputTokens) && (
          <Text color="magenta"> [{selectedRun.tokenDisplay || selectedRun.outputTokens + " out"}]</Text>
        )}
        {isDone && selectedRun.score != null && (() => {
          const grade = getGrade(selectedRun.score!);
          return <Text color={grade.color}> {grade.letter} {selectedRun.score}/100</Text>;
        })()}
      </Box>
      <Box paddingX={1}>
        <Text color="gray" dimColor>←/→ switch runs | ↑↓ scroll | Home/End jump | PgUp/PgDn page</Text>
      </Box>
      <LogPanel
        output={output}
        maxLines={process.stdout.rows ? process.stdout.rows - 8 : 28}
        scrollOffset={scrollOffset}
      />
    </Box>
  );
}

function formatRunElapsed(ms: number): string {
  const totalSec = Math.floor(ms / 1000);
  if (totalSec < 60) return `${totalSec}s`;
  const m = Math.floor(totalSec / 60);
  const s = totalSec % 60;
  if (m < 60) return `${m}m ${s}s`;
  const h = Math.floor(m / 60);
  return `${h}h ${m % 60}m`;
}
