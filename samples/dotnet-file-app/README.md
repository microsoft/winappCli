# .NET File-Based App Sample

This sample is a complete Windows app in a single `.cs` file: no `.csproj`, manifest, or image assets. `winapp run` builds it, generates a manifest from its `#:property` directives, and launches it with package identity.

For a complete step-by-step guide, see the [.NET File-Based Apps Guide](../../docs/guides/dotnet-file-based.md). For every option, see [Single-file mode](../../docs/usage.md#single-file-mode-net-file-based-apps) in the usage docs.

## What This Sample Shows

- A .NET 10 [file-based app](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10/sdk#file-based-apps) configured entirely with `#:` directives
- Describing the package identity with `#:property WinAppPackageName` and `WinAppDisplayName`, without writing a manifest
- Reading package identity through Windows Runtime APIs (`Windows.ApplicationModel.Package`)
- Console output staying in the current terminal (a console app is launched through its execution alias automatically)
- Running with plain `dotnet run app.cs` through the `Microsoft.Windows.SDK.BuildTools.WinApp` package

## Prerequisites

- .NET SDK 10.0.300 or newer
- winapp CLI

## Building and Running

```powershell
winapp run app.cs
```

Output:

```
Package Family Name: winapp.samples.fileapp_abc123xyz
```

The package stays registered after the app exits, so its data survives between runs. Remove the registration with:

```powershell
winapp unregister app.cs
```

### Run with `dotnet run`

Because `app.cs` references `Microsoft.Windows.SDK.BuildTools.WinApp`, plain `dotnet run` gives the same packaged launch:

```powershell
dotnet run app.cs
```

If you already ran `winapp run app.cs`, `dotnet run` warns that it is replacing an existing registration installed from a different location. This is expected: both commands register the same app, but build it into different output folders. You can ignore the warning.

## Customize It

Every `#:property` that shapes the package is optional. For example, add capabilities or set a version:

```csharp
#:property WinAppCapabilities=internetClient;microphone
#:property Version=1.2.3
```

To add something the properties don't cover, such as a protocol handler or file association, put your own manifest next to the file as `app.appxmanifest`. winapp then uses it instead of generating one.
