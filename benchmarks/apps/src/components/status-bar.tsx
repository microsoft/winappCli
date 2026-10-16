import React from "react";
import { Box, Text } from "ink";
import type { RunEntry, ViewName } from "../types.js";
import { useRunClock } from "./clock.js";

interface Props {
  currentRun?: RunEntry;
  progress: { current: number; total: number };
  runStart: Date | null;
  /** Epoch ms of the last finished trial once the whole run is done; freezes the clock. */
  runEnd: number | null;
  activeView: ViewName;
  runName?: string;
  memory?: { heapMb: number; rssMb: number; visibleKb: number };
}

export function StatusBar({ currentRun, progress, runStart, runEnd, activeView, runName, memory }: Props) {
  const elapsed = useRunClock(runStart, runEnd);
  return (
    <Box borderStyle="single" borderColor="magenta" paddingX={1}>
      <Box flexGrow={1}>
        {runName && <Text color="magenta" bold>{runName} </Text>}
        <Text color="magenta" bold>
          [{progress.current}/{progress.total}]
        </Text>
        {currentRun && (
          <Text color="cyan">
            {" "}{currentRun.scenario} / {currentRun.condition} / {currentRun.model.replace("claude-", "")}
          </Text>
        )}
        <Text color="gray"> | {elapsed}</Text>
        {memory && (
          <Text color={memory.heapMb >= 384 ? "yellow" : "gray"}>
            {" "}| heap {memory.heapMb}MB rss {memory.rssMb}MB view {memory.visibleKb}KB
          </Text>
        )}
      </Box>
      <Box gap={1}>
        <Text color={activeView === "live" ? "green" : "gray"}>[1]Live</Text>
        <Text color={activeView === "progress" ? "green" : "gray"}>[2]Progress</Text>
        <Text color={activeView === "results" ? "green" : "gray"}>[3]Results</Text>
        <Text color={activeView === "charts" ? "green" : "gray"}>[4]Charts</Text>
        <Text color={activeView === "summary" ? "green" : "gray"}>[5]Summary</Text>
      </Box>
    </Box>
  );
}
