# C++ Sample Application

This sample demonstrates how to use winapp CLI with a C++ application built with CMake to add package identity and package as MSIX.

For a complete step-by-step guide, see the [C++ Getting Started Guide](../../docs/guides/cpp.md).

## What This Sample Shows

- Basic C++ console application built with CMake
- Using Windows App Model APIs to retrieve package identity
- Using `winapp run` to run the app packaged (registers a loose layout package, just like a real MSIX install)
- MSIX packaging with app manifest and assets

## Prerequisites

- Visual Studio Native Desktop workload or Visual Studio with C++ development tools
- CMake 3.20 or later
- WinApp CLI installed

## Building and Running

### Restore dependencies

```powershell
winapp restore
```

### Build the Application

```powershell
cmake -B build
cmake --build build --config Debug
```

### Run without Identity

```powershell
.\build\Debug\cpp-app.exe
```
*Output should be: "Not packaged"*

### Run with Identity (Debug)

```powershell
winapp run .\build\Debug
```
This registers a loose layout package (just like a real MSIX install), then launches the app via its execution alias so console output stays in the current terminal.

*Output should show the Package Family Name.*

> **Note:** A console app is launched through an execution alias automatically, so `--with-alias` is only needed to force one for a windowed app. This sample's `appxmanifest.xml` declares its own `uap5:ExecutionAlias`, which winapp uses as-is; otherwise winapp stages one into the AppX layout for you. Add one to an appxmanifest.xml with `winapp manifest add-alias`.
