# Copilot instructions for winapp

This file provides focused, actionable information to help an AI coding agent be immediately productive in this repo.

## Big picture

Two main components:
- **src/winapp-CLI** (C#/.NET): The native CLI implemented with System.CommandLine. Key files: `Program.cs`, `Commands/*.cs` (e.g., `InitCommand.cs`, `PackageCommand.cs`). Build with `scripts/build-cli.ps1`.
- **src/winapp-npm** (Node): A thin Node wrapper/SDK and CLI (`cli.js`) that forwards most commands to the native CLI. Key helpers: `winapp-cli-utils.js`, `msix-utils.js`, `cpp-addon-utils.js`. Install with `npm install` inside `src/winapp-npm`.

## Developer workflows

```powershell
# Build native CLI (preferred)
.\scripts\build-cli.ps1

# Or build directly with dotnet
dotnet build src/winapp-CLI/winapp.sln -c Debug

# Run native CLI in-tree
dotnet run --project src/winapp-CLI/WinApp.Cli/WinApp.Cli.csproj -- <args>

# Update npm package after CLI changes
cd src/winapp-npm && npm run build              # builds C# CLI + copies to npm bin
cd src/winapp-npm && npm run build-copy-only    # copies already-built Release binaries

# Node package development
cd src/winapp-npm && npm install
node cli.js help

# Regenerate LLM documentation after command changes
.\scripts\generate-llm-docs.ps1
```

## Where to look first

| Area | Key files |
|------|-----------|
| CLI commands | `src/winapp-CLI/WinApp.Cli/Commands/*.cs` |
| Services | `src/winapp-CLI/WinApp.Cli/Services/*.cs` |
| Node CLI | `src/winapp-npm/cli.js`, `winapp-cli-utils.js` |
| Config example | `winapp.example.yaml` |
| LLM docs | `docs/llm-context.md`, `docs/cli-schema.json` |
| Samples | `samples/` (electron, cpp-app, dotnet-app, etc.) |

## CLI command semantics

| Command | When to use |
|---------|-------------|
| `init` | First-time project setup. Creates `winapp.yaml`, `appxmanifest.xml` with assets, dev certificate, and downloads SDKs. Combines `manifest generate` + `cert generate`. Interactive by default. |
| `restore` | Reinstall packages from existing `winapp.yaml`. Use after clone or when `.winapp/` is missing. Does not update versions. |
| `update` | Check for newer SDK versions and update `winapp.yaml`. Also refreshes build tools cache. |
| `manifest generate` | Create `appxmanifest.xml` standalone, without full init. Use when you only need a manifest. |
| `manifest update-assets` | Regenerate app icons from a source image. |
| `cert generate` | Create development certificate standalone. |
| `cert install` | Trust an existing certificate on this machine (requires elevation). |
| `package` | Build MSIX from app's output directory. Combines makeappx + optional signing. |
| `sign` | Code-sign a package or executable with a certificate. |
| `create-debug-identity` | Register app with Windows for identity-requiring APIs during dev. Re-run after manifest/asset changes. |
| `tool` | Execute Windows SDK build tools (makeappx, signtool, makepri). Auto-downloads Build Tools if needed. |
| `get-winapp-path` | Print `.winapp` directory path. Use `--global` for shared cache. |
| `--cli-schema` | Output complete CLI structure as JSON for tooling/LLM integration. |

## Quick change checklist

- **Adding a new CLI command**: Implement in C# under `Commands/`, update `src/winapp-npm/cli.js` if needed, then run `scripts/generate-llm-docs.ps1`.
- **Changing command descriptions**: Edit the description string in the command's constructor. Run `scripts/generate-llm-docs.ps1` and commit updated docs.
- **After C# CLI changes**: Run `cd src/winapp-npm && npm run build` to update npm package binaries.
- **Updating package versions**: Edit `winapp.example.yaml`.

## Integration points

- **NuGet**: Packages downloaded to `.winapp/packages`. global `.winapp` cache at `%LOCALAPPDATA%/.winapp`. 
- **Build Tools**: makeappx.exe, signtool.exe, makepri.exe, etc. Auto-downloaded by `tool` command or when commands then need them are invoked.
- **CppWinRT**: Generated headers in `.winapp/generated/include`. Response file at `.cppwinrt.rsp`.