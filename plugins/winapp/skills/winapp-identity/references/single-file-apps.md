# Single-file .cs apps

#### Single-file mode: `winapp run` on a `.cs` file-based app

A .NET 10 file-based app is a single `.cs` file configured by `#:` directives, with no project file. Point `winapp run` at it and the app builds and launches **with package identity** — no hand-written manifest:

```powershell
# Build and run a file-based app with identity
winapp run counter.cs

# Register identity without launching, or run a Release build detached
winapp run counter.cs --no-launch
winapp run counter.cs -c Release --detach --json

# Remove the package it registered
winapp unregister counter.cs
```

Describe the package with `#:property` directives in the file. All are optional:

| Property | Sets | Default |
|---|---|---|
| `WinAppPackageName` | `Identity/@Name` | file name (sanitized) + a short hash of its path |
| `WinAppDisplayName` | Start/Settings name | file name |
| `WinAppPublisher` | `Identity/@Publisher` | `CN=<current user>`; a bare name is wrapped as `CN=` |
| `WinAppVersion` | `Identity/@Version` | `$(Version)`, normalized to four 0–65535 parts (`1.2.3-preview.4` ⇒ `1.2.3.0`) |
| `WinAppDescription` | Install/Settings description | the display name |
| `WinAppCapabilities` | Capabilities to declare, separated by `;` or `,` | none |
| `WinAppRunUseExecutionAlias` | Launch via execution alias instead of AUMID, so console output stays in the terminal | `true` for `OutputType=Exe`, else `false` |

- **Capabilities for gated APIs.** Running full trust with identity is enough for APIs that only require a packaged identity, but some are gated on a *declared capability* regardless — the Windows AI APIs most notably. (Shell integrations like protocol handlers and file associations are a separate thing again: those need authored `<Extensions>` entries, not a capability.) `#:property WinAppCapabilities=systemAIModels` is all Phi Silica needs from the manifest. winapp writes each name into the element and namespace it actually requires (`<systemai:Capability>`, `<Capability>`, `<DeviceCapability>` — they differ), declares that namespace, and raises `MaxVersionTested` when required. An unknown bare name is rejected rather than guessed; qualify it yourself with `rescap:`, `uap:`, `systemai:`, `device:` or `app:` (the `app:` set is closed at the five foundation capabilities).

- **Bring your own manifest** with `--manifest`, `#:property WinAppManifestPath=…`, or a manifest next to the `.cs` named `<filename>.appxmanifest`. Only that per-file name is auto-detected — a shared `Package.appxmanifest` in the folder is ignored, since several `.cs` files can live together. Otherwise one is generated into the build output (with default assets) and refreshed each run.
- **Packaged and unpackaged both work**, from the effective `WindowsPackageType` — same as project mode. `None` builds, installs the Windows App Runtime, and launches the `.exe` directly; identity options apply to packaged apps only.
- **Console apps print to the terminal by default.** An app with `OutputType=Exe` is launched through an execution alias rather than AUMID activation, because an AUMID-launched packaged app has no console and would print nothing. Pass `--without-alias` (or set `#:property WinAppRunUseExecutionAlias=false`) to force AUMID; pass `--with-alias` to get one for a windowed app. The alias is named from the effective package *family* name with a `winapp-` prefix; `winapp run` prints the name it registered. An authored manifest's own alias is used as-is unless `--unique-identity` renames it. If another package already owns the name, winapp reports it — falling back to AUMID when it inferred the alias for you, and failing the run when you asked for one explicitly with `--with-alias` or `WinAppRunUseExecutionAlias=true`, rather than launching the wrong app.
- **Rejected options** (the file configures itself): `-f` ⇒ `#:property TargetFramework=…`; `--project` ⇒ not applicable. `--arch`/`-r` work as in project mode and default to the **current winapp process architecture** — required for self-contained WinAppSDK apps, which fail as `AnyCPU`. Everything else works as usual.
- **The package outlives the run.** winapp says so the first time it registers an app. Remove it with `winapp unregister counter.cs` (works with or without `--unique-identity`), or run with `--unregister-on-exit`. See [unregister](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#unregister) for apps with several layouts or a deleted source.
- **`dotnet run counter.cs` works too.** Add `#:package Microsoft.Windows.SDK.BuildTools.WinApp@*` and a Windows TFM (`#:property TargetFramework=net10.0-windows10.0.19041.0`) and the package's targets redirect the run to winapp — same packaged launch, no rebuild, no `winapp` command. A plain `net10.0` file or `#:property WindowsPackageType=None` is left alone and runs unpackaged; `#:property EnableWinAppRunSupport=false` opts out. Diagnose with `dotnet build counter.cs -t:WinAppRunSupportInfo` (`dotnet msbuild` cannot load a `.cs`).
- Requires **.NET SDK 10.0.300+**.

> The default identity includes a short hash of the file's path (`counter.cs` → `counter-a1b2c3d4`), so two `counter.cs` files in different folders are separate apps with separate `LocalState`. It is stable across edits and re-runs, and changes only if the file moves. Set `WinAppPackageName` to pick a stable identity yourself. The Start menu shows `WinAppDisplayName`, not the identity.
