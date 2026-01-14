# WPF Sample Application

This sample demonstrates how to use winapp CLI with a WPF application to add package identity and package as MSIX.

For a complete step-by-step guide, see the [.NET Getting Started Guide](../../docs/guides/dotnet.md) - the same steps apply to WPF, WinForms, and other .NET UI frameworks.

## What This Sample Shows

- WPF desktop application with modern UI
- Using Windows Runtime APIs to retrieve package identity
- Configuring MSBuild to automatically apply debug identity after building in Debug configuration
- Using Windows App SDK via NuGet for modern Windows APIs
- MSIX packaging with app manifest and assets

## Prerequisites

- .NET 10.0 SDK
- winapp CLI installed via winget: `winget install Microsoft.winappcli`

## Building and Running

### Run 

The `.csproj` is configured to automatically apply debug identity when building in Debug configuration:

```powershell
dotnet run
```

The WPF window will display:
```
Package Family Name: wpf-app.debug_12345abcde
Windows App Runtime Version: 1.8-stable (1.8.0)
```

### Package as MSIX

The `.csproj` is also configured to automatically package when building in Release mode:

```powershell
# Create a dev certificate (first time only)
winapp cert generate --if-exists skip

# Build and package in one command
dotnet build -c Release

# Install certificate (first time only, requires admin)
winapp cert install .\devcert.pfx

# Install the generated MSIX
# The .msix file will be in the root directory
```

Double-click the `.msix` file to install. The app will be available in your Start Menu and can be launched like any other installed app.