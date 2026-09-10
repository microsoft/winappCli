# WinUI 3 Solution Sample

This sample is a **multi-project Visual Studio solution** used to exercise
`winapp run` **solution mode** — where you point `winapp run` at a `.sln`/`.slnx`
(or a directory containing one) and it figures out which project to run.

## What This Sample Shows

- A classic `.sln` (`WinUISolution.sln`) and a configuration-free `.slnx`
  (`WinUISolution.slnx`) with three projects:
  - **`App`** — a packaged WinUI 3 desktop app (the runnable app).
  - **`App.Core`** — a WinUI 3 class library referenced by `App` that
    deliberately declares **no `<Platforms>`** (see below).
  - **`App.Tests`** — a *test-shaped* project that reproduces the shape found in
    real solutions like the WinUI Gallery and the AI Dev Gallery.
- `winapp run` auto-selecting the runnable **`App`** from the solution and
  **skipping `App.Core`** (a library) and **`App.Tests`**, with no ambiguity error.
- Explicit project selection via `--project` when you want a specific project.
- A **multi-project arch regression guard**: `App` references `App.Core`, which
  declares no `<Platforms>`. A global `-p:Platform=<arch>` would desynchronize the
  library's XAML/PRI output path from the app's lookup (`MSB3030`/`PRI252`), so a
  green build here proves winapp keeps the two consistent.

## The projects

| Project | Kind | Key markers |
|---------|------|-------------|
| `App` | Packaged WinUI 3 app | `OutputType=WinExe`, `UseWinUI=true`, `EnableMsixTooling=true`, `Package.appxmanifest`, `ProjectReference` → `App.Core` |
| `App.Core` | WinUI 3 class library (arch regression guard) | `UseWinUI=true`, **no `<Platforms>`** (effective Platform `AnyCPU`), a `Themes/Generic.xaml` ResourceDictionary so the XAML compiler emits a `.pri`/`.xbf` |
| `App.Tests` | Test-shaped (skipped by auto-select) | `OutputType=WinExe`, `<ProjectCapability Include="TestContainer" />`, `MSTest.TestFramework` ref, **`IsTestProject` deliberately NOT set** |

`App.Core` intentionally declares **no `<Platforms>`** (effective Platform `AnyCPU`).
Because winapp only injects an explicit `-p:Platform=<arch>` when the target *and its
whole `ProjectReference` closure* declare that arch, this sample builds with the
architecture conveyed by `-r win-<arch>` alone — so the library's `Generic.xbf`/`.pri`
and the consuming app agree on one output path. The solution maps `App.Core` to
`Any CPU` for every configuration (the VS-canonical mapping for a no-`<Platforms>`
library) so a plain `dotnet build WinUISolution.sln -p:Platform=<arch>` stays consistent too.

`App.Tests` intentionally does **not** set `IsTestProject`, so a naive check on that
property (or on a `.Tests` name suffix) would miss it. `winapp run` classifies it from
evaluated signals — the `TestContainer` capability and the MSTest package reference —
which is the realistic gallery shape.

## How `winapp run` picks the project

When given a solution (or a directory containing one), `winapp run`:

1. Lists the projects in the solution.
2. Classifies each as a runnable **app**, a **library**, or a **test** project
   (using evaluated `OutputType`, `IsTestProject`, `TestContainer` capability,
   and known test-framework package references).
3. Selects:
   - the single runnable app if there is exactly one, **or**
   - the single test project if there are no apps and exactly one test project, **or**
   - errors and lists the candidates if the choice is ambiguous — pass
     `--project` to disambiguate.

## Building and Running

```powershell
# Auto-select the runnable app from the solution and build + launch it.
winapp run WinUISolution.sln

# Same, but only build + register the debug identity without launching (no GUI).
winapp run WinUISolution.slnx --no-launch

# Point at the directory instead of a solution explicitly (directory mode prefers the .slnx migration pair).
winapp run .

# Explicitly select a specific project.
winapp run WinUISolution.sln --project App

# Build the whole solution with dotnet directly.
dotnet build WinUISolution.sln -c Debug -p:Platform=x64
```

## Automated test

`test.Tests.ps1` (Pester 5.x) validates solution mode end-to-end:

- **Phase 1** copies the sources to a clean temp directory and runs
  `winapp run WinUISolution.slnx --no-launch`, asserting the **`App`** project is
  auto-selected and built (no ambiguity error). It also verifies that the
  configuration-free `.slnx` restores without an `MSB4126` solution-platform error
  when `--project App.Tests` resolves an explicit project platform.
- **Phase 2** restores and builds the existing solution to verify the committed
  sample still compiles.

Run it with:

```powershell
.\scripts\test-samples.ps1 -Samples winui-solution
```
