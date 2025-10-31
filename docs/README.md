# Windows Development CLI Documentation

The winapp command line utility provides tools and helpers for building and packaging Windows applications. It helps with:
* **Using modern Windows APIs** - bootstrapping and setup of the Windows SDK and Windows App SDK
* **MSIX Packaging** - generating and signing MSIX packages 
* **App Identity** - setting up identity for debugging, or for generating sparse packages for app identity with other packaging formats
* `+` generating and managing **manifests**, **certificates**, **assets**, and more

If you're building a Windows application with cross-platform frameworks like Electron, Qt, or Flutter - or with any non-MSBuild/Visual Studio workflows like CMake - this CLI is for you.

<!-- 

## Quickstart

```bash
# install
winget install Microsoft.winapp

# call the cli
winapp --help
```

or if using Electron

```bash
# install
npm i -D @microsoft/winappcli

# call the cli
npx winapp --help
```
## Table of contents

- What does `init` do?
- Using Windows SDK and the Windows App SDK APIs
- Debugging with app identity
- MSIX packaging
    - Packaging with Windows App SDK ("framework-dependent" vs "self contained")
    - Sparse packaging
- Manifests, Certificates, and SDK Tools
- How does the cli manage nuget packages
- Using the cli in CI environments
- CLI extensions
    - Electron
    - CMAKE


## Samples\guides

- [GUIDE] Electron quick start (init, debug, package)
- [SAMPLE] Electron calling WinAI APIs (OCR)
- [SAMPLE] Electron registering App Actions
- [SAMPLE] Electron sparse packaging with squirel/msi

- [GUIDE] Qt quick start (init, debug, package)
- [SAMPLE] Qt calling WinAI APIs (OCR) -->


## Known issues

1. **Debug identity and sparse packaging in Electron** - There is a known bug in Windows that breaks Electron apps when they are sparse packaged. The bug is fixed in Windows, but it is not yet available publicaly (ETA end of year). Until then, the workaround is to disable sandboxing in your Electron apps for debugging. Add `app.commandLine.appendSwitch('--no-sandbox')` to your main process to disable sandobing temporarily. 