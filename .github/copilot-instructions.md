# Copilot instructions for winsdk (concise)

This file provides focused, actionable information to help an AI coding agent be immediately productive in this repo.

Big picture
- Three main components:
  - src/winsdk-CLI (C#/.NET): the native CLI implemented with System.CommandLine. Key files: `src/winsdk-CLI/Winsdk.Cli/Program.cs`, `*Commands/*.cs` (e.g. `SetupCommand.cs`, `MsixCommand.cs`, `ToolCommand.cs`). Build with: `dotnet build src/winsdk-CLI/winsdk.sln`.
  - src/winsdk-npm (Node): a thin Node wrapper/SDK and CLI (`cli.js`) that forwards most commands to the native CLI. Key helpers: `winsdk-cli-utils.js`, `msix-utils.js`, `addon-utils.js`. Install with `npm install` inside `src/winsdk-npm` and test the CLI locally with `node cli.js <command>`.
  - src/winsdk-vcpkg (vcpkg ports + sample): contains vcpkg port files and a CMake sample. Build the sample with CMake presets (see `src/winsdk-vcpkg/vcpkg_sample/README.md`): `cmake . --preset x64-debug` then `cmake --build out/build/x64-debug`.

Developer workflows (concrete commands)
- Build native CLI: `dotnet restore && dotnet build src/winsdk-CLI/winsdk.sln -c Debug`.
- Run native CLI in-tree: `dotnet run --project src/winsdk-CLI/Winsdk.Cli/Winsdk.Cli.csproj -- <args>` or execute the built exe under `bin/Debug`.
- Update npm package with CLI changes: `cd src/winsdk-npm && npm run build` (builds & publishes C# CLI to npm bin folders) OR `npm run build-copy-only` (copies already built Release binaries).
- Node package dev: `cd src/winsdk-npm && npm install` then `node cli.js help` (or run `node ./cli.js setup` to exercise setup flow).
- Generate / test C++ sample: follow `src/winsdk-vcpkg/vcpkg_sample/README.md` (CMake presets + `winsdk_copy_appx_files()` helper used by the sample).
- MSIX / packaging: templates and generation logic live in `src/winsdk-CLI/Winsdk.Cli/Services/MsixService.cs` and assets in `msix-assets/`. Node helper functions are in `src/winsdk-npm/msix-utils.js` (e.g. `addElectronDebugIdentity`).

Project conventions & patterns (repo-specific)
- .winsdk is the canonical workspace folder for downloaded NuGet packages and generated headers. The Node API defaults to `.winsdk/packages` and `.winsdk/generated/include` (see `src/winsdk-npm/README.md`).
- `winsdk.example.yaml` defines package names/versions and `SetupCommand` writes an active `winsdk.yaml` to the `.winsdk` area; inspect `SetupCommand.cs` for when/where config is saved and how `.gitignore` is updated.
- Node <-> native split: `src/winsdk-npm/cli.js` forwards non-node-only commands to the native `winsdk-cli` using `winsdk-cli-utils.callWinsdkCli`. Keep both sides in sync when adding commands.
- CppWinRT flow: the C# runner writes a response file `.cppwinrt.rsp` before invoking `cppwinrt` (see `src/winsdk-CLI/Winsdk.Cli/Services/CppWinrtRunner.cs`). Expect external dependency on `cppwinrt` and SDK packages in `.winsdk/packages`.
- Console UI: use `UiSymbols` for emoji/ASCII fallbacks to keep messages consistent (see `UiSymbols.cs`).

Integration points & external dependencies
- NuGet: packages are downloaded and extracted to `.winsdk/packages` (handled by `src/winsdk-npm` utilities).
- External tools invoked: `cppwinrt`, BuildTools executables (mt.exe, signtool.exe, makeappx.exe, etc.). Node helper `execSyncWithBuildTools` ensures BuildTools are on PATH at runtime.
- vcpkg: ports are under `src/winsdk-vcpkg/vcpkg_ports`; the repo provides a sample port and CMake sample to test integration.

Where to look first (important files)
- `src/winsdk-CLI/Winsdk.Cli/` — C# command implementations, services, templates (MSIX/service/CppWinRT runners).
- `src/winsdk-npm/` — Node CLI, helper utilities, API used by other packages/projects.
- `src/winsdk-vcpkg/` — vcpkg port and CMake sample.
- `winsdk.example.yaml` — canonical example configuration for SDK package versions.
- `msix-assets/` and `sample/` — examples that show packaging and electron integration.

Quick change checklist for common edits
- Adding a new CLI subcommand: implement in C# under `Winsdk.Cli/Commands` AND update `src/winsdk-npm/cli.js` help text and `winsdk-cli-utils` forwarding if needed.
- Changing Cpp/WinRT generation: update `CppWinrtRunner.cs` and adjust node `setup` flow if output paths change (see `.winsdk/generated/include`).
- Updating package versions: edit `winsdk.example.yaml` and ensure `SetupCommand` behavior remains compatible.
- After C# CLI changes: Use `cd src/winsdk-npm && npm run build` or `npm run build-copy-only` to properly update npm package binaries.

If something's missing from these notes, tell me which area you'd like expanded (build, packaging, node-native contract, or vcpkg), and I will update this document.