---
name: winui-packaging
description: "MSIX packaging, code signing, and distribution for WinUI 3 apps with WinApp CLI 0.7+ — SDK-native project packaging, Native AOT, certificates, self-contained deployment, CI/CD, and Microsoft Store handoff. Use when preparing for release, creating MSIX installers, managing certificates, setting up CI/CD packaging, or publishing to the Microsoft Store."
---

Requires **WinApp CLI 0.7+**. For analyzer setup, see `winui-dev-workflow`.

### Quick Reference

| Task | Command |
|------|---------|
| Release package + sign | `winapp package .\MyApp.csproj --arch x64 --cert .\devcert.pfx` |
| Unsigned multi-architecture bundle | `winapp package .\MyApp.csproj --arch x64 --arch arm64 --no-sign` |
| Generate dev certificate | `winapp cert generate` |
| Trust certificate (admin) | `winapp cert install ./devcert.pfx` |
| Sign existing file | `winapp sign ./app.msix ./devcert.pfx` |
| Bundle Windows App SDK runtime | `winapp package .\MyApp.csproj --arch x64 --cert .\devcert.pfx --self-contained` |

### End-to-End Workflow

#### Step 1: Check the Project and Deployment Intent

- Pass the **explicit project file**, not `.` or a guessed `bin` folder. WinUI project packaging uses SDK-native `dotnet publish` packaging, defaults to **Release**, and preserves project AOT settings.
- Check manifest identity, target architectures, the SDK for the app's TFM, and release warnings.
- For Native AOT, set `<PublishAot>true</PublishAot>` in the project and fix IL/CsWinRT warnings; there is **no `winapp package --aot`**. AOT also needs the MSVC C++ build tools. See [source-generator patterns](references/sourcegen-patterns.md).
- Project packaging rejects `WindowsPackageType=None`; restore the packaged setting first (see `winui-dev-workflow` Critical Rules).

Do **not** run/register/unregister a development package just to produce release artifacts. Project packaging builds without a development `winapp run --no-launch` step. For WinUI SDK-native packaging, do not pass layout overrides `--manifest`, `--executable`, or `--skip-pri`; fix the project/manifest instead.

#### Step 2: Generate Certificate (one-time)
```powershell
winapp cert generate --manifest .\Package.appxmanifest
```
Creates a development `devcert.pfx` (default password: `password`). This **certificate command's** `--manifest` flag auto-matches the `Publisher` field in `Package.appxmanifest`. Keep PFX files/passwords out of source control; production signing needs the organization's signing policy.

#### Step 3: Trust on the Intended Test Machine (optional, admin)
```powershell
winapp cert install ./devcert.pfx
```
Adds the cert to the machine trust store and persists across reboots. Do this only with approval on the intended test machine; it is not required just to build/package in CI.

#### Step 4: Package and Sign
```powershell
winapp package .\MyApp.csproj --arch x64 --cert .\devcert.pfx
# Repeat --arch for a bundle
winapp package .\MyApp.csproj --arch x64 --arch arm64 --no-sign
```
Choose one signing policy: `--cert` takes a **PFX file**, not a certificate thumbprint; its subject must match the manifest's `Identity.Publisher`. `--no-sign` leaves an artifact for external signing. Use the artifact paths reported by the command, not a guessed output directory.

For timestamped production signing, create an unsigned package then sign the resulting artifact:
```powershell
# Replace MyApp.msix with the package/bundle path returned above
winapp sign .\MyApp.msix .\prod.pfx --timestamp http://timestamp.digicert.com
```
`--timestamp` belongs to **`winapp sign`**, not `winapp package`. Use an approved timestamp service and protect the PFX/password.

#### Step 5: Install or Distribute
When installation/testing is part of the task, choose the target per `winui-ui-testing` Step 1, and get consent for certificate trust and dependency provisioning on that machine. Packaging alone is not permission to install an app.

### Self-Contained Does Not Mean Single-File

`winapp package --self-contained` sets only **`WindowsAppSDKSelfContained=true`**, not .NET `SelfContained=true`. For a fully self-contained JIT .NET app, set both in the project:
```xml
<PropertyGroup>
  <SelfContained>true</SelfContained>
  <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
</PropertyGroup>
```
Native AOT covers the .NET runtime but still needs the Windows App SDK choice. Native WinUI runtime files stay alongside the executable/in the package; do not promise a single-file WinUI EXE. Some APIs still require additional MSIX dependencies; see the [deployment guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps).

### CI/CD

On a Windows runner provisioned with CLI 0.7+, the app's .NET SDK, and (for AOT) the native C++ toolchain, the packaging step can be:
```powershell
winapp package .\MyApp.csproj --arch x64 --arch arm64 --no-sign
```

- Committed package references, including the analyzer, restore normally; no machine-local analyzer is needed.
- Archive the reported `.msix`/`.msixbundle` outputs using the CI system's artifact step. No UI launch or development registration is needed.
- Retrieve production signing material through approved secret storage and sign in a separate protected step. Never commit a PFX or install a development root certificate just to build.

### Store Submission

Associate the app with its [Partner Center](https://partner.microsoft.com/dashboard) identity and follow its current submission requirements (package validation, ratings, screenshots, privacy policy). Use the **SDK/Visual Studio packaging workflow** when Store-upload artifacts or resource-package splitting are required. `winapp package` produces package artifacts; it does not publish to the Store or replace that submission workflow.

### Troubleshooting

| Error | Solution |
|-------|----------|
| "Publisher mismatch" | Run `winapp cert generate --manifest` to re-generate |
| "Certificate not trusted" | Run `winapp cert install ./devcert.pfx` as admin |
| "Access denied" | `cert install` needs admin elevation |
| "Certificate file already exists" | Use `--if-exists overwrite` or `--if-exists skip` |
| Manifest missing / layout override rejected | Check the explicit project and its original manifest; do not use `winapp init` or layout overrides to bypass SDK packaging |
| "Package installation failed" | Check signing/trust and the exact conflicting identity; remove a stale registration only if confirmed and authorized |
| Signature invalid after time | Sign with an approved timestamp service via `winapp sign --timestamp` |

### References

| File | Read when... |
|------|-------------|
| [references/sourcegen-patterns.md](references/sourcegen-patterns.md) | Setting up AOT/trimming, JSON source generators, Native AOT readiness, CsWin32 |
