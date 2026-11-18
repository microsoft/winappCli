import React from "react";
import { Box, Text } from "ink";

interface DataPoint {
  x: number;
  y: number;
  label: string;
  color?: string;
}

interface Props {
  data: DataPoint[];
  width?: number;
  height?: number;
  xLabel?: string;
  yLabel?: string;
  title?: string;
}

export function ScatterPlot({
  data,
  width = 60,
  height = 15,
  xLabel = "Tokens",
  yLabel = "Score",
  title,
}: Props) {
  if (data.length === 0) {
    return <Text color="gray">No data to plot</Text>;
  }

  // Calculate bounds with padding
  const xValues = data.map((d) => d.x);
  const yValues = data.map((d) => d.y);
  const xMin = 0;
  const xMax = Math.max(...xValues) * 1.1 || 1;
  const yMin = 0;
  const yMax = Math.max(...yValues, 100);

  // Build the grid
  const plotWidth = width - 8; // leave room for y-axis labels
  const plotHeight = height - 3; // leave room for x-axis labels

  // Place points on grid
  const grid: Map<string, DataPoint> = new Map();
  for (const point of data) {
    const col = Math.round(((point.x - xMin) / (xMax - xMin)) * (plotWidth - 1));
    const row = Math.round(
      (1 - (point.y - yMin) / (yMax - yMin)) * (plotHeight - 1)
    );
    const clampedCol = Math.max(0, Math.min(plotWidth - 1, col));
    const clampedRow = Math.max(0, Math.min(plotHeight - 1, row));
    grid.set(`${clampedRow},${clampedCol}`, point);
  }

  // Render — build rows with per-cell color info
  interface PlotCell { char: string; color: string }
  const rows: Array<{ yLabel: string; cells: PlotCell[] }> = [];

  for (let row = 0; row < plotHeight; row++) {
    let yLabelStr = "      ";
    if (row === 0) yLabelStr = formatTokens(yMax).padStart(6);
    else if (row === plotHeight - 1) yLabelStr = formatTokens(yMin).padStart(6);
    else if (row === Math.floor(plotHeight / 2))
      yLabelStr = formatTokens(Math.round((yMax + yMin) / 2)).padStart(6);

    const cells: PlotCell[] = [{ char: "│", color: "gray" }];
    for (let col = 0; col < plotWidth; col++) {
      const key = `${row},${col}`;
      const point = grid.get(key);
      if (point) {
        cells.push({ char: "●", color: point.color || "green" });
      } else {
        cells.push({ char: " ", color: "gray" });
      }
    }
    rows.push({ yLabel: yLabelStr, cells });
  }

  // Build legend entries
  const legend = data.map((d) => ({
    label: d.label,
    color: d.color || "white",
    x: d.x,
    y: d.y,
  }));

  // Format x-axis values
  const xMinStr = formatTokens(xMin);
  const xMaxStr = formatTokens(xMax);
  const xMidStr = formatTokens((xMin + xMax) / 2);

  return (
    <Box flexDirection="column">
      {title && (
        <Text bold color="cyan">
          {"  "}{title}
        </Text>
      )}
      <Box>
        <Text color="gray" dimColor>
          {yLabel}
        </Text>
      </Box>
      {rows.map((row, i) => (
        <Box key={i}>
          <Text color="gray">{row.yLabel}</Text>
          {row.cells.map((cell, j) => (
            <Text key={j} color={cell.color as any}>{cell.char}</Text>
          ))}
          {i < legend.length && (
            <Text color={legend[i].color as any}>
              {"  "}● {legend[i].label} (score:{legend[i].x}, {formatTokens(legend[i].y)})
            </Text>
          )}
        </Box>
      ))}
      {/* X-axis */}
      <Box>
        <Text color="gray">{"      "}└{"─".repeat(plotWidth)}</Text>
      </Box>
      <Box>
        <Text color="gray">
          {"       "}{xMinStr}
          {" ".repeat(Math.max(1, Math.floor(plotWidth / 2) - xMinStr.length - Math.floor(xMidStr.length / 2)))}{xMidStr}
          {" ".repeat(Math.max(1, plotWidth - Math.floor(plotWidth / 2) - Math.ceil(xMidStr.length / 2) - xMaxStr.length))}{xMaxStr}
        </Text>
      </Box>
      <Box justifyContent="center">
        <Text color="gray" dimColor>
          {xLabel}
        </Text>
      </Box>
    </Box>
  );
}

function formatTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(0)}K`;
  return String(Math.round(n));
}

// Helper to parse token strings like "2.4m", "137.5k" to numbers
// (re-exported from runner/format.ts so non-UI consumers don't import Ink)
export { parseTokenString } from "../runner/format.js";

// Color palette for conditions — each must be unique
const CONDITION_COLORS: Record<string, string> = {
  bare: "white",
  starter: "yellow",
  "agentsetup-minimal": "green",
  "agentsetup-single-agent": "cyan",
  "agentsetup-lite-orchestrator": "magenta",
  "agentsetup-mcp-first": "blue",
  "agentsetup-winmd-first": "redBright",
  "agentsetup-current": "red",
};

// Fallback colors for unknown conditions
const FALLBACK_COLORS = ["greenBright", "yellowBright", "cyanBright", "magentaBright", "blueBright"];
let fallbackIdx = 0;

export function getConditionColor(condition: string): string {
  // Strip iteration suffix like " [1/2]"
  const base = condition.replace(/\s*\[\d+\/\d+\]$/, "");
  if (CONDITION_COLORS[base]) return CONDITION_COLORS[base];
  // Assign a unique fallback color
  if (!CONDITION_COLORS[base]) {
    CONDITION_COLORS[base] = FALLBACK_COLORS[fallbackIdx % FALLBACK_COLORS.length];
    fallbackIdx++;
  }
  return CONDITION_COLORS[base];
}
