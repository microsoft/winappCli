# Windows Developer CLI

The winsdk command line utility provides tools and helpers for building and packaging Windows applications. It helps with:
* **Using modern Windows APIs** - boostraping and setup of the Windows SDK and Windows App SDK
* **MSIX Packaging** - generating and signing MSIX packages 
* **App Identity** - seting up identity for debugging, or for generating sparse packages for app identity with other packaging formats
* `+` generating and managing **manifests**, **certificates**, **assets**, and more

If you are building an application with any framework (Electron, Qt, Flutter, etc) that is targetting Windows and you are not using the built-in tooling in Visual Studio, this cli is for you.

[Read the docs](./docs)

## To get started

```bash
# install
winget install Microsoft.WinDevCli

# call the cli
winsdk version
```

or if using Electron

```bash
# install
npm i -D @microsoft\windevcli

# call the cli
npx winsdk version
```

### Initialize the project (init)

```bash
winsdk init

# or setup with prerelease versions of the sdks
winsdk init --prerelease
```

This command will
* Generate an appxmanifest, required assets, and development certificate
* Download and make headers and binaries available to your project for the Windows App SDK and the Windows SDK
* Ensure your machine is setup for development by ensuring the build tools are available, dev mode is enabled, and the Win App SDK runtime is installed
* Generate a `winsdk.yaml` file in your project that contains the versions of the sdks - use this to manage versions of dependencies (calling `winsdk init` again or just `winsdk restore` will update your project with the specified versions if you modify this file)


### Generate app identity

```bash
winsdk create-debug-identity <exe>
```

This command will generate a temporary identity for your application to allow for debugging with identity. It will use the `appxmanifest.xml` you generated to modify the [side-by-side](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests) manifest in your exe with the debug identity and register a [sparse package](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps). All this means is, the next time you run your exe, it will have identity allowing you to debug apis that need identity without doing a full MSIX packaging.
> Note: if you modify your appxmanifest.xml after running the above command, run the command  again to ensure the registration contains the new manifest changes.

### MSIX packaging

```bash
winsdk package <folder to package>
```

This command will generate a MSIX package from a folder. It will use an appxmanifest in your root, in the folder, or you can specify a path to an appxmanifest. This command will also ensure your final appxmanifest has the right framework dependencies, update the side by side manifest with required registrations, and even handle self containing winappsdk. You can also provide a certificate to sign the package (or generate a new one for debugging).


### Manifests, certificates, and tools

The cli also contains commands for generating, updating, and validating appxmanifests (`winsdk manifest`), creating and installing dev certificates (`winsdk cert`), and calling Windows sdk tools (`winsdk tool`).  

## Electron/NodeJS

The cli is available as an npm package that can be used with your Electron applications during development. To use it, install it with

```
npm i -D @microsoft\windevcli
```

You can call the cli with `npx` or use it programaticaly

```js
TODO, programatic usage
```

In addition to the above commands, the npm package contains specific electron commands to help in debugging:

### Generate electron debug identity

```bash
npx winsdk node add-electron-debug-identity
```

This command register debug identity for the electron.exe process. When electron.exe runs, it will contain identity, allowing you to debug and step through code that requires app identity without having to package your electron application as MSIX.
> Note: if you modify your appxmanifest.xml after running the above command, run the command  again to ensure the registration contains the new manifest changes.

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
