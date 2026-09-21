<!-- mslearn: true -->
<!-- description: What winapp CLI development certificates and Developer Mode change on your machine, safe handling of devcert.pfx, and production signing. -->
# Security guidance

The winapp CLI makes local Windows development straightforward: it can generate a signing certificate, trust it on your machine, and turn on Developer Mode for you. Each of those steps changes machine state or creates a file that carries a private key, so it helps to know exactly what they do.

This page explains the consequence of each command, how to undo it, and what to do differently when you ship. Development certificates and Developer Mode are the normal, supported path for local testing — the goal here is that you understand what you are opting into, not that you avoid them.

## Development certificates

MSIX packages must be signed before Windows will install them. For local testing, [`winapp cert generate`](usage.md#cert-generate) creates a self-signed certificate so you can sign and install your own package without buying anything.

### What `winapp cert generate` creates

The generated certificate is a self-signed, end-entity code-signing certificate:

| Property | Value |
|----------|-------|
| Key | RSA 2048-bit, marked exportable |
| Signature algorithm | SHA-256 with RSA (PKCS#1 v1.5) |
| Key usage | Digital signature |
| Enhanced key usage | Code signing (`1.3.6.1.5.5.7.3.3`) |
| Basic constraints | Not a certificate authority |
| Validity | 365 days by default (`--valid-days`) |
| Subject | Must match the `Publisher` in your manifest |

The command writes two things:

- `devcert.pfx` in the current directory (or the path you pass to `--output`). This file contains **both** the certificate and its private key.
- A copy of the certificate in your personal certificate store (`Cert:\CurrentUser\My`).

With `--export-cer`, it also writes a `.cer` file next to the `.pfx`. That file contains the public certificate only — no private key — which makes it the right thing to hand to a teammate or a test machine that needs to trust your builds.

> [!NOTE]
> A self-signed certificate is trusted by nobody until someone explicitly trusts it. It is fine for your own machine and your own test machines; it is not a substitute for a real code-signing identity when you distribute your app.

### The default password

`winapp cert generate` uses `password` as the PFX password unless you pass `--password`. The same default applies when you later supply that certificate to [`winapp sign`](usage.md#sign), whose password option is also `--password`, and to [`winapp pack`](usage.md#pack), which takes `--cert-password`.

A well-known password means the private key in `devcert.pfx` is effectively unprotected — anyone who obtains the file can sign code with it. That is an acceptable trade-off for a throwaway certificate that only ever signs local test builds on your own machine, and it is why the default exists.

> [!IMPORTANT]
> Treat the default password as a signal that the certificate is disposable. If a certificate is ever used to sign something another person will install, it should not be a `winapp cert generate` certificate with the default password — see [Signing for production](#signing-for-production).

### Where the certificate file lives

`devcert.pfx` is a private key on disk. Two rules keep it out of trouble:

**Do not commit it.** `winapp cert generate` automatically appends the certificate's filename to the `.gitignore` next to it, so the default flow is already covered. If you move the file, rename it, or generate it into a directory managed by a different `.gitignore`, check that the entry followed it:

```powershell
git check-ignore -v devcert.pfx
```

If that prints nothing, the file is *not* ignored — add it before you commit.

**Do not package it.** [`winapp pack`](usage.md#pack) packages everything in the input directory, so a `devcert.pfx` sitting in your app's output folder ends up inside the shipped MSIX. Generate the certificate outside the folder you package, as the [Packaging an EXE/CLI guide](guides/packaging-cli.md) shows, and confirm it is absent before you distribute:

```powershell
# Unpack the package and check that no certificate is inside
winapp tool makeappx unpack /p .\MyApp.msix /d .\inspect /o
Get-ChildItem .\inspect -Recurse -Include *.pfx, *.cer
```

> [!TIP]
> If a `.pfx` with a real private key ever does get committed or published, rotate it: generate a new certificate, re-sign, and stop trusting the old one using the steps in [Removing a trusted certificate](#removing-a-trusted-certificate). Deleting the file from a later commit does not remove it from history.

### What `winapp cert install` grants

[`winapp cert install`](usage.md#cert-install) adds the certificate to the **`LocalMachine\TrustedPeople`** store. This requires administrator privileges, because it changes trust for every user on the machine.

Once a certificate is in `TrustedPeople`, Windows will accept **any** MSIX package signed by that certificate as trusted enough to install — not just the package you were testing. For a certificate whose private key you hold and keep locally, that is exactly the intended effect. It is also the reason to be deliberate about it:

- Trust certificates you generated yourself, or that come from someone you would let install software on the machine.
- Do not install a development certificate on shared, production, or build machines that other people rely on.
- Prefer distributing the `.cer` (public key only) rather than the `.pfx` when a colleague needs to install your test package. They gain the ability to trust your builds without gaining the ability to sign as you.

To trust a `.cer` on another test machine, run `winapp cert install` on it directly — the command accepts either a `.pfx` or a public-only `.cer`:

```powershell
# Run as Administrator
winapp cert install .\devcert.cer
```

The equivalent using only built-in Windows tooling is:

```powershell
# Run as Administrator
Import-Certificate -FilePath .\devcert.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

### Removing a trusted certificate

Development certificates expire after a year by default, but expiry is not removal. When you are finished with a certificate — the project ended, the machine is being repurposed, or the key may have leaked — remove it explicitly.

First, find its thumbprint:

```powershell
Get-ChildItem Cert:\LocalMachine\TrustedPeople |
    Where-Object { $_.Subject -like '*CN=Contoso*' } |
    Format-List Subject, Thumbprint, NotAfter
```

Then remove it from the machine trust store. This step needs elevation:

```powershell
# Run as Administrator. Replace with the thumbprint from the previous command.
$thumbprint = 'ABCD...'
Remove-Item -Path "Cert:\LocalMachine\TrustedPeople\$thumbprint"
```

`cert generate` also placed the certificate, along with its private key, in your personal store. Remove that from a **normal, non-elevated prompt, signed in as the account that ran `cert generate`**:

```powershell
$thumbprint = 'ABCD...'
Remove-Item -Path "Cert:\CurrentUser\My\$thumbprint"
```

> [!IMPORTANT]
> Run the two commands above in the contexts shown. If you elevated using a different administrator account, `Cert:\CurrentUser` in that elevated session is that administrator's store — not yours — so the private key would be left behind in the generating user's store.

Finally, delete the `.pfx` and any `.cer` copies you handed out, and unregister packages you sideloaded with it:

```powershell
winapp unregister
```

> [!NOTE]
> Removing the certificate does not uninstall packages that were already installed with it. Uninstall those separately through **Settings > Apps > Installed apps**, or with [`winapp unregister`](usage.md#unregister) for packages registered in development mode.

## Developer Mode

Windows requires Developer Mode to register an app package directly from a folder on disk — a *loose layout* — instead of installing a built, signed MSIX. Commands such as [`winapp run`](usage.md#run) and [`create-debug-identity`](usage.md#create-debug-identity) rely on that and fail without it, and [`winapp init`](usage.md#init) offers to turn it on for you.

### What enabling it changes

The CLI enables Developer Mode by writing two `DWORD` values under `HKEY_LOCAL_MACHINE`:

```text
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock
    AllowDevelopmentWithoutDevLicense = 1
    AllowAllTrustedApps               = 1
```

Because these are machine-wide settings, the CLI launches an elevated helper process and Windows shows a **User Account Control** prompt. Nothing is changed if you decline the prompt.

Practically, this means the machine will:

- Register app packages directly from a folder on disk, without them being built into an MSIX or signed at all (`AllowDevelopmentWithoutDevLicense`).
- Install app packages from outside the Microsoft Store as long as they are signed by a certificate the machine trusts — including any development certificate in `TrustedPeople` (`AllowAllTrustedApps`).

> [!IMPORTANT]
> Developer Mode plus a trusted development certificate is a deliberate loosening of the default install restrictions. That combination belongs on development and test machines. Leave it off on production machines, kiosks, and shared infrastructure.

### Controlling when it is enabled

`winapp init` asks before changing anything, and `--use-defaults` skips the question entirely, leaving Developer Mode untouched. That makes scripted and CI runs safe by default:

```powershell
winapp init --use-defaults
```

If you would rather manage the setting yourself, enable it once through **Settings > System > For developers > Developer Mode** and the CLI will detect it and move on.

### Turning it off

Use **Settings > System > For developers** and switch **Developer Mode** off. This is the recommended path, because Settings also cleans up the associated OS state. To confirm the registry value afterwards:

```powershell
Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' `
    -Name AllowDevelopmentWithoutDevLicense, AllowAllTrustedApps
```

Turning Developer Mode off does not remove trusted certificates or already-installed packages — see [Removing a trusted certificate](#removing-a-trusted-certificate).

## Signing for production

A development certificate only works for people who have explicitly trusted it. To distribute your app, sign it with an identity that Windows already trusts.

### Choose a signing identity

- **[Azure Trusted Signing](https://azure.microsoft.com/products/trusted-signing)** — a cloud-managed signing service. The private key never exists on your build machine, so there is no `.pfx` to protect, leak, or rotate by hand. Use [`winapp az-sign`](usage.md#az-sign), which authenticates with the standard Azure credential chain and works with GitHub Actions OIDC or a managed identity.

  ```powershell
  winapp az-sign .\MyApp.msix
  ```

- **A code-signing certificate from a trusted certificate authority** — pass it to [`winapp sign`](usage.md#sign) as the second positional argument, with its password in `--password`. You are then responsible for storing the key material safely; keep it in a hardware token, a key vault, or your CI provider's secret store, and never in the repository.

- **The Microsoft Store** — if you distribute exclusively through the Store, it signs the package for you and you do not need to sign before submission.

In every case the certificate subject must match the `Publisher` value in your manifest, including for [sparse packages](guides/sparse.md).

### Keep signing secrets out of the repository

Certificate passwords belong in your CI secret store, not in a config file. Read them from the environment instead of hard-coding them:

```powershell
winapp sign .\MyApp.msix $env:SIGNING_CERT_PATH --password $env:SIGNING_CERT_PASSWORD
```

The same applies to build configuration checked into source control, such as an Electron Forge config — see [Electron packaging](guides/electron/packaging.md). `winapp az-sign` avoids the problem entirely, because there is no password to pass.

## Before you publish

A short checklist for the transition from local testing to distribution:

- The package is signed with a CA-issued certificate, Azure Trusted Signing, or submitted to the Store — not with `devcert.pfx`.
- No `.pfx` or `.cer` file is inside the packaged output.
- No certificate password appears in committed files, build scripts, or CI logs.
- The certificate subject matches the manifest `Publisher`.
- Development certificates and Developer Mode are not enabled on machines that only need to *run* the app.

## Reporting a security issue

To report a security vulnerability in the winapp CLI itself, follow the process in [SECURITY.md](../SECURITY.md). Please do not open a public GitHub issue for security reports.

## Related topics

- [CLI reference](usage.md)
- [Debugging with package identity](debugging.md)
- [Packaging an EXE/CLI](guides/packaging-cli.md)
- [Sparse packaging](guides/sparse.md)
- [Electron packaging](guides/electron/packaging.md)
- [Sign an app package using SignTool](https://learn.microsoft.com/windows/msix/package/sign-app-package-using-signtool)
- [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/)
