# Electron WinML Sample

This sample demonstrates how to integrate Windows Machine Learning (WinML) into an Electron application using the Windows App Development CLI. The app uses the SqueezeNet 1.1 model to classify images directly on the user's device, and the Phi model for text generation.

## What's Included

- **Image Classification**: Uses SqueezeNet 1.1 ONNX model for real-time image classification
- **C# Native Addon**: A .NET 10 Native AOT addon that bridges JavaScript and WinML APIs
- **Hardware Acceleration**: Automatically uses CPU, GPU, or NPU based on device capabilities
- **Production Ready**: Includes MSIX packaging configuration and ASAR handling for distribution

## Features

- 🖼️ **Classify Images**: Select any image and get top predictions with confidence scores
- ⚡ **Fast Performance**: Native AOT compilation with hardware acceleration
- 📦 **MSIX Packaging**: Ready for distribution via Microsoft Store or direct download
- 🎨 **Modern UI**: Simple, clean interface for testing image classification

## Prerequisites

- **Windows 11** or Windows 10 (version 1809+)
- **Node.js** - `winget install OpenJS.NodeJS --source winget`
- **.NET SDK v10** - `winget install Microsoft.DotNet.SDK.10 --source winget`
- **Visual Studio with the Native Desktop Workload** - `winget install --id Microsoft.VisualStudio.Community --source winget --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --includeRecommended --passive --wait"`

## Getting Started

### 1. Install Dependencies

```bash
npm install
```

This automatically runs the `postinstall` script which:
- Restores Windows SDK packages to `.winapp/`
- Generates a development certificate
- Adds debug identity to Electron

### 2. Download the Model
The models are available in the [AI Dev Gallery](https://aka.ms/aidevgallery). Install the gallery to download the models. You don't need both models if you only care about one or the other. The *models* folder will need to be created.


#### SqueezeNet
1. Navigate to the **Classify Image** sample
2. Download the **SqueezeNet 1.1** model
3. Click **Open Containing Folder** to locate the `.onnx` file
4. Copy `squeezenet1.1-7.onnx` to the `models/` folder in this project

#### Phi
1. Navigate to the **Generate Text** sample
2. Download any of the Phi models from Custom models 
3. Click **Open Containing Folder** to locate model files
4. Copy all the contents of the folder (should have .onnx and .json files) to the `models/phi` folder in this project.


### 3. Build the C# Addon

```bash
npm run build-winMlAddon
```

This compiles the C# addon using Native AOT, creating a `.node` binary that requires no .NET runtime on target machines.

### 4. Run the App

```bash
npm start
```

> **Note:** If you encounter a blank window or crash, add `--no-sandbox` to the start script in `package.json` as a workaround for a known Windows issue.


## Learn More

- **[Full Guide](../../docs/guides/electron/winml-addon.md)** - Step-by-step tutorial
- **[winapp CLI Documentation](../../docs/usage.md)** - CLI reference
- **[WinML Documentation](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/overview)** - Official WinML docs
- **[AI Dev Gallery](https://aka.ms/aidevgallery)** 

## Troubleshooting

**App crashes or shows blank window:**
Add `-- --no-sandbox` to the start script in `package.json`.

**Model not found:**
Ensure `squeezenet1.1-7.onnx` is in the `models/` folder.

**Build errors:**
Run `npx winapp restore` to restore SDK packages.

**Certificate errors:**
Reinstall the certificate (as admin): `npx winapp cert install .\devcert.pfx`

## License

See [LICENSE](../../LICENSE) for details.
