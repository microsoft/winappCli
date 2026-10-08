# C++ WinUI 3 Sample Application

A packaged WinUI 3 app written in C++/WinRT with a XAML UI. The project is the Visual Studio
**WinUI Blank App (Packaged)** template (`.vcxproj` + `packages.config`), with a button that shows the
app's package identity.

## Prerequisites

- Visual Studio or Build Tools for Visual Studio with the **Desktop development with C++** workload
  and **C++ WinUI app development tools**
- The Windows SDK (installed with the workload)

## Building and Running

Point `winapp run` at the project (or this folder). It restores the NuGet packages, builds with
Visual Studio's MSBuild, registers the build output as a loose-layout package, and launches it:

```powershell
# Build + run in one step
winapp run .

# Launch detached for automation
winapp run .\CppWinUIApp.vcxproj --detach --json

# Run the existing build without rebuilding
winapp run . --no-build
```

Click **Show package identity** to see the package family name the app is running under.

## Testing with winapp ui

```powershell
winapp ui invoke "Show package identity" -a CppWinUIApp
winapp ui search "Package family name" -a CppWinUIApp
```
