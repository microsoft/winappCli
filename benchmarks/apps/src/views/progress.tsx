import React, { useState, useMemo, useEffect } from "react";
import { Box, Text, useInput, useStdout } from "ink";
import type { RunEntry } from "../types.js";
import { getGrade } from "../components/grades.js";
import { useNow, useRunClock } from "../components/clock.js";

const ACTIVE = ["setup", "building", "build_done", "dotnet_build", "launching", "validating", "retrospective"];

interface Props {
  entries: RunEntry[];
  runName: string;
  runStart: Date | null;
  runEnd: number | null;
  isRunning?: boolean;
  onRerun?: (entryIds: string[]) => void;
  onRevalidate?: (entryIds: string[]) => void;
}

export function ProgressView({ entries, runName, runStart, runEnd, isRunning, onRerun, onRevalidate }: Props) {
  const elapsed = useRunClock(runStart, runEnd);
  const completed = entries.filter(e => ["done", "failed", "interrupted", "timeout"].includes(e.status)).length;
  // Allow selection when nothing is actively running (all done, or loaded run with queued entries)
  const noneActive = entries.length > 0 && !entries.some(e => ACTIVE.includes(e.status));
  // Redraw once a second while trials run so their timers move.
  const now = useNow(1000, !noneActive);
  const allDone = noneActive && !isRunning;
  const [selectedForRerun, setSelectedForRerun] = useState<Set<string>>(new Set());
  const [cursorIndex, setCursorIndex] = useState(0);

  // Fix #1: Clamp cursorIndex when entries shrink
  useEffect(() => {
    if (entries.length > 0 && cursorIndex >= entries.length) {
      setCursorIndex(entries.length - 1);
    }
  }, [entries.length, cursorIndex]);

  // Fix #2: Prune stale IDs from selectedForRerun when entries change
  useEffect(() => {
    const validIds = new Set(entries.map(e => e.id));
    setSelectedForRerun(prev => {
      const pruned = new Set([...prev].filter(id => validIds.has(id)));
      return pruned.size !== prev.size ? pruned : prev;
    });
  }, [entries]);

  // Virtual scrolling: compute visible window around cursor
  const { stdout } = useStdout();
  const termRows = stdout?.rows || 30;
  // Account for ALL vertical overhead:
  //   StatusBar (app.tsx): 3 lines (border-top, content, border-bottom)
  //   Bottom help bar (app.tsx): 1 line
  //   ProgressView padding={1}: 2 lines (top + bottom)
  //   Header box with border: 4 lines (border-top, title, guide text, border-bottom)
  //   Column headers + separator: 2 lines
  //   Scroll indicators: 2 lines (above + below, worst case)
  const totalOverhead = 14;
  const maxVisible = Math.max(5, termRows - totalOverhead);

  const { visibleEntries, scrollTop } = useMemo(() => {
    const total = entries.length;
    if (total <= maxVisible) {
      return { visibleEntries: entries.map((e, i) => ({ entry: e, index: i })), scrollTop: 0 };
    }
    // Keep cursor roughly centered, clamped to bounds
    let top = Math.max(0, cursorIndex - Math.floor(maxVisible / 2));
    top = Math.min(top, total - maxVisible);
    const slice = entries.slice(top, top + maxVisible).map((e, j) => ({ entry: e, index: top + j }));
    return { visibleEntries: slice, scrollTop: top };
  }, [entries, cursorIndex, maxVisible]);

  useInput((input, key) => {
    if (!allDone) return;

    if (key.upArrow) {
      setCursorIndex(i => Math.max(0, i - 1));
    } else if (key.downArrow) {
      setCursorIndex(i => Math.min(entries.length - 1, i + 1));
    } else if (key.pageUp || input === "[") {
      // Fix #6: Page navigation
      setCursorIndex(i => Math.max(0, i - maxVisible));
    } else if (key.pageDown || input === "]") {
      setCursorIndex(i => Math.min(entries.length - 1, i + maxVisible));
    } else if (input === "h") {
      // Home — jump to top
      setCursorIndex(0);
    } else if (input === "e") {
      // End — jump to bottom
      setCursorIndex(entries.length - 1);
    } else if (input === " ") {
      // Toggle selection
      setSelectedForRerun(prev => {
        const next = new Set(prev);
        const id = entries[cursorIndex]?.id;
        if (id) next.has(id) ? next.delete(id) : next.add(id);
        return next;
      });
    } else if (input === "a" || input === "A") {
      // Toggle select all / deselect all
      setSelectedForRerun(prev => {
        if (prev.size === entries.length) return new Set();
        return new Set(entries.map(e => e.id));
      });
    } else if (input === "i" || input === "I") {
      setSelectedForRerun(new Set(
        entries
          .filter((entry) => entry.status === "queued" || entry.status === "interrupted")
          .map((entry) => entry.id)
      ));
    } else if (input === "c" || input === "C") {
      setSelectedForRerun(new Set(
        entries
          .filter((entry) =>
            (entry.status === "failed" && !entry.failReason?.startsWith("sdk_session_failed")) ||
            entry.status === "timeout" ||
            (entry.status === "done" && (entry.score ?? 100) < 50)
          )
          .map((entry) => entry.id)
      ));
    } else if (input === "s" || input === "S") {
      setSelectedForRerun(new Set(
        entries
          .filter((entry) => entry.failReason?.startsWith("sdk_session_failed"))
          .map((entry) => entry.id)
      ));
    } else if (input === "r" || input === "R") {
      if (selectedForRerun.size > 0 && onRerun) {
        onRerun([...selectedForRerun]);
        setSelectedForRerun(new Set());
      }
    } else if (input === "v" || input === "V") {
      if (selectedForRerun.size > 0 && onRevalidate) {
        onRevalidate([...selectedForRerun]);
        setSelectedForRerun(new Set());
      }
    }
  });

  return (
    <Box flexDirection="column" padding={1}>
      <Box borderStyle="single" borderColor="magenta" paddingX={1} flexDirection="column">
        <Text color="magenta" bold>
          RUN: {runName}  |  Progress: {completed}/{entries.length}  |  Elapsed: {elapsed}
        </Text>
        {allDone && onRerun && (
          <Text color="gray">
            ↑↓ navigate | I: incomplete | C: recoverable | S: SDK failures | R: rerun | V: revalidate | Selected: {selectedForRerun.size}
          </Text>
        )}
      </Box>
      <Box flexDirection="column" marginTop={1}>
        <Text color="gray">
          {"  "}{pad("Scenario", 22)} {pad("AgentSetup", 32)} {pad("Model", 12)} Status
        </Text>
        <Text color="gray">
          {"  "}{"─".repeat(100)}
        </Text>
        {scrollTop > 0 && <Text color="gray">  ↑ {scrollTop} more above</Text>}
        {visibleEntries.map(({ entry, index: i }) => {
          const shortModel = entry.model.replace("claude-", "");
          const { text: statusText, color } = getStatusDisplay(entry, now);
          const isSelected = selectedForRerun.has(entry.id);
          const isCursor = allDone && i === cursorIndex;
          const prefix = isCursor ? (isSelected ? "✓▸" : " ▸") : (isSelected ? "✓ " : "  ");
          const lineNum = String(i + 1).padStart(3);
          const rowColor = isCursor ? "white" : isSelected ? "yellow" : color;
          return (
            <Text key={entry.id} color={rowColor} bold={isCursor} inverse={isCursor}>
              {prefix}{lineNum}. {pad(entry.scenario, 20)} {pad(entry.condition, 32)} {pad(shortModel, 12)} {statusText}
            </Text>
          );
        })}
        {scrollTop + maxVisible < entries.length && <Text color="gray">  ↓ {entries.length - scrollTop - maxVisible} more below</Text>}
      </Box>
    </Box>
  );
}

function pad(s: string, len: number): string {
  return s.length >= len ? s.slice(0, len) : s + " ".repeat(len - s.length);
}

function getStatusDisplay(entry: RunEntry, now: number): { text: string; color: string } {
  switch (entry.status) {
    case "queued": return { text: "⏳ Queued", color: "gray" };
    case "setup": return { text: `🔧 Setup... ${runElapsed(entry, now)}`, color: "cyan" };
    case "building": {
      const tokens = entry.tokenDisplay || (entry.outputTokens ? entry.outputTokens + " out" : "");
      return { text: `🔄 Coding... ${runElapsed(entry, now)}${tokens ? " [" + tokens + "]" : ""}`, color: "cyan" };
    }
    case "build_done": return { text: `📦 Built ${runElapsed(entry, now)}`, color: "cyan" };
    case "dotnet_build": return { text: `🔨 Compiling... ${runElapsed(entry, now)}`, color: "cyan" };
    case "launching": return { text: `🚀 Launching... ${runElapsed(entry, now)}`, color: "cyan" };
    case "validating": {
      const tokens = entry.tokenDisplay || (entry.outputTokens ? entry.outputTokens + " out" : "");
      return { text: `🔍 Validating... ${runElapsed(entry, now)}${tokens ? " [" + tokens + "]" : ""}`, color: "cyan" };
    }
    case "retrospective": return { text: `📝 Retrospective... ${runElapsed(entry, now)}`, color: "cyan" };
    case "done": {
      const breakdown = entry.qualityBreakdown ? ` (${entry.qualityBreakdown})` : "";
      const score = entry.score != null ? `${entry.score}/100${breakdown}` : "—";
      const grade = entry.score != null ? getGrade(entry.score) : { letter: "—", color: "gray" };
      const time = entry.sessionTime || (entry.finishedAt && entry.startedAt
        ? formatElapsed(entry.finishedAt.getTime() - entry.startedAt.getTime())
        : "—");
      const tokens = entry.inputTokens || "";
      const subTok = entry.subAgentInputTokens ? ` (sub:${entry.subAgentInputTokens})` : "";
      const pr = entry.credits ? `, ${entry.credits} AIU` : "";
      return { text: `${grade.letter} ${score} (${time}${tokens ? ", " + tokens + subTok : ""}${pr})`, color: grade.color };
    }
    case "failed": {
      const time = entry.sessionTime || runElapsed(entry, now);
      return { text: `❌ ${entry.failReason || "Failed"} (${time})`, color: "red" };
    }
    case "timeout": return { text: `⏰ Timeout (${runElapsed(entry, now)})`, color: "red" };
    case "interrupted": return { text: "⚠ Interrupted — select and rerun", color: "yellow" };
    default: return { text: entry.status, color: "white" };
  }
}

function runElapsed(entry: RunEntry, now: number): string {
  if (!entry.startedAt) return "";
  const end = entry.finishedAt?.getTime() ?? now;
  return formatElapsed(end - entry.startedAt.getTime());
}

function formatElapsed(ms: number): string {
  const totalSec = Math.floor(ms / 1000);
  if (totalSec < 60) return `${totalSec}s`;
  const m = Math.floor(totalSec / 60);
  const s = totalSec % 60;
  if (m < 60) return `${m}m ${s}s`;
  const h = Math.floor(m / 60);
  return `${h}h ${m % 60}m`;
}
