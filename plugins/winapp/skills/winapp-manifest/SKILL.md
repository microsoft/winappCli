---
name: winapp-manifest
description: "Edit Package.appxmanifest or appxmanifest.xml: execution aliases (launch from a terminal), file associations, protocols, capabilities, identity, and app icons/tiles (regenerate all from one image). Use when changing what the manifest declares or replacing icons. Not for packaging or signing."
---

**If you can't run `winapp` yourself** (no shell, or the command is denied), give the user the exact `winapp` command(s) for their project instead of only describing the steps.

## When to use

Use this skill when:
- **Creating `Package.appxmanifest`** for a project that doesn't have one yet
- **Generating app icon assets** from a single source image
- **Understanding manifest structure** for package identity and capabilities

## Prerequisites

- winapp CLI installed
- Optional: a source image (PNG or SVG, at least 400x400 pixels) for custom app icons

## Key concepts

**`Package.appxmanifest`** is the key prerequisite for most winapp commands — it's more important than `winapp.yaml`. It declares:
- **Package identity** — name, publisher, version
- **App entry point** — which executable to launch
- **Capabilities** — what the app can access (internet, file system, etc.)
- **Visual assets** — icons for Start menu, taskbar, installers
- **Extensions** — share target, startup tasks, file associations, etc.

**Two manifest templates:**
- **`packaged`** (default) — for full MSIX distribution
- **`sparse`** — for desktop apps that need package identity without full MSIX containment (uses `AllowExternalContent`)

**`winapp init` also generates a manifest** as part of full project setup. Use `winapp manifest generate` when you only need the manifest without SDK setup or `winapp.yaml`.

## Usage

### Generate a new manifest

```powershell
# Defaults — uses current folder name, current user as publisher
winapp manifest generate

# Into a specific directory
winapp manifest generate ./my-project

# Customize identity
winapp manifest generate --package-name "MyApp" --publisher-name "CN=Contoso" --version "2.0.0.0"

# Set entry point and description
winapp manifest generate --executable myapp.exe --description "My awesome app"

# Generate a sparse manifest (for desktop apps needing identity without full MSIX)
winapp manifest generate --template sparse

# Overwrite existing manifest
winapp manifest generate --if-exists overwrite
```

Output:
- `Package.appxmanifest` — the manifest file
- `Assets/` — default app icons in required sizes (Square44x44Logo, Square150x150Logo, Wide310x150Logo, etc.)

### Update app icons from a source image

```powershell
# Generate all required icon sizes from one source image
winapp manifest update-assets ./my-logo.png

# SVG source images produce the best quality at all sizes
winapp manifest update-assets ./my-logo.svg

# Specify manifest location (if not in current directory)
winapp manifest update-assets ./my-logo.png --manifest ./path/to/Package.appxmanifest

# Generate light theme variants from a separate image
winapp manifest update-assets ./my-logo.png --light-image ./my-logo-light.png

# Use the same image for both (generates all MRT light theme qualifiers)
winapp manifest update-assets ./my-logo.png --light-image ./my-logo.png
```

The source image should be at least 400x400 pixels (PNG or SVG recommended). The command reads the manifest to determine which asset sizes are needed and generates:
- **5 scale variants** per asset (100%, 125%, 150%, 200%, 400%)
- **14 plated + 14 unplated targetsize variants** for the app icon (44x44)
- **app.ico** — multi-resolution ICO file for shell integration. If an existing `.ico` file is present in the assets directory, it is replaced in-place (preserving the original filename)
- With `--light-image`: light theme variants using the correct MRT qualifiers per asset type

### Add an execution alias

Execution aliases let users launch the app by typing its name in a terminal (e.g. `myapp`).

```powershell
# Add alias inferred from the Executable attribute in the manifest
winapp manifest add-alias

# Specify the alias name explicitly
winapp manifest add-alias --name myapp

# Target a specific manifest file
winapp manifest add-alias --manifest ./path/to/Package.appxmanifest
```

This adds a `uap5:AppExecutionAlias` extension to the manifest. If the alias already exists, the command reports it and exits successfully.

> **When combined with `winapp run --with-alias`** or the `WinAppRunUseExecutionAlias` MSBuild property, this enables apps to run in the current terminal with inherited stdin/stdout/stderr instead of opening a new window.

## Manifest structure overview

A typical `Package.appxmanifest` looks like:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
  <Identity Name="MyApp" Publisher="CN=MyPublisher" Version="1.0.0.0" />
  <Properties>
    <DisplayName>My App</DisplayName>
    <PublisherDisplayName>My Publisher</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Resources>
    <Resource Language="en-us" />
  </Resources>
  <Applications>
    <Application Id="App" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="My App" Description="My Application"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png" BackgroundColor="transparent" />
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
```

Key fields to edit:
- `Identity.Name` — unique package name (no spaces)
- `Identity.Publisher` — must match your certificate exactly
- `Application.Executable` — your app's exe filename
- `Capabilities` — add capabilities as needed (`internetClient`, `broadFileSystemAccess`, etc.)

## Tips

- Always ensure `Identity.Publisher` matches your signing certificate — use `winapp cert generate --manifest` to auto-match
- The `sparse` template adds a `<uap10:AllowExternalContent>true</uap10:AllowExternalContent>` element (under `<Properties>`) for apps that need identity but run outside the MSIX container
- You can manually edit `Package.appxmanifest` after generation — it's a standard XML file
- Image assets must match the paths referenced in the manifest — `update-assets` handles this automatically
- For logos, transparent PNGs or SVGs work best. SVG source images are rendered as vectors directly at each target size, producing pixel-perfect results. Use a square image for best results across all sizes.
- **`$targetnametoken$` placeholder:** When `winapp manifest generate` creates `Package.appxmanifest`, it sets `Application.Executable` to `$targetnametoken$.exe` by default. This is a valid placeholder that gets automatically resolved by `winapp package --executable <name>` at packaging time — you rarely need to override it during manifest generation. If `--executable` is provided to `winapp manifest generate`, winapp reads `FileVersionInfo` from the actual exe to auto-fill package name, description, publisher, and extract an icon, so the exe must already exist on disk.

## Related skills

- After generating a manifest, see `winapp-signing` for certificate setup and `winapp-package` to create the MSIX installer
- Not sure which command to use? See `winapp-troubleshoot` for a command selection flowchart

## Troubleshooting
| Error | Cause | Solution |
|-------|-------|----------|
| "Manifest already exists" | `Package.appxmanifest` present | Use `--if-exists overwrite` to replace, or edit existing file directly |
| "Invalid source image" | Image too small or wrong format | Use PNG or SVG, at least 400x400 pixels |
| "Publisher mismatch" during packaging | Manifest publisher ≠ cert publisher | Edit `Identity.Publisher` in manifest, or regenerate cert with `--manifest` |

## CLI reference

Run `winapp <command> --help` for current command options, or `winapp --cli-schema` for the complete machine-readable command schema.
