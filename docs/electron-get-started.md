# Getting Started: Adding Windows APIs to Your Electron App

This guide walks you through adding Windows-native capabilities to an Electron application using the Windows App Development CLI. You'll learn how to call modern Windows APIs from your Electron app, test with app identity, and package for distribution.

## What You'll Build

By the end of this guide, you'll have an Electron app that:
- ✅ Calls modern Windows APIs (Windows SDK and Windows App SDK)
- ✅ Uses a native addon with AI capabilities (Phi Silica or WinML)
- ✅ Runs with app identity for testing protected APIs
- ✅ Packages as a signed MSIX for distribution

## The Process

Building a Windows-enabled Electron app involves three main phases:

### 1. [Setting Up the Development Environment](guides/electron/setup.md)

First, you'll set up your development environment with the necessary tools and SDKs. This includes:
- Creating or configuring an Electron app
- Installing winapp CLI
- Initializing Windows SDKs and required assets
- Setting up your build pipeline

**Time:** ~10 minutes | **Difficulty:** Easy

[Get Started with Setup →](guides/electron/setup.md)

### 2. Creating a Native Addon

Next, you'll create a native addon that calls Windows APIs. Choose one of the following guides:

#### Option A: [Creating a Phi Silica Addon](guides/electron/phi-silica-addon.md)
Learn how to create a C# addon that uses the Phi Silica AI model to summarize text on-device. Phi Silica is a small language model that runs locally on Windows 11 devices with NPUs.

**Time:** ~20 minutes | **Difficulty:** Moderate | **Requirements:** Copilot+ PC

[Create a Phi Silica Addon →](guides/electron/phi-silica-addon.md)

#### Option B: [Creating a WinML Addon](guides/electron/winml-addon.md)
Learn how to create an addon that uses Windows Machine Learning (WinML) to run custom ONNX models for image classification, object detection, and more.

**Time:** ~20 minutes | **Difficulty:** Moderate | **Requirements:** Windows 11

[Create a WinML Addon →](guides/electron/winml-addon.md)

### 3. [Packaging for Distribution](guides/electron/packaging.md)

Finally, you'll package your app as an MSIX for distribution. This includes:
- Building your app for production
- Creating and signing an MSIX package
- Testing the installed package
- Understanding distribution options

**Time:** ~10 minutes | **Difficulty:** Easy

[Package Your App →](guides/electron/packaging.md)

## Prerequisites

Before starting, ensure you have:

- **Windows 11** (Copilot+ PC if using Phi Silica)
- **Node.js** - `winget install OpenJS.NodeJS`
- **.NET SDK v10** - `Microsoft.DotNet.SDK.10`
- **Visual Studio with the Native Desktop Workload** - `winget install --id Microsoft.VisualStudio.Community --source winget --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --includeRecommended --passive --wait"`

## Quick Navigation

| Phase | Guide | What You'll Learn |
|-------|-------|-------------------|
| 1️⃣ | [Setup](guides/electron/setup.md) | Install tools, initialize SDKs, configure build pipeline |
| 2️⃣ | [Phi Silica Addon](guides/electron/phi-silica-addon.md) | Create C# addon, call AI APIs, test with debug identity |
| 2️⃣ | [WinML Addon](guides/electron/winml-addon.md) | Create WinML addon, run ONNX models, integrate ML |
| 3️⃣ | [Packaging](guides/electron/packaging.md) | Build production app, create MSIX, distribute |

## Additional Resources

- **[winapp CLI Documentation](usage.md)** - Full CLI reference
- **[Sample Electron App](../samples/electron/)** - Complete working example
- **[AI Dev Gallery](https://aka.ms/aidevgallery)** - Sample gallery of all AI APIs 
- **[Windows App SDK Samples](https://github.com/microsoft/WindowsAppSDK-Samples)** - Collection of Windows App SDK samples
- **[node-api-dotnet](https://github.com/microsoft/node-api-dotnet)** - C# ↔ JavaScript interop library

## Get Help

- **Found a bug?** [File an issue](https://github.com/microsoft/WinAppCli/issues)

Happy coding! 🚀
