# Using winapp CLI with AI Coding Assistants

This guide provides prompt templates and best practices for using AI coding assistants (GitHub Copilot, Cursor, Claude, ChatGPT, etc.) with winapp CLI.

## Quick Start Prompt

Copy and paste this into your AI coding assistant:

```
I'm working with winapp CLI - a CLI for generating and managing appxmanifest.xml, 
image assets, test certificates, Windows (App) SDK projections, package identity, 
and packaging for any app framework targeting Windows.

Please read and reference the official LLM context documentation:
https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md

My specific task: [describe what you need help with]
```

---

## Detailed Prompt Template

For more complex tasks, use this extended version:

```markdown
# Context: winapp CLI Project

I'm working with **winapp CLI** - a CLI for generating and managing appxmanifest.xml, 
image assets, test certificates, Windows (App) SDK projections, package identity, 
and packaging. It works with any app framework targeting Windows.

## Documentation Reference

**Primary LLM-optimized documentation (please fetch and reference):**
https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md

This contains:
- Complete command reference with all options
- Common workflows (new projects, existing projects, debugging, Electron apps)
- Prerequisites and state requirements
- Machine-readable CLI schema

Additional resources if needed:
- Main README: https://github.com/microsoft/winapp CLI/blob/main/README.md
- Full usage docs: https://github.com/microsoft/winapp CLI/blob/main/docs/usage.md
- Electron guide: https://github.com/microsoft/winapp CLI/blob/main/docs/electron-get-started.md

## My Project Details

**Framework:** [e.g., Electron, .NET, C++, Rust, Tauri, Python, etc.]

**Current setup:**
- [Describe your current project state]
- [Mention if you have winapp.yaml, appxmanifest.xml, etc.]
- [Current build process]

**What I need help with:**
[Choose relevant workflow:]
- [ ] New project setup with SDK projections
- [ ] Adding package identity for debugging Windows APIs
- [ ] MSIX packaging and signing
- [ ] Electron integration with native addons
- [ ] CI/CD pipeline integration
- [ ] Updating SDK versions
- [ ] Certificate management
- [ ] Manifest generation or modification

## Specific Question/Task

[Your detailed question or task here]
```

---

## For GitHub Copilot Users

### Method 1: Workspace Context (Recommended)

1. Download the LLM-optimized documentation locally:
   ```bash
   # Create .ai directory for AI context files
   mkdir -p .ai
   curl -o .ai/winapp-llm-context.md https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md
   ```

2. Keep the file open in your editor while coding - Copilot will automatically use it as context

3. Optionally, also download the CLI schema for structured data:
   ```bash
   curl -o .ai/winapp-cli-schema.json https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/cli-schema.json
   ```

### Method 2: Project Instructions

Create `.github/copilot-instructions.md` in your repository:

```markdown
# winapp CLI Project Instructions

This project uses winapp CLI for Windows app development, packaging, and identity management.

## Core Workflows

**New Project:**
1. `winapp init .` (or with `--use-defaults` for non-interactive)
2. Build your app
3. `winapp create-debug-identity <exe-path>` for package identity during development
4. `winapp pack <output-folder> --cert .\devcert.pfx` for MSIX packaging

**Existing Project/CI:**
1. `winapp restore` - reinstall from winapp.yaml
2. Build and package

**Electron Projects:**
1. `winapp init --use-defaults`
2. `winapp node create-addon --template cs` (or `--template cpp`)
3. `winapp node add-electron-debug-identity`
4. `npm start` (now with package identity)
5. `winapp pack <dist> --cert .\devcert.pfx`

## Key Commands
- `winapp init` - Initialize with manifests, assets, certs, SDK projections
- `winapp restore` - Restore packages from winapp.yaml
- `winapp manifest generate` - Create appxmanifest.xml
- `winapp cert generate` - Create development certificate
- `winapp package` - Build MSIX from output folder
- `winapp create-debug-identity` - Add temporary package identity for debugging
- `winapp tool` - Run Windows SDK build tools

## Documentation Reference
Primary: https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md
Schema: https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/cli-schema.json

When helping with winapp CLI tasks:
1. Reference the llm-context.md for command details and workflows
2. Check winapp.yaml for project configuration
3. Verify appxmanifest.xml exists for packaging operations
4. Ensure devcert.pfx is available for signing
```

---

## For Cursor/Claude/ChatGPT Users

Simply paste this at the start of your conversation:

```
I need help with winapp CLI - a CLI for managing appxmanifest.xml, 
image assets, certificates, Windows SDK projections, and MSIX packaging.

Please fetch and reference the LLM-optimized documentation:
https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md

My task: [describe what you need]

[Optional: Also mention your framework - Electron, .NET, C++, Rust, Python, etc.]
```

These assistants can fetch the documentation directly from the URL.

---

## Framework-Specific Prompts

### For Electron Projects

```
I'm building an Electron app and need help with winapp CLI for:
- [ ] Setting up package identity for debugging
- [ ] Creating native C#/C++ addons for Windows APIs
- [ ] MSIX packaging for distribution
- [ ] Code signing with certificates

Please reference the LLM-optimized docs:
https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md

Also see Electron-specific guide if needed:
https://github.com/microsoft/winapp CLI/blob/main/docs/electron-get-started.md

My current setup: [describe your Electron project structure]
My specific need: [e.g., "add push notification support via native addon"]
```

**Common Electron tasks:**
- `winapp node create-addon --template cs` - Create C# native addon
- `winapp node create-addon --template cpp` - Create C++ native addon  
- `winapp node add-electron-debug-identity` - Enable identity for debugging
- `winapp node clear-electron-debug-identity` - Remove identity

### For .NET Projects

```
I'm building a .NET [WPF/WinForms/Console] app and need winapp CLI help with:
- [ ] Adding package identity for modern Windows APIs
- [ ] MSIX packaging
- [ ] Certificate generation and signing
- [ ] Windows Store preparation

Reference the LLM-optimized docs:
https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md

Framework guide:
https://github.com/microsoft/winapp CLI/blob/main/docs/guides/dotnet.md

My project: [describe your .NET project]
```

**Key workflow for .NET:**
1. `winapp init` - Sets up manifests, certs, and optionally SDK projections
2. Build your app normally
3. `winapp create-debug-identity YourApp.exe` - For testing APIs requiring identity
4. `winapp pack bin\Release\net8.0-windows --cert devcert.pfx` - Package as MSIX

### For C++ Projects

```
I'm building a C++ Win32 app with [CMake/MSBuild] and need winapp CLI for:
- [ ] Windows SDK integration and projections
- [ ] Package identity for modern Windows APIs
- [ ] MSIX packaging
- [ ] Manifest generation

Reference: https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md
C++ guide: https://github.com/microsoft/winapp CLI/blob/main/docs/guides/cpp.md

My build system: [CMake/MSBuild/other]
My specific need: [e.g., "integrate Windows App SDK with CMake build"]
```

**C++ workflow:**
1. `winapp init --setup-sdks stable` - Download Windows SDK + App SDK projections
2. Add `.winapp/packages` to include paths
3. Build your app
4. `winapp create-debug-identity YourApp.exe` - Test with package identity
5. `winapp pack build/release --cert devcert.pfx` - Create MSIX

---

## CI/CD Integration Prompt

```
I need to integrate winapp CLI into my [GitHub Actions/Azure DevOps] pipeline for:
- [ ] Automated winapp.yaml package restoration
- [ ] MSIX packaging in build pipeline
- [ ] Code signing (with cert stored in secrets)
- [ ] Build artifact generation

Current pipeline: [describe your CI/CD setup]

Please reference:
- Setup action: https://github.com/microsoft/setup-winapp CLI
- LLM docs: https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md

My specific need: [e.g., "restore packages and build MSIX in GitHub Actions"]
```

**Example GitHub Actions workflow:**
```yaml
- uses: microsoft/setup-winapp CLI@v1
- name: Restore winapp CLI packages
  run: winapp restore
- name: Build app
  run: [your build command]
- name: Package MSIX
  run: winapp pack ./dist --cert ${{ secrets.CERT_PATH }}
```

---

## Common Tasks - Quick Prompts

**Initialize new project:**
```
Help me set up a new [framework] project with winapp CLI.
Run: winapp init . --use-defaults
Reference: https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md
```

**Generate manifest only:**
```
I need to create an appxmanifest.xml for my [framework] app.
Use: winapp manifest generate
Project details: [app name, publisher, entry point]
Reference: https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md
```

**Add debug identity:**
```
I need package identity to debug Windows APIs that require it (e.g., push notifications).
Help me use: winapp create-debug-identity <my-app.exe>
I have: [appxmanifest.xml location]
Reference: https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md
```

**Package as MSIX:**
```
Help me create a signed MSIX package for distribution.
App output folder: [path]
Use: winapp pack [folder] --cert devcert.pfx
Reference: https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md
```

**Create development certificate:**
```
Help me generate a test certificate for code signing.
Use: winapp cert generate --publisher "CN=MyCompany" --install
Reference: https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md
```

**Restore from existing project:**
```
I cloned a project with winapp.yaml and need to restore packages.
Use: winapp restore
Reference: https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md
```

**Update SDK versions:**
```
Help me update to the latest Windows SDK and App SDK versions.
Use: winapp update --setup-sdks stable
Reference: https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md
```
**Install SDKs after initial setup:**
```
I ran winapp init without SDK installation and now need the SDKs.
Use: winapp init --use-defaults --setup-sdks stable
This re-runs init, skips prompts, preserves existing files, and installs the SDKs.
Reference: https://raw.githubusercontent.com/microsoft/WinAppCli/main/docs/llm-context.md
```
---

## Tips for Best Results

1. **Always reference the LLM-optimized docs** - Include the llm-context.md URL in your prompt:
   ```
   https://raw.githubusercontent.com/microsoft/winapp CLI/main/docs/llm-context.md
   ```

2. **Be specific about your framework** - Electron, .NET, C++, Rust, Python, etc.

3. **Mention existing files** - Does your project already have:
   - `winapp.yaml` (version configuration)
   - `appxmanifest.xml` (package manifest)
   - `devcert.pfx` (development certificate)
   - `.winapp/` folder (SDK packages)

4. **State your goal clearly**:
   - Local debugging with package identity
   - MSIX packaging for distribution
   - Windows Store submission
   - CI/CD automation
   - Native addon development (Electron)

5. **Include error messages** if troubleshooting - Full command output helps

6. **Check prerequisites** - Some commands require:
   - `winapp init` → Creates initial setup
   - `winapp restore` → Needs existing `winapp.yaml`
   - `winapp package` → Needs built app output + `appxmanifest.xml`
   - `winapp create-debug-identity` → Needs `appxmanifest.xml` + exe path

7. **For advanced scenarios** - Mention the CLI schema is available:
   ```bash
   winapp --cli-schema  # Outputs machine-readable JSON
   ```
