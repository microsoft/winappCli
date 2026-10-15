import { closeSync, openSync, readSync } from "fs";

export function* readLinesSync(path: string, chunkBytes = 64 * 1024): Generator<string> {
  const fd = openSync(path, "r");
  const chunk = Buffer.allocUnsafe(chunkBytes);
  let carry = "";
  try {
    while (true) {
      const bytes = readSync(fd, chunk, 0, chunk.length, null);
      if (bytes === 0) break;
      const text = carry + chunk.toString("utf8", 0, bytes);
      const lines = text.split("\n");
      carry = lines.pop() || "";
      for (const line of lines) yield line.endsWith("\r") ? line.slice(0, -1) : line;
    }
    if (carry) yield carry.endsWith("\r") ? carry.slice(0, -1) : carry;
  } finally {
    closeSync(fd);
  }
}
