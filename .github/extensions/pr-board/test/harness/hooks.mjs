// Run the extension outside the Copilot CLI: swap in a fake SDK, and a fake `gh` for github.mjs.
// The shipped code is untouched; these hooks only apply inside the test process.
import { registerHooks } from "node:module";

const SDK = new URL("./fake-sdk.mjs", import.meta.url).href;
const CHILD = new URL("./fake-child-process.mjs", import.meta.url).href;

registerHooks({
    resolve(specifier, context, next) {
        if (specifier === "@github/copilot-sdk/extension") return { url: SDK, shortCircuit: true };
        if ((specifier === "node:child_process" || specifier === "child_process") && /\/pr-board\/github\.mjs$/.test(context.parentURL || "")) {
            return { url: CHILD, shortCircuit: true };
        }
        return next(specifier, context);
    },
});
