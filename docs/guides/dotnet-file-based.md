<!-- mslearn: true -->
<!-- description: Run a single-file .NET 10 app with package identity using winapp run, then name the package, add capabilities, and launch it with dotnet run. -->
# Using winapp CLI with .NET file-based apps

> For a working example, see the [dotnet-file-app sample](../../samples/dotnet-file-app). For the complete reference, see [Single-file mode](../usage.md#single-file-mode-net-file-based-apps) in the CLI reference.

.NET 10 can run a single `.cs` file with no project file. This guide shows how to run that file with package identity using `winapp run`, so APIs such as `Windows.ApplicationModel.Package.Current`, app notifications, and on-device AI work. You don't author a manifest or image assets: winapp generates them for you.

Package identity is a core concept in the Windows app model. It allows your application to access specific Windows APIs, have a clean install/uninstall experience, and more. A file-based app run with `dotnet run` has no package identity.

## Prerequisites

1.  **.NET SDK 10.0.300 or later**: Install the .NET 10 SDK (requires a restart after installation):
    ```powershell
    winget install Microsoft.DotNet.SDK.10 --source winget
    ```

2.  **winapp CLI**: Install the `winapp` tool via winget (or update if already installed):
    ```powershell
    winget install Microsoft.winappcli --source winget
    ```

## 1. Create a file-based app

Create a folder with a file named `hello.cs`:

```csharp
#:property OutputType=Exe
#:property TargetFramework=net10.0-windows10.0.19041.0

using Windows.ApplicationModel;

try
{
    Console.WriteLine($"Package Family Name: {Package.Current.Id.FamilyName}");
}
catch (InvalidOperationException)
{
    // Thrown when the app doesn't have package identity
    Console.WriteLine("Not packaged");
}
```

The `#:property` lines configure the app the way a `.csproj` would. The Windows `TargetFramework` gives the code access to Windows Runtime APIs such as `Package`.

Run it with `dotnet run`:

```powershell
dotnet run hello.cs
```

The output is `Not packaged`: the app runs without package identity.

## 2. Run with package identity

Run the same file with winapp:

```powershell
winapp run hello.cs
```

winapp builds the app, generates a manifest and default image assets in the build output, registers the app as a package, and launches it. The output now shows the package identity:

```
Package Family Name: hello-<hash>_<publisher-hash>
```

Your source folder still contains only `hello.cs`. Because this is a console app (`OutputType=Exe`), winapp launches it through an execution alias, so its output appears in your terminal.

The registration stays in place after the app exits, so running `winapp run hello.cs` again reuses the same identity. You remove it in [step 5](#5-clean-up).

## 3. Name your package

By default, the package name is the file name plus a short hash of the file's path. To choose your own name, unregister the current package first, because a new name registers a new package:

```powershell
winapp unregister hello.cs
```

Then add `#:property` directives to the top of `hello.cs`:

```csharp
#:property WinAppPackageName=com.contoso.hello
#:property WinAppDisplayName=Contoso Hello
#:property Version=1.0.0
```

Run it again:

```powershell
winapp run hello.cs
```

The output shows the new name, for example `Package Family Name: com.contoso.hello_<publisher-hash>`. The app also appears in the Start menu as **Contoso Hello**.

For every identity property and its default, see [Manifest properties](../usage.md#single-file-mode-net-file-based-apps).

## 4. Run with `dotnet run`

To get the same packaged launch from plain `dotnet run`, reference the `Microsoft.Windows.SDK.BuildTools.WinApp` package at the top of `hello.cs`:

```csharp
#:package Microsoft.Windows.SDK.BuildTools.WinApp@*
```

```powershell
dotnet run hello.cs
```

The output shows the same package identity as `winapp run`. Because `dotnet run` builds into a different output folder, it warns that it is replacing an existing registration installed from a different location. This is expected, and you can ignore it.

The package also gives the app a placeholder window icon, so a windowed app shows an icon in its title bar. To use your own, point `ApplicationIcon` at an `.ico` file:

```csharp
#:property ApplicationIcon=Assets\MyApp.ico
```

For the properties that control this launch, see [Running with `dotnet run`](../usage.md#running-with-dotnet-run).

## 5. Clean up

Remove the registration by passing the `.cs` file to `winapp unregister`:

```powershell
winapp unregister hello.cs
```

## Add capabilities or a custom manifest

Some APIs need a declared capability. For example, the Windows AI APIs need `systemAIModels`:

```csharp
#:property WinAppCapabilities=systemAIModels
```

winapp writes each capability into the correct manifest element. See [Capabilities](../usage.md#capabilities) for the full list and naming rules.

Protocol handlers, file associations, and other shell integrations need an authored manifest. Put a `hello.appxmanifest` next to `hello.cs` and winapp uses it instead of generating one. See [Bring your own manifest](../usage.md#bring-your-own-manifest).

## Next Steps

- **Package for distribution**: `winapp run` registers the app for development only. To produce an MSIX you can ship, move your code into a project and follow [Package with MSIX](dotnet.md#7-package-with-msix) in the .NET guide.
- **Explore Windows APIs**: With package identity, you can now use [Notifications](https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart), [on-device AI](https://learn.microsoft.com/windows/ai/apis/), and other [identity-dependent APIs](https://learn.microsoft.com/windows/apps/desktop/modernize/desktop-to-uwp-extensions)
