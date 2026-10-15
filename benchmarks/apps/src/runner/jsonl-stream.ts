import { closeSync, openSync, readSync } from "fs";
import { StringDecoder } from "string_decoder";

export function* readLinesSync(path: string, chunkBytes = 64 * 1024): Generator<string> {
  const fd = openSync(path, "r");
  const chunk = Buffer.allocUnsafe(chunkBytes);
  // Stateful decoder: a multi-byte character split across two reads stays intact.
  const decoder = new StringDecoder("utf8");
  let carry = "";
  try {
    while (true) {
      const bytes = readSync(fd, chunk, 0, chunk.length, null);
      if (bytes === 0) break;
      const text = carry + decoder.write(chunk.subarray(0, bytes));
      const lines = text.split("\n");
      carry = lines.pop() || "";
      for (const line of lines) yield line.endsWith("\r") ? line.slice(0, -1) : line;
    }
    carry += decoder.end();
    if (carry) yield carry.endsWith("\r") ? carry.slice(0, -1) : carry;
  } finally {
    closeSync(fd);
  }
}
