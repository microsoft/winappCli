// TUI entry point. Sets the environment before React/Ink load: React picks its
// development or production build from NODE_ENV at import time, and the
// development build renders large logs several times slower.
const args = process.argv.slice(2);
process.env.NODE_ENV ??= "production";
const judgeIdx = args.indexOf("--judge-model");
if (judgeIdx >= 0 && args[judgeIdx + 1]) process.env.BENCH_JUDGE_MODEL = args[judgeIdx + 1];

await import("./index.js");

export {};
