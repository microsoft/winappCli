# Copilot instructions for winapp (concise)

This file provides focused, actionable information to help an AI coding agent be immediately productive in this repo.

Big picture
- Three main components:
  - src/winapp-CLI (C#/.NET): the native CLI implemented with System.CommandLine. Key files: `src/winapp-CLI/WinApp.Cli/Program.cs`, `*Commands/*.cs` (e.g. `InitCommand.cs`, `RestoreCommand.cs`, `PackageCommand.cs`, `ToolCommand.cs`). Build with: `dotnet build src/winapp-CLI/winapp.sln`.
  - src/winapp-npm (Node): a thin Node wrapper/SDK and CLI (`cli.js`) that forwards most commands to the native CLI. Key helpers: `winapp-cli-utils.js`, `msix-utils.js`, `cpp-addon-utils.js`. Install with `npm install` inside `src/winapp-npm` and test the CLI locally with `node cli.js <command>`.
  - src/winapp-vcpkg (vcpkg ports + sample): contains vcpkg port files and a CMake sample. Build the sample with CMake presets (see `src/winapp-vcpkg/vcpkg_sample/README.md`): `cmake . --preset x64-debug` then `cmake --build out/build/x64-debug`.

Developer workflows (concrete commands)
- Build native CLI: `dotnet restore && dotnet build src/winapp-CLI/winapp.sln -c Debug`.
- Run native CLI in-tree: `dotnet run --project src/winapp-CLI/WinApp.Cli/WinApp.Cli.csproj -- <args>` or execute the built exe under `bin/Debug`.
- Update npm package with CLI changes: `cd src/winapp-npm && npm run build` (builds & publishes C# CLI to npm bin folders) OR `npm run build-copy-only` (copies already built Release binaries).
- Node package dev: `cd src/winapp-npm && npm install` then `node cli.js help` (or run `node ./cli.js init .` to exercise init flow).
- Generate / test C++ sample: follow `src/winapp-vcpkg/vcpkg_sample/README.md` (CMake presets + `winapp_copy_appx_files()` helper used by the sample).
- MSIX / packaging: templates and generation logic live in `src/winapp-CLI/WinApp.Cli/Services/MsixService.cs` and assets in `msix-assets/`. Node helper functions are in `src/winapp-npm/msix-utils.js` (e.g. `addElectronDebugIdentity`).

Project conventions & patterns (repo-specific)
- .winapp is the canonical workspace folder for downloaded NuGet packages and generated headers. The Node API defaults to `.winapp/packages` and `.winapp/generated/include` (see `src/winapp-npm/README.md`).
- `winapp.example.yaml` defines package names/versions and `InitCommand` writes an active `winapp.yaml` to the `.winapp` area; inspect `InitCommand.cs` for when/where config is saved and how `.gitignore` is updated.
- Node <-> native split: `src/winapp-npm/cli.js` forwards non-node-only commands to the native `winapp-CLI` using `winapp-cli-utils.callWinappCli`. Keep both sides in sync when adding commands.
- CppWinRT flow: the C# runner writes a response file `.cppwinrt.rsp` before invoking `cppwinrt` (see `src/winapp-CLI/WinApp.Cli/Services/CppWinrtService.cs`). Expect external dependency on `cppwinrt` and SDK packages in `.winapp/packages`.
- Console UI: use `UiSymbols` for emoji/ASCII fallbacks to keep messages consistent (see `UiSymbols.cs`).

Integration points & external dependencies
- NuGet: packages are downloaded and extracted to `.winapp/packages` (handled by `src/winapp-npm` utilities).
- External tools invoked: `cppwinrt`, BuildTools executables (mt.exe, signtool.exe, makeappx.exe, etc.). Node helper `execSyncWithBuildTools` ensures BuildTools are on PATH at runtime.
- vcpkg: ports are under `src/winapp-vcpkg/vcpkg_ports`; the repo provides a sample port and CMake sample to test integration.

Where to look first (important files)
- `src/winapp-CLI/WinApp.Cli/` — C# command implementations, services, templates (MSIX/service/CppWinRT runners).
- `src/winapp-npm/` — Node CLI, helper utilities, API used by other packages/projects.
- `src/winapp-vcpkg/` — vcpkg port and CMake sample.
- `winapp.example.yaml` — canonical example configuration for SDK package versions.
- `msix-assets/` and `sample/` — examples that show packaging and electron integration.

Quick change checklist for common edits
- Adding a new CLI subcommand: implement in C# under `WinApp.Cli/Commands` AND update `src/winapp-npm/cli.js` help text and `winapp-cli-utils` forwarding if needed.
- Changing Cpp/WinRT generation: update `CppWinrtService.cs` and adjust node `init` flow if output paths change (see `.winapp/generated/include`).
- Updating package versions: edit `winapp.example.yaml` and ensure `InitCommand` behavior remains compatible.
- After C# CLI changes: Use `cd src/winapp-npm && npm run build` or `npm run build-copy-only` to properly update npm package binaries.

If something's missing from these notes, tell me which area you'd like expanded (build, packaging, node-native contract, or vcpkg), and I will update this document.