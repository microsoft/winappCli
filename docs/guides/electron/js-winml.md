<!-- mslearn: true -->
<!-- description: Run machine learning inference with Windows ML from Electron JavaScript using winapp JS bindings, with no native addon or node-gyp build step. -->
# Run WinML from JavaScript (JS bindings)

This guide shows how to run ONNX model inference from Electron using JS bindings for Windows App SDK ML APIs (execution provider discovery via `ExecutionProviderCatalog` and model download via `ModelCatalog`) combined with `onnxruntime-node` for inference — no C# addon required. Inference runs in an Electron utility process so it doesn't block the main process.

## Prerequisites

Before starting this guide, make sure you've:
- Completed the [development environment setup](setup.md).

Install ONNX Runtime for Node:

```bash
npm install onnxruntime-node@1.24.3
```

> [!IMPORTANT]
> The Windows App Runtime pre-loads its own `onnxruntime.dll` into child processes. The `onnxruntime-node` version must match the ORT ABI bundled with your Windows App SDK version. For Windows App SDK 2.x, use `onnxruntime-node@1.24.x`.

## Step 1: Confirm WinML bindings

The Windows App SDK transitively depends on `Microsoft.WindowsAppSDK.ML`, so the WinML APIs are already in your generated bindings. Verify:

> [!IMPORTANT]
> **Requires `@microsoft/dynwinrt-codegen` ≥ `0.1.0-preview.8`** — see [Get started with Electron](index.md#2-call-windows-apis-from-javascript) for older-project fallbacks.

```bash
node -e "console.log(Object.keys(require('#winapp/bindings')).filter(k => k.startsWith('ExecutionProvider')))"
```

You should see `[ 'ExecutionProvider', 'ExecutionProviderCatalog', 'ExecutionProviderReadyState' ]`.

## Step 2: Download the model via Model Catalog (main process)

Use `ModelCatalog` from JS bindings to download and cache the model locally. The catalog reads a JSON manifest (hosted remotely or locally) that describes available models and their download URLs. After the first download, subsequent runs use the cached copy:

Create `src/winml-model.js`:

```js
const { ModelCatalog, ModelCatalogSource, Uri } = require('#winapp/bindings');
const fs = require('node:fs');
const path = require('node:path');

// Remote catalog JSON hosted in the WindowsAppSDK-Samples repo
const MODEL_CATALOG_URL =
  'https://raw.githubusercontent.com/microsoft/WindowsAppSDK-Samples/main/Samples/WindowsML/Resources/SqueezeNetModelCatalog.json';

async function downloadModel(modelId, onProgress) {
  const uri = Uri.createUri(MODEL_CATALOG_URL);
  const source = await ModelCatalogSource.createFromUriAsync(uri);
  const catalog = ModelCatalog.createInstance([source]);
  const model = await catalog.findModelAsync(modelId);

  const op = model.getInstanceAsync();
  if (onProgress) {
    op.progress((value) => {
      try { onProgress(value); } catch {}
    });
  }
  const result = await op;

  const instance = result.getInstance();
  if (!instance) return undefined;

  const paths = instance.modelPaths;
  // modelPaths returns directories containing model files
  for (let i = 0; i < paths.size; i++) {
    const dir = paths.getAt(i);
    if (fs.existsSync(dir) && fs.statSync(dir).isDirectory()) {
      const onnx = fs.readdirSync(dir).find((f) => f.endsWith('.onnx'));
      if (onnx) {
        instance.close();
        return path.join(dir, onnx);
      }
    } else if (dir.endsWith('.onnx')) {
      instance.close();
      return dir;
    }
  }
  instance.close();
  return undefined;
}

module.exports = { downloadModel };
```

## Step 3: Discover and ensure execution providers (main process)

Use `ExecutionProviderCatalog` to list available providers (CPU, DirectML, QNN/NPU) and `ensureReadyAsync` to download their runtime if needed:

Create `src/winml-ep.js`:

```js
const { ExecutionProviderCatalog, ExecutionProviderReadyState, ExecutionProviderReadyResultState } = require('#winapp/bindings');

function listProviders() {
  const catalog = ExecutionProviderCatalog.getDefault();
  return catalog.findAllProviders().map((p) => ({
    name: p.name,
    readyState: p.readyState,
    libraryPath: p.libraryPath,
  }));
}

async function ensureProviderReady(providerName, onProgress) {
  const catalog = ExecutionProviderCatalog.getDefault();
  const providers = catalog.findAllProviders();
  const provider = providers.find((p) => p.name === providerName);
  if (!provider) {
    throw new Error(`Execution provider not found: ${providerName}`);
  }

  if (provider.readyState === ExecutionProviderReadyState.Ready) {
    return { name: provider.name, readyState: 'Ready', libraryPath: provider.libraryPath };
  }

  const op = provider.ensureReadyAsync();
  if (onProgress) {
    op.progress((value) => {
      try { onProgress(value); } catch {}
    });
  }
  const result = await op;
  let readyState;
  if (result.status === ExecutionProviderReadyResultState.Success) readyState = 'Ready';
  else if (result.status === ExecutionProviderReadyResultState.Failure) readyState = 'Failed';
  else readyState = 'InProgress';
  return {
    name: provider.name,
    readyState,
    diagnosticText: result.diagnosticText,
    libraryPath: provider.libraryPath,
  };
}

module.exports = { listProviders, ensureProviderReady };
```

## Step 4: Run inference in a utility process

ONNX Runtime session creation and inference are blocking — run them in an Electron utility process to keep the main process responsive.

### 4.1. Create the worker

Create `src/winml-worker.js` (this file runs in a utility process):

```js
const { roInitialize } = require('@microsoft/dynwinrt');

// When dynwinrt and onnxruntime-node share the same process, ORT's native
// init can leave the COM apartment uninitialized. Explicitly init MTA first.
roInitialize(1);

const ort = require('onnxruntime-node');

async function runModel(modelPath, inputData, inputShape, ep) {
  const providers = ep === 'dml'
    ? [{ name: 'dml', deviceId: 0 }, 'cpu']
    : ['cpu'];

  const session = await ort.InferenceSession.create(modelPath, {
    executionProviders: providers,
    graphOptimizationLevel: 'all',
  });

  const inputName = session.inputNames[0];
  const input = new ort.Tensor('float32', inputData, inputShape);
  const outputs = await session.run({ [inputName]: input });
  return Array.from(outputs[session.outputNames[0]].data);
}

process.parentPort.on('message', async (e) => {
  const { id, method, args } = e.data;
  try {
    if (method === 'classify') {
      const [modelPath, inputData, inputShape, ep] = args;
      const result = await runModel(modelPath, new Float32Array(inputData), inputShape, ep);
      process.parentPort.postMessage({ id, ok: true, result });
    }
  } catch (err) {
    process.parentPort.postMessage({ id, ok: false, error: err.message });
  }
});
```

### 4.2. Launch and call the worker from main

Add the following to your `src/index.js`:

```js
const { utilityProcess } = require('electron');
const path = require('node:path');
const { listProviders, ensureProviderReady } = require('./winml-ep.js');
const { downloadModel } = require('./winml-model.js');

let worker = null;
let workerReady = null;
const pending = new Map();
let nextId = 1;

function startWinmlWorker() {
  worker = utilityProcess.fork(path.join(__dirname, 'winml-worker.js'), [], {
    stdio: 'pipe',
    serviceName: 'winml-worker',
  });
  worker.on('message', (msg) => {
    const entry = pending.get(msg.id);
    if (!entry) return;
    pending.delete(msg.id);
    if (msg.ok) entry.resolve(msg.result);
    else entry.reject(new Error(msg.error));
  });
  workerReady = new Promise((resolve) => worker.once('spawn', resolve));
}

async function classify(modelPath, inputData, inputShape, ep) {
  if (!worker) startWinmlWorker();
  await workerReady;
  return new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    worker.postMessage({ id, method: 'classify', args: [modelPath, Array.from(inputData), inputShape, ep] });
  });
}
```

### 4.3. Use it

Make sure your `createWindow` function is `async`, then add:

```js
const createWindow = async () => {
  // ... existing window creation code ...

  // List and ensure all execution providers are ready
  const providers = listProviders();
  console.log('Available providers:', providers);

  for (const ep of providers) {
    console.log(`Ensuring ${ep.name} is ready...`);
    const result = await ensureProviderReady(ep.name, (progress) => {
      const pct = progress <= 1 ? Math.round(progress * 100) : Math.round(progress);
      process.stdout.write(`\r  ${ep.name}: ${pct}%`);
    });
    process.stdout.write('\n');
    console.log(`  ${ep.name}: ${result.readyState}`);
  }

  // Download model via Model Catalog (cached after first run)
  console.log('Downloading model...');
  const modelPath = await downloadModel('squeezenet', (progress) => {
    if (progress >= 0 && progress <= 100) {
      process.stdout.write(`\rDownloading model: ${Math.round(progress)}%`);
    }
  });
  process.stdout.write('\n');
  console.log('Model path:', modelPath);

  // Run inference in utility process (replace with real preprocessed data)
  const inputData = new Float32Array(1 * 3 * 224 * 224);
  const output = await classify(modelPath, inputData, [1, 3, 224, 224], 'dml');

  console.log('Model output (top 5 values):', output.slice(0, 5));
};
```

## Step 5: Run it

```bash
npx winapp node add-electron-debug-identity
npm start
```

You should see the available execution providers and model output in the console.

> [!TIP]
> For more end-to-end samples of calling Windows APIs from Electron, see the [Electron on Windows gallery](https://github.com/microsoft/electron-on-windows-gallery).

## Next Steps

Congratulations! You're running WinML execution providers and ONNX Runtime from JavaScript — no C# addon required. 🎉

Now you're ready to:
- **[Package Your App for Distribution](packaging.md)** — produce an MSIX you can ship.

Or explore other guides:
- **[Show a Notification from JavaScript](js-notification.md)** — Windows App SDK notifications through JS bindings.
- **[Call Windows APIs from JavaScript](js-file-picker.md)** — pick a file using JS bindings.
- **[Call Phi Silica from JavaScript](js-phi-silica.md)** — summarize text with Windows App SDK AI.
- **[Creating a WinML Addon](winml-addon.md)** — native C# addon counterpart.
- **[Getting Started Overview](index.md)** — return to the main guide.
