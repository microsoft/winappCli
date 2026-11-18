import React, { useMemo } from "react";
import { Box, Text } from "ink";

interface Props {
  output: string;
  maxLines?: number;
  scrollOffset?: number;
}

export function LogPanel({ output, maxLines = 30, scrollOffset = 0 }: Props) {
  const allLines = useMemo(() => {
    const wanted = Math.max(maxLines + scrollOffset + 1, maxLines * 2);
    const lines: string[] = [];
    let end = output.length;
    while (end > 0 && lines.length < wanted) {
      const start = output.lastIndexOf("\n", end - 1);
      lines.push(output.slice(start + 1, end));
      end = start;
    }
    return lines.reverse();
  }, [output, maxLines, scrollOffset]);
  const allLineCount = allLines.length;

  const { visibleLines, startLine, endLine } = useMemo(() => {
    if (scrollOffset > 0) {
      const end = Math.max(0, allLineCount - scrollOffset);
      const start = Math.max(0, end - maxLines);
      return { visibleLines: allLines.slice(start, end), startLine: start + 1, endLine: end };
    }
    const start = Math.max(0, allLineCount - maxLines);
    return { visibleLines: allLines.slice(-maxLines), startLine: start + 1, endLine: allLineCount };
  }, [allLines, allLineCount, maxLines, scrollOffset]);

  const isAtBottom = scrollOffset === 0;
  const isAtTop = startLine <= 1;

  return (
    <Box flexDirection="column" flexGrow={1}>
      {!isAtBottom && !isAtTop && (
        <Text color="gray" dimColor>  ↑ {startLine - 1} more lines — PgUp/↑ to scroll, Home to jump to top</Text>
      )}
      {!isAtBottom && isAtTop && (
        <Text color="gray" dimColor>  ── top of log ──</Text>
      )}
      {visibleLines.map((line, i) => (
        <Text key={i} wrap="wrap">{line}</Text>
      ))}
      {!isAtBottom && (
        <Text color="gray" dimColor>  ── lines {startLine}–{endLine} of {allLineCount} — End to jump to bottom ──</Text>
      )}
    </Box>
  );
}
