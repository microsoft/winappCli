import React from "react";
import { Box, Text } from "ink";
import { existsSync, readFileSync } from "fs";
import { join } from "path";
import type { RunEntry } from "../types.js";
import { aggregateEntries } from "../runner/aggregate.js";
import { getGrade } from "../components/grades.js";

interface SummaryData {
  rankings?: Array<{
    condition: string;
    avg_score: number;
    avg_time_minutes: number;
    summary: string;
  }>;
  condition_analysis?: Array<{
    condition: string;
    strengths: string[];
    weaknesses: string[];
    best_model: string;
    notes: string;
  }>;
  common_issues?: string[];
  model_comparison?: { opus: string; sonnet: string };
  recommendations?: string[];
  overall_summary?: string;
}

interface Props {
  entries: RunEntry[];
  runDir?: string;
}

export function SummaryView({ entries, runDir }: Props) {
  const aggregated = aggregateEntries(entries);
  const allDone =
    entries.length > 0 &&
    entries.every((e) => ["done", "failed", "interrupted", "timeout"].includes(e.status));

  // Try to load AI-generated summary
  let summary: SummaryData | null = null;
  if (runDir) {
    const summaryPath = join(runDir, "summary.json");
    if (existsSync(summaryPath)) {
      try {
        summary = JSON.parse(readFileSync(summaryPath, "utf-8"));
      } catch {}
    }
  }

  if (aggregated.length === 0) {
    return (
      <Box padding={1}>
        <Text color="gray">No completed results yet.</Text>
      </Box>
    );
  }

  return (
    <Box flexDirection="column" padding={1}>
      {/* Quick stats */}
      <Box borderStyle="single" borderColor="cyan" paddingX={1} flexShrink={0}>
        <Text color="cyan" bold>BENCHMARK SUMMARY</Text>
      </Box>

      <Box flexDirection="column" marginTop={1} paddingX={1} flexShrink={0}>
        <Text bold color="white">Quick Stats</Text>
        <Text color="gray">
          {"  "}Agent setups tested: {[...new Set(aggregated.map((a) => a.condition))].length}
        </Text>
        <Text color="gray">
          {"  "}Scenarios: {[...new Set(aggregated.map((a) => a.scenario))].join(", ")}
        </Text>
        <Text color="gray">
          {"  "}Models: {[...new Set(aggregated.map((a) => a.model.replace("claude-", "")))].join(", ")}
        </Text>
        <Text color="gray">
          {"  "}Total runs: {entries.length} ({entries.filter((e) => e.status === "done").length} passed, {entries.filter((e) => e.status === "failed").length} failed, {entries.filter((e) => e.status === "timeout").length} timeout)
        </Text>
        <Text color="gray">
          {"  "}Total credits used (AIU): {Math.round(entries.reduce((sum, e) => sum + (e.credits ?? 0), 0) * 10) / 10}
        </Text>
      </Box>

      {/* Rankings from data */}
      <Box flexDirection="column" marginTop={1} paddingX={1} flexShrink={0}>
        <Text bold color="yellow">Rankings (by avg score)</Text>
        {aggregated.map((agg, i) => {
          const grade = getGrade(agg.avgScore);
          const scoreRange =
            agg.iterations > 1
              ? `${agg.avgScore}/100 (${agg.minScore}-${agg.maxScore})`
              : `${agg.avgScore}/100`;
          return (
            <Text key={i} color={grade.color}>
              {"  "}{i + 1}. {pad(grade.letter, 4)} {pad(agg.condition, 28)} {pad(agg.model.replace("claude-", ""), 12)} {pad(scoreRange, 20)} {pad(agg.avgInputTokens || "\u2014", 8)} {pad(agg.avgCredits != null ? `${agg.avgCredits} AIU` : "\u2014", 12)}
            </Text>
          );
        })}
      </Box>

      {/* AI-generated analysis */}
      {summary ? (
        <Box flexDirection="column" marginTop={1} flexShrink={0}>
          <Box borderStyle="single" borderColor="green" paddingX={1} flexShrink={0}>
            <Text color="green" bold>AI ANALYSIS (Opus)</Text>
          </Box>

          {summary.overall_summary && (
            <Box marginTop={1} paddingX={2} flexShrink={0}>
              <Text color="white" bold wrap="wrap">
                {summary.overall_summary}
              </Text>
            </Box>
          )}

          {summary.condition_analysis && summary.condition_analysis.length > 0 && (
            <Box flexDirection="column" marginTop={1} paddingX={1} flexShrink={0}>
              <Text bold color="cyan">Per-Condition Analysis</Text>
              {summary.condition_analysis.map((ca, i) => (
                <Box key={i} flexDirection="column" marginTop={1} paddingX={1} flexShrink={0}>
                  <Text bold color="yellow">
                    {"  "}{ca.condition} {ca.best_model ? `(best with ${ca.best_model})` : ""}
                  </Text>
                  {ca.strengths?.map((s, j) => (
                    <Text key={`s${j}`} color="green" wrap="wrap">{"    "}✓ {s}</Text>
                  ))}
                  {ca.weaknesses?.map((w, j) => (
                    <Text key={`w${j}`} color="red" wrap="wrap">{"    "}✗ {w}</Text>
                  ))}
                  {ca.notes && (
                    <Text color="gray" wrap="wrap">{"    "}{ca.notes}</Text>
                  )}
                </Box>
              ))}
            </Box>
          )}

          {summary.common_issues && summary.common_issues.length > 0 && (
            <Box flexDirection="column" marginTop={1} paddingX={1} flexShrink={0}>
              <Text bold color="red">Common Issues</Text>
              {summary.common_issues.map((issue, i) => (
                <Text key={i} color="red" wrap="wrap">{"  "}• {issue}</Text>
              ))}
            </Box>
          )}

          {summary.model_comparison && (
            <Box flexDirection="column" marginTop={1} paddingX={1} flexShrink={0}>
              <Text bold color="magenta">Model Comparison</Text>
              {summary.model_comparison.opus && (
                <Text color="gray" wrap="wrap">{"  "}Opus: {summary.model_comparison.opus}</Text>
              )}
              {summary.model_comparison.sonnet && (
                <Text color="gray" wrap="wrap">{"  "}Sonnet: {summary.model_comparison.sonnet}</Text>
              )}
            </Box>
          )}

          {summary.recommendations && summary.recommendations.length > 0 && (
            <Box flexDirection="column" marginTop={1} paddingX={1} flexShrink={0}>
              <Text bold color="green">Recommendations</Text>
              {summary.recommendations.map((rec, i) => (
                <Text key={i} color="green" wrap="wrap">{"  "}{i + 1}. {rec}</Text>
              ))}
            </Box>
          )}
        </Box>
      ) : (
        <Box marginTop={1} paddingX={1}>
          <Text color="gray" dimColor>
            {allDone
              ? "AI summary is being generated..."
              : "AI summary will be generated after all runs complete."}
          </Text>
        </Box>
      )}
    </Box>
  );
}

function pad(s: string, len: number): string {
  return s.length >= len ? s.slice(0, len) : s + " ".repeat(len - s.length);
}
