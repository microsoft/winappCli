import { closeSync, existsSync, fstatSync, openSync, readSync } from "fs";

export const LIVE_OUTPUT_MAX_CHARS = 100_000;
export const PROCESS_OUTPUT_MAX_CHARS = 1_000_000;

export function appendBounded(
  previous: string,
  data: string,
  maxChars = LIVE_OUTPUT_MAX_CHARS
): string {
  if (data.length >= maxChars) return data.slice(-maxChars);
  const overflow = previous.length + data.length - maxChars;
  return overflow > 0 ? previous.slice(overflow) + data : previous + data;
}

export function readFileTail(path: string, maxBytes = LIVE_OUTPUT_MAX_CHARS): string {
  if (!existsSync(path)) return "";
  const fd = openSync(path, "r");
  try {
    const size = fstatSync(fd).size;
    const length = Math.min(size, maxBytes);
    const buffer = Buffer.allocUnsafe(length);
    readSync(fd, buffer, 0, length, size - length);
    return buffer.toString("utf8");
  } finally {
    closeSync(fd);
  }
}
