---
ms.custom: mslearn
---

# Use winapp from TypeScript or JavaScript

```powershell
npm install @microsoft/winappcli
```

The package includes the Windows CLI and typed functions for calling it from Node.js.
Run your script on Windows. Each command function launches the CLI, waits for it to
finish, and captures its output.

## Initialize a project

Run this script from your application's directory:

```typescript
import { init } from '@microsoft/winappcli';

const result = await init({ useDefaults: true });
console.log(result.stdout);
```

`init` creates the manifest and assets and sets up SDK packages and projections.
After cloning a project that already has `winapp.yaml`, use `restore()` instead.
See [initialization and restore](usage.md#init) for the CLI workflow.

To work in another existing directory, pass `cwd`. Paths passed to the command
are resolved in that directory, not relative to your script.

## Package build output

Build your application first, then package its output folder:

```typescript
import { certGenerate, packageApp } from '@microsoft/winappcli';

await certGenerate({ output: './devcert.pfx' });
const result = await packageApp({
  inputFolder: './dist',
  cert: './devcert.pfx',
  output: './MyApp.msix',
});
console.log(result.stdout);
```

The manifest must describe your executable and match the signing certificate's
publisher. The generated certificate is for local development only; it uses the
publicly known default password `password`. See [packaging](usage.md#pack),
[code signing](usage.md#sign), and the [Electron guides](guides/electron/index.md)
for preparing your application's package layout and trusting a development certificate.

## Find command functions and options

Use your editor's completion and the package's `.d.ts` declarations for the full
API of the version you installed. Command paths generally become camel-case
functions, and hyphenated options become camel-case properties:

| CLI | JavaScript/TypeScript |
|-----|-----------------------|
| `winapp init --use-defaults` | `init({ useDefaults: true })` |
| `winapp restore` | `restore()` |
| `winapp cert generate --output devcert.pfx` | `certGenerate({ output: 'devcert.pfx' })` |
| `winapp pack ./dist` | `packageApp({ inputFolder: './dist' })` |
| `winapp run MyApp.csproj` | `run({ input: 'MyApp.csproj' })` |
| `winapp ui inspect --app notepad` | `uiInspect({ app: 'notepad' })` |

`packageApp` is the function for packaging; it is not named `pack` or `package`.
See [CLI usage](usage.md) for each command's behavior, prerequisites, and examples.
Use `npx winapp <command> --help` for exact flags or `npx winapp --cli-schema`
for the installed CLI's complete machine-readable command tree.

Every command's options object also accepts these shared properties:

| Property | Use |
|----------|-----|
| `cwd` | Run in a specified existing directory. Defaults to the current working directory. |
| `quiet` | Suppress progress messages. |
| `verbose` | Include diagnostic output. |
| `signal` | Cancel the native process with an `AbortSignal`. |
| `workflowId` | Group related UI calls into one desktop workflow. |

## Read results and handle failures

Command functions return a promise containing `exitCode`, `stdout`, and `stderr`.
Successful calls have `exitCode: 0`. A nonzero CLI exit rejects the promise with
an `Error` carrying those same three properties; launch failures reject with an
error message, and cancellation rejects with an `AbortError`.

```typescript
import { packageApp } from '@microsoft/winappcli';

try {
  const result = await packageApp({ inputFolder: './dist', noSign: true });
  console.log(result.stdout);
} catch (error) {
  if (error instanceof Error) {
    console.error(error.message);
  }
  throw error;
}
```

If a call fails, inspect its error message and captured output, correct the
reported path, manifest, signing, or prerequisite problem, and retry.
The wrapper captures text; it does not automatically parse JSON output.
For commands that support `json`, request it and parse `stdout`:

```typescript
import { uiInspect } from '@microsoft/winappcli';

const result = await uiInspect({ app: 'notepad', json: true });
const tree: unknown = JSON.parse(result.stdout);
console.log(tree);
```

Open the target application before inspecting it.

`perfAnalyze` with `json: true` is the one exception to rejecting on a nonzero
exit. When the capture is missing some evidence, it resolves with `exitCode: 1`
and the usable results in `stdout`. Check `coverage.complete` before treating
the results as complete:

```typescript
import { perfAnalyze } from '@microsoft/winappcli';

const result = await perfAnalyze({ directory: './capture', json: true });
const evidence = JSON.parse(result.stdout);
if (!evidence.coverage.complete) {
  console.warn(result.stderr);
}
```

### Calls are non-interactive

Command wrappers capture output and use piped stdin, so commands cannot ask for
interactive input. Supply the options and credentials your command needs.
`init` and `newCommand` support defaults; other commands may fail if a required
selection is missing. For Azure signing, supply `metadataFile` or all of
`subscription`, `resourceGroup`, `account`, and `profile`, and authenticate
non-interactively beforehand. See [Azure signing](usage.md#az-sign).

## Cancel a call

```typescript
import { uiInspect } from '@microsoft/winappcli';

const controller = new AbortController();
const timeout = setTimeout(() => controller.abort(), 30_000);
try {
  await uiInspect({ app: 'notepad', signal: controller.signal });
} catch (error) {
  if (error instanceof Error && error.name === 'AbortError') {
    console.error('The inspection was cancelled.');
  }
  throw error;
} finally {
  clearTimeout(timeout);
}
```

Cancellation stops the whole native invocation, including a wait for the desktop.
On Windows it force-terminates the process, so cleanup may not run and completed
UI actions are not undone. Cancelling a recording can leave an incomplete video.

## Coordinate UI calls and record a bounded video

Desktop-sensitive commands coordinate automatically, even without `workflowId`.
Pass the same ID to related calls when they should retain the desktop between
commands. Give each independent workflow its own ID.

```typescript
import { randomUUID } from 'node:crypto';
import { uiInspect, uiYield } from '@microsoft/winappcli';

const workflowId = randomUUID();
try {
  const result = await uiInspect({ app: 'notepad', workflowId });
  console.log(result.stdout);
  // Use selectors from this inspection for subsequent UI calls with the same ID.
} finally {
  await uiYield({ workflowId });
}
```

`workflowId` is passed only to the child process; it does not change `process.env`.
See [coordinating concurrent UI workflows](ui-automation.md#coordinating-concurrent-ui-workflows)
for how desktop turns are shared.

`uiRecord` and `targetRecord` require a finite, positive `durationSec`:

```typescript
import { uiRecord } from '@microsoft/winappcli';

await uiRecord({
  app: 'notepad',
  durationSec: 5,
  output: './notepad.mp4',
});
```

Use the same `workflowId` for recording and concurrent UI actions when they belong
to one workflow. Do not use cancellation as normal recording completion.
See [UI recording](ui-automation.md) for capture options.

## Electron and Node-only tools

The package also exports `addElectronDebugIdentity`, `clearElectronDebugIdentity`,
`addMsixIdentityToExe`, and `execWithBuildTools`. These utilities have their own
result types; use the installed declarations and [Electron guides](guides/electron/index.md)
for their options and prerequisites.

Some npm CLI operations are not command-function exports. Use
[the `node` commands](usage.md#node-create-addon) for native addon scaffolding
and JavaScript binding generation.
