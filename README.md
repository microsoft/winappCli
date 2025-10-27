# Windows Development CLI

The winsdk command line utility provides tools and helpers for building and packaging Windows applications. It helps with:
* **Using modern Windows APIs** - bootstrapping and setup of the Windows SDK and Windows App SDK
* **MSIX Packaging** - generating and signing MSIX packages 
* **App Identity** - setting up identity for debugging, or for generating sparse packages for app identity with other packaging formats
* `+` generating and managing **manifests**, **certificates**, **assets**, and more

If you're building a Windows application with cross-platform frameworks like Electron, Qt, or Flutter - or with any non-MSBuild/Visual Studio workflows like CMake - this CLI is for you.

## Installation

### 📦 Stable Releases

Stable releases via WinGet and NPM will be available when the project becomes public.

---

### 🌙 Nightly Dev Builds

Download automated nightly builds from GitHub Releases.

**[👉 Download Latest Nightly Build](https://github.com/microsoft/winsdk/releases/latest)**

**Available Options:**

| Package | Description | Use Case |
|---------|-------------|----------|
| **`binaries-[version].zip`** | 📦 Standalone Binaries | Portable, no install needed - great for CI/CD |
| **`microsoft-winsdk-[version].tgz`** | 📚 NPM Package | For Node.js/Electron projects |

## Quick start

Once installed (see [Installation](#installation) above), verify the installation by calling the CLI:

```bash
winsdk --help
```

or if using Electron/NodeJS

```bash
npx winsdk --help
```

### Initialize the project (init)

```bash
winsdk init

# or setup with prerelease versions of the sdks
winsdk init --prerelease
```

This command will:
* ✅ Generate an appxmanifest, required assets, and development certificate
* ✅ Download/generate headers and binaries for Windows App SDK and Windows SDK
* ✅ Set up your machine for development (build tools, dev mode, runtime)
* ✅ Create a `winsdk.yaml` configuration file to manage SDK versions
  
> **Tip:** Modify `winsdk.yaml` to change SDK versions, then run `winsdk init` or `winsdk restore` to update your project.

### Hosted Apps (Python/Node.js scripts)

The CLI supports packaging Python and Node.js scripts as MSIX packages using the [Hosted App model](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/hosted-apps). This allows your scripts to gain Windows app identity and capabilities without bundling a full runtime.

#### Generate a Hosted App manifest

```bash
winsdk manifest generate --template hostedapp --entrypoint app.py
```

This command will:
* ✅ Create an `appxmanifest.xml` configured for hosted apps
* ✅ Auto-detect the script type (Python `.py` or JavaScript `.js`)
* ✅ Configure the appropriate host runtime dependency (Python314 or Nodejs22)
* ✅ Generate required assets in the `Assets` folder

**Supported script types:**
- **Python scripts** (`.py`) - Uses Python314 host runtime
- **JavaScript/Node.js scripts** (`.js`) - Uses Nodejs22 host runtime

#### Debug identity for hosted apps

You can also create debug identity for hosted apps to test them without full MSIX packaging:

```bash
# Generate hosted app manifest
winsdk manifest generate --template hostedapp --entrypoint app.py

# Create debug identity (registers as a sparse package)
winsdk create-debug-identity
```

> **Note:** The hosted app model requires the appropriate runtime (Python 3.14+ or Node.js 22+) to be installed on the target system. The manifest specifies this as a runtime dependency.

### Generate app identity

```bash
winsdk create-debug-identity <exe>
```

This command generates a temporary identity for your application to enable debugging with identity. It uses your `appxmanifest.xml` to modify the [side-by-side](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests) manifest in your exe and register a [sparse package](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps). The next time you run your exe, it will have identity, allowing you to debug APIs that require identity without doing a full MSIX packaging.
> **Note:** If you modify your appxmanifest.xml after running the above command, run the command again to ensure the registration contains the new manifest changes.

### MSIX packaging

```bash
winsdk package <folder to package>
```

This command generates an MSIX package from a folder. The CLI will:
- Use an appxmanifest from your root, the folder, or a custom path
- Ensure proper framework dependencies in the manifest
- Update side-by-side manifests with required registrations
- Handle self-contained WinAppSDK deployment
- Sign with a provided certificate (or generate one for debugging)


### Manifests, certificates, and tools

The cli also contains commands for generating, updating, and validating appxmanifests (`winsdk manifest`), creating and installing dev certificates (`winsdk cert`), and calling Windows sdk tools (`winsdk tool`).  

## Electron/NodeJS

The CLI is available as an npm package for Electron and Node.js projects. Install via the nightly build `.tgz` file (see [Installation](#installation) above) or via npm when stable releases are available.

You can call the CLI with `npx` or use it programmatically:

```js
TODO, programmatic usage
```

In addition to the above commands, the npm package contains specific electron commands to help in debugging:

### Generate electron debug identity

```bash
npx winsdk node add-electron-debug-identity
```

This command registers debug identity for the electron.exe process. When electron.exe runs, it will contain identity, allowing you to debug and step through code that requires app identity without having to package your electron application as MSIX.
> **Note:** If you modify your appxmanifest.xml after running the above command, run the command again to ensure the registration contains the new manifest changes.

### Templates for node addon

```bash
npx winsdk node create-addon --name myAddon
```

This command will create a new C++ addon with examples of usage to call the Windows App SDK and Windows SDK.

## Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit [Contributor License Agreements](https://cla.opensource.microsoft.com).

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft
trademarks or logos is subject to and must follow
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.
