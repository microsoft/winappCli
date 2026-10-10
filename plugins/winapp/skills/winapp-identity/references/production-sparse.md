# Production sparse identity

## Production sparse packaging (`init --sparse` / `pack` / `embed-identity`)

`create-debug-identity` is for **developer-time debugging** (requires Developer Mode, registers a raw manifest). For **production** — shipping identity to an app distributed by an existing installer (Inno Setup, WiX, NSIS) — use the sparse packaging workflow, which produces a signed identity-only `.msix`:

```powershell
# 1. Create the sparse identity manifest for your exe (skips SDK install)
winapp init --exe ./bin/Release/MyApp.exe --sparse --use-defaults

# 2. Build and sign the identity-only .msix (just the manifest, no binaries)
winapp pack ./sparse/appxmanifest.xml --cert ./devcert.pfx

# 3. Embed the <msix> identity element into the exe's fusion manifest
winapp embed-identity ./bin/Release/MyApp.exe
```

Then your installer registers the package against the install directory:
`Add-AppxPackage -Path MyApp.identity.msix -ExternalLocation <install-dir>`.

Assets are resolved from the external (install) location at runtime, **not** bundled into the `.msix`. `winapp embed-identity` also supports an XML mode (`winapp embed-identity ./app.manifest`) for updating a checked-in side-by-side manifest. See the [Sparse Packaging Guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/sparse.md) and the [sparse-app sample](https://github.com/microsoft/WinAppCli/tree/main/samples/sparse-app).

### Initialize a sparse identity package (existing exe)

Use `--sparse` when you have an **already-built desktop exe** (WPF, WinForms, Win32, Electron, etc.) and only want to give it **package identity** — without repackaging the whole app into the MSIX. The app's files stay where they are and are resolved from an *external content location* at runtime.

```powershell
# Generate an identity-only sparse manifest for an existing exe
winapp init --exe ./bin/Release/MyApp.exe --sparse

# Non-interactive, with explicit identity values
winapp init --exe ./bin/Release/MyApp.exe --sparse --name MyApp --publisher "CN=Contoso" --use-defaults
```

`--sparse` requires `--exe`. It skips all SDK/package installation (sparse identity packages have no SDK dependencies) and, by default, writes to a dedicated `sparse/` folder in the current directory (override with `--output-dir`) so the manifest and its `Assets/` stay out of a build-output folder that a rebuild would wipe:
- `appxmanifest.xml` — identity-only sparse manifest (declares `uap10:AllowExternalContent`)
- `Assets/` — placeholder visual assets (extracted from the exe's icon when possible), resolved from the **external location** at runtime — **not** bundled into the `.msix`

If an `appxmanifest.xml` already exists in the target directory, init fails instead of overwriting it; re-run with `--force` to regenerate.

This is step 1 of the sparse packaging workflow. Continue with:
1. `winapp pack ./sparse/appxmanifest.xml --cert ./devcert.pfx` — build the signed identity `.msix`
2. `winapp embed-identity ./bin/Release/MyApp.exe` — connect the exe to the identity package (re-sign the exe afterward)
3. Register in your installer with `Add-AppxPackage -Path <msix> -ExternalLocation <install-dir>`

For the full walkthrough, see the [Sparse packaging guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/sparse.md).
