# C++ Sample Application

This sample demonstrates how to use WinAppCLI with a C++ application built with CMake to add package identity and package as MSIX.

For a complete step-by-step guide, see the [C++ Getting Started Guide](../../docs/guides/cpp.md).

## What This Sample Shows

- Basic C++ console application built with CMake
- Using Windows App Model APIs to retrieve package identity
- Configuring CMake to automatically apply debug identity after building in Debug configuration
- MSIX packaging with app manifest and assets

## Prerequisites

- Visual Studio Build Tools or Visual Studio with C++ development tools
- CMake 3.20 or later
- WinAppCLI installed via winget: `winget install Microsoft.WinAppCli`

## Building and Running

### Build the Application

```powershell
cmake -B build
cmake --build build --config Debug
```

### Run

The CMakeLists.txt is configured to automatically apply debug identity when building in Debug configuration. Simply build and run:

```powershell
cmake --build build --config Debug
.\build\Debug\cpp-app.exe
```

Output: `Package Family Name: cpp-app_12345abcde`
