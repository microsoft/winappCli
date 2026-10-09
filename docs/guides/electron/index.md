<!-- mslearn: true -->
<!-- description: Add Windows-native capabilities to an Electron app with the winapp CLI, from calling modern Windows APIs to testing identity and packaging. -->
# Getting Started: Adding Windows APIs to Your Electron App

This guide walks you through adding Windows-native capabilities to an Electron application using the Windows App Development CLI. You'll learn how to call modern Windows APIs from your Electron app, test with app identity, and package for distribution.

## What You'll Build

By the end of this guide, you'll have an Electron app that:
- ✅ Calls modern Windows APIs (Windows SDK and Windows App SDK)
- ✅ Uses Windows AI capabilities from JavaScript (Phi Silica or WinML)
- ✅ Optionally uses a native addon when an API needs native code
- ✅ Runs with app identity for testing protected APIs
- ✅ Packages as a signed MSIX for distribution

## Prerequisites

Before starting, ensure you have:

- **Windows 11** (Copilot+ PC if using Phi Silica)
- **Node.js 22.13 or later** - `winget install OpenJS.NodeJS --source winget`. Check your version with `node --version`, and upgrade if it is older.
- **.NET SDK v10** - `winget install Microsoft.DotNet.SDK.10 --source winget`
- **Visual Studio with the Native Desktop Workload** - `winget install --id Microsoft.VisualStudio.Community --source winget --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --includeRecommended --passive --wait"`

## The Process

Follow these guides to set up your Electron app, call modern Windows APIs, and package it for distribution:

### 1. [Setting Up the Development Environment](setup.md)

First, you'll set up your development environment with the necessary tools and SDKs. This includes:
- Creating or configuring an Electron app
- Installing winapp CLI
- Initializing Windows SDKs and required assets
- Setting up your build pipeline

[Get Started with Setup →](setup.md)

### 2. Call Windows APIs from JavaScript

If you enabled JS bindings during setup, `.winapp/bindings/` contains generated `.js` wrapper classes and matching `.d.ts` declarations for Windows App SDK APIs. Import `#winapp/bindings` to access all exported classes. The `#winapp/bindings` package import requires `@microsoft/dynwinrt-codegen` ≥ `0.1.0-preview.8`; older projects can keep a path relative to the importing file (for example, `require('../.winapp/bindings/index.js')` from `src/index.js`) or upgrade with `npm i -D @microsoft/dynwinrt-codegen@latest && npx winapp init --add-js-bindings`.

- **[Show a Notification from JavaScript →](js-notification.md)** — call the same Windows App SDK notification surface without a native addon.
- **[Call Windows APIs from JavaScript →](js-file-picker.md)** — pick a file with Windows App SDK and inspect it with Windows SDK imaging APIs.
- **[Call Phi Silica from JavaScript →](js-phi-silica.md)** — summarize text with the local language model on Copilot+ PCs.
- **[Run WinML from JavaScript →](js-winml.md)** — use Windows App SDK ML provider discovery with `onnxruntime-node`.

### 3. Optional: Create a Native Addon

Skip this step if generated JS bindings cover the Windows App SDK API you need. Create a native addon only when you need a native boundary, such as Win32, pure COM, native C/C++ libraries, or managed .NET assemblies.

#### Option A: [Creating a C++ Notification Addon](cpp-notification-addon.md)
Learn how to create a C++ addon that calls the Windows App SDK notification APIs. This is a great starting point for understanding native addons before diving into more complex scenarios.

[Create a C++ Notification Addon →](cpp-notification-addon.md)

#### Option B: [Creating a Phi Silica Addon](phi-silica-addon.md)
Learn how to create a C# addon that uses the Phi Silica AI model to summarize text on-device. Phi Silica is a small language model that runs locally on Windows 11 devices with NPUs.

[Create a Phi Silica Addon →](phi-silica-addon.md)

#### Option C: [Creating a WinML Addon](winml-addon.md)
Learn how to create a C# addon that uses Windows Machine Learning (WinML) to run custom ONNX models for image classification, object detection, and more.

[Create a WinML Addon →](winml-addon.md)

### 4. [Packaging for Distribution](packaging.md)

Finally, you'll package your app as an MSIX for distribution. This includes:
- Building your app for production
- Creating and signing an MSIX package
- Testing the installed package
- Understanding distribution options

[Package Your App →](packaging.md)

## Quick Navigation

| Phase | Guide | What You'll Learn |
|-------|-------|-------------------|
| 1️⃣ | [Setup](setup.md) | Install tools, initialize SDKs, configure build pipeline |
| 2️⃣ | [Notification JS bindings](js-notification.md) | Show a Windows App SDK notification directly from JavaScript |
| 2️⃣ | [Windows APIs JS bindings](js-file-picker.md) | Call a Windows App SDK API, then extend bindings to Windows SDK APIs |
| 2️⃣ | [Phi Silica JS bindings](js-phi-silica.md) | Call the local language model directly from JavaScript |
| 2️⃣ | [WinML JS bindings](js-winml.md) | Register WinML execution providers and run ONNX Runtime from JavaScript |
| 3️⃣ | [C++ Notification Addon](cpp-notification-addon.md) | Create C++ addon, call notification APIs, test with debug identity |
| 3️⃣ | [Phi Silica Addon](phi-silica-addon.md) | Create a C# addon alternative for AI APIs |
| 3️⃣ | [WinML Addon](winml-addon.md) | Create a C# addon alternative for WinML |
| 4️⃣ | [Packaging](packaging.md) | Build production app, create MSIX, distribute |

## Additional Resources

- **[winapp CLI Documentation](../../usage.md)** - Full CLI reference
- **[Sample Electron App](../../../samples/electron/)** - Complete working example
- **[AI Dev Gallery](https://aka.ms/aidevgallery)** - Sample gallery of all AI APIs 
- **[Windows App SDK Samples](https://github.com/microsoft/WindowsAppSDK-Samples)** - Collection of Windows App SDK samples
- **[node-api-dotnet](https://github.com/microsoft/node-api-dotnet)** - C# ↔ JavaScript interop library

## Get Help

- **Found a bug?** [File an issue](https://github.com/microsoft/WinAppCli/issues)
