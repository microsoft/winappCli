# WinApp CLI Python blog examples

Runnable companions to the [Python projection blog](../../ifdef-blog-python-projections-draft.md). This workflow uses the Python CLI from [microsoft/winappCli#1062](https://github.com/microsoft/winappCli/pull/1062), not an assumed published `winapp-cli` package. DynWinRT and codegen are the published **0.1.0rc23** packages.

## Prerequisites

- Windows 11 x64 or ARM64 with CPython 3.11-3.14 for this walkthrough.
- The Python CLI from microsoft/winappCli#1062, already installed and available as `winapp-py` in the terminal. Preview installation is described separately below.
- Windows App Runtime matching the restored SDK; the validated SDK is 2.5.1.

The application does not need a per-API native extension build. CLI installation is separate from the project environment it manages.

The notification and text-rewriting companions are independent projects. Start either from the repository root in a normal PowerShell session. Each setup defines its own source paths and creates a new folder under your home directory, with its own `.venv`, SDK pins, configuration, and manifest. The XAML showcase links to a separate dynwinrt sample.

## 1. Windows notification without identity

Use a non-elevated terminal. From the repository root, create and initialize a separate writable project before copying any scripts. Leave generated content outside this sample checkout:

```powershell
$repo = (Get-Location).Path
$sample = Join-Path $repo "samples\python-projections-blog"
$project = Join-Path $HOME "python-notification-demo"
if (Test-Path -LiteralPath $project) { throw "Choose a new project directory" }
New-Item -ItemType Directory -Path $project | Out-Null
Set-Location $project

winapp-py init . --use-defaults --add-python-bindings
if ($LASTEXITCODE -ne 0) { throw "Python project initialization failed" }
```

`winapp-py init` prepares the Windows SDK and Windows App SDK inputs and generates the default supported, non-UI Windows App SDK bindings.

Copy `notification.py`, then run it without identity:

```powershell
Copy-Item -LiteralPath (Join-Path $sample "notification.py") -Destination .
winapp-py run .\notification.py
```

`notification.py` sends a Windows notification and runs unpackaged, without `--identity`. It checks the assigned notification ID, unregisters the running process in `finally`, and releases native objects before the SDK context. It does not handle notification clicks or remove the notification after sending it. Use a non-elevated terminal; Windows settings can suppress a banner.

## 2. XAML WinUI showcase

The independent [WinUI Tic-Tac-Toe sample](https://github.com/microsoft/dynwinrt/tree/main/samples/python/winui-tic-tac-toe) loads a XAML board, connects its named controls to Python handlers, and uses a Mica backdrop. Follow that sample's own prerequisites, generation, and run commands; it has its own SDK pins and bindings package.

The article's GIF records actual game moves, an X win, and a reset from a cached copy of this existing XAML sample. It is not a recording of the new notification or text-rewriting programs.

## 3. Text rewriting with identity

This example needs Developer Mode and a supported Copilot+ PC. Follow the [Windows AI guide](https://learn.microsoft.com/en-us/windows/ai/apis/phi-silica) for model availability and hardware requirements.

From the repository root, create and initialize its independent project:

```powershell
$repo = (Get-Location).Path
$sample = Join-Path $repo "samples\python-projections-blog"
$project = Join-Path $HOME "python-text-rewrite"
if (Test-Path -LiteralPath $project) { throw "Choose a new project directory" }
New-Item -ItemType Directory -Path $project | Out-Null
Set-Location $project
winapp-py init . --use-defaults --add-python-bindings
if ($LASTEXITCODE -ne 0) { throw "Python project initialization failed" }
```

`LanguageModel` and `TextRewriter` belong to the default scope. Keep the project's generated `package = "winapp_bindings"`, `scope = "default"`, and `types = []` settings; no additional types or a second initialization are needed.

```powershell
Copy-Item -LiteralPath (Join-Path $sample "rewrite.py") -Destination .
```

Before running, make sure this project's `Package.appxmanifest` has the [article's Windows AI declarations](../../ifdef-blog-python-projections-draft.md#3-rewrite-text-with-on-device-ai): the `systemai` namespace and token in `IgnorableNamespaces`, `systemAIModels`, and the shown `MaxVersionTested` value.

Run with identity:

```powershell
winapp-py run --identity .\rewrite.py
```

`rewrite.py` uses one short English sentence. It calls `LanguageModel.create_async`, `TextRewriter.create_instance`, and `rewrite_async(text, TextRewriteTone.Formal)`, then prints the rewritten sentence. Unsupported readiness states, failed preparation, unsuccessful results, and empty output raise explicit errors.

When the model is `NotReady`, the code calls `ensure_ready_async()` and requires `AIFeatureReadyResultState.Success` before continuing. This prepares required components and may download them.

To clear this project's development identity after testing, run `winapp-py identity clear` from `python-text-rewrite`. This does not uninstall an installed MSIX.

From `python-text-rewrite`, follow the [article's packaging steps](../../ifdef-blog-python-projections-draft.md#package-and-share-the-rewrite-app) to bundle, sign, install, and run this command-line application.

## Extra: AppInstance initialization check

`app_instance.py` is an initialization check, not a replacement for `os.getpid()`. It can run in either local companion project above with `winapp_bindings` and SDK 2.5. Without identity, it sets the architecture-specific Bootstrap DLL path and retains SDK initialization until after apartment cleanup. Copy it into the project you choose and run the following from that folder, using the `$sample` path defined in that project's setup.

```powershell
Copy-Item -LiteralPath (Join-Path $sample "app_instance.py") -Destination .
winapp-py run .\app_instance.py
winapp-py run --identity .\app_instance.py
winapp-py identity clear
```

To restore a project's pinned SDK inputs or force regeneration later, run these commands from that project's folder:

```powershell
winapp-py restore
winapp-py generate --force
```

## Validation boundaries

The synchronous notification revision has not been run natively. The text-rewriting model has not been invoked, and no readiness checks, model preparation, identity registration, or packaging have been performed for it. Its model, rewrite, and preparation calls were checked against the restored Windows App SDK 2.5.1 generated declarations and the published JavaScript example.

The XAML GIF used an isolated copy of the existing sample and previously cached ARM64 runtime/projection wheels. Maximizing that owned window made the layout visible at the desktop's current scaling; UI Automation invoked five cells, observed `Player X wins!`, reset the board, and closed the window. The GIF contains seven cropped/downscaled real screenshots. The source checkout was not modified, and the capture involved no model calls, SDK downloads, or AI identity registration. This is media for the existing XAML sample, not a new validation of the text-rewriting program or the article's SDK 2.5 bindings.

Earlier native evidence applies to the previous examples, not these revised scenarios. `ocr.py`, `counter.py`, and their image assets are retained as earlier companions. OCR and AppInstance were run on native ARM64/CPython 3.13.15 with the preview CLI and DynWinRT 0.1.0rc23; the old OCR document yielded five lines and 36 words, including an `AI`/`Al` recognition error. The old counter screenshot followed two button invocations and an exit-code-0 close. None of that is a text-rewriting result or evidence that the new notification script was run.

For mypy, run from the project you want to check and point it at the parent of the editable generated package:

```powershell
$env:MYPYPATH = (Resolve-Path .\.winapp\python).Path
python -m pip --python .\.venv\Scripts\python.exe install "mypy==1.20.2"
.\.venv\Scripts\python.exe -m mypy --strict --follow-imports=silent `
    .\notification.py
```

Use `.\rewrite.py` instead in the text-rewriting project.

This optional check installs mypy in the selected environment. It does not suppress missing generated imports or use casts. No notification-click activation, custom-component deployment, or native coverage of the revised examples is claimed here.

The private identity environment installs declared application dependencies and the generated bindings; packages manually added to `.venv` are not automatically copied. Ordinary `run` requires an existing `.venv` and does not initialize Windows App SDK on a script's behalf.

## Preview tool installation and version reproduction

This section is for obtaining the unreleased preview tool or reproducing the versions used for the original recorded examples. It can be skipped when `winapp-py` is already installed.

The tested wheel is `winapp_cli-0.7.2.dev63-py3-none-win_arm64.whl`, built from microsoft/winappCli#1062 at `da26b1e8c220458d08cca1800cee86848e4e42c5`. The live PR head changed to `20585326` during review; this validation concerns the installed build, not that newer source. Install the matching architecture's wheel into the Python environment chosen for the CLI, for example the normal Python installation:

```powershell
$cliWheel = "C:\path\to\winapp_cli-0.7.2.dev63-py3-none-win_arm64.whl"
python -m pip install $cliWheel
winapp-py --version
```

The wheel installs the `winapp-cli` distribution and its `winapp-py` console entry. Its Python `Scripts` directory must be on `PATH`. This is a local PR build, not a claim that `pip install winapp-cli` retrieves the Python feature from a public release.

The validation used published DynWinRT/runtime and codegen `0.1.0rc23`. To reproduce those versions, copy the supplied [`constraints.txt`](constraints.txt) into the project and enable it before CLI init, restore, or forced generation:

```powershell
$env:PIP_CONSTRAINT = (Resolve-Path .\constraints.txt).Path
winapp-py generate --force
```

Constraints only limit package versions; they do not install the packages themselves. `PIP_CONSTRAINT` applies to this terminal and its pip subprocesses, including the CLI's codegen preparation, not to the entire machine. Normal use does not require creating a constraints file. The generated package independently pins the runtime matching its generator.
