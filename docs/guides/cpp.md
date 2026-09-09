<!-- mslearn: true -->
<!-- description: Set up a C++ CMake project with the winapp CLI to restore Windows App SDK headers, generate a certificate, and package the app as MSIX. -->
# Using winapp CLI with C++ and CMake

This guide demonstrates how to use the `winapp` CLI with a C++ application to debug with package identity and package your application as an MSIX.

Package identity is a core concept in the Windows app model. It allows your application to access specific Windows APIs (like Notifications, Security, AI APIs, etc), have a clean install/uninstall experience, and more.

A standard executable (like one created with `cmake --build`) does not have package identity. This guide shows how to add it for debugging and then package it for distribution.

## Prerequisites

1.  **Build Tools**: Use a compiler toolchain supported by CMake. This example uses Visual Studio. You can install the community edition with (or update if already installed):
    ```powershell
    winget install --id Microsoft.VisualStudio.Community --source winget --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --includeRecommended --passive --wait"
    ```
    Reboot after installation. 

2.  **CMake**: Install CMake (or update if already installed):
    ```powershell
    winget install Kitware.CMake --source winget
    ```

3.  **winapp CLI**: Install the `winapp` cli via winget (or update if already installed):
    ```powershell
    winget install Microsoft.winappcli --source winget
    ```

## 1. Create a New C++ App

Start by creating a simple C++ application. Create a new directory for your project:

```powershell
mkdir cpp-app
cd cpp-app
```

Create a `main.cpp` file with a basic "Hello, world!" program:

```cpp
#include <iostream>

int main() {
    std::cout << "Hello, world!" << std::endl;
    return 0;
}
```

Create a `CMakeLists.txt` file to configure the build:

```cmake
cmake_minimum_required(VERSION 3.20)
project(cpp-app)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(cpp-app main.cpp)
```

Build and run it to make sure everything is working:

```powershell
cmake -B build
cmake --build build --config Debug
.\build\Debug\cpp-app.exe
```
*Output should be "Hello, world!"*

## 2. Update Code to Check Identity

We'll update the app to check if it's running with package identity. This will help us verify that identity is working correctly in later steps. We'll use the Windows Runtime C++ API to access the Package APIs.

First, add the following line to the end of your `CMakeLists.txt` to link against the Windows App Model library:

```cmake
# Link Windows Runtime libraries
target_link_libraries(cpp-app PRIVATE WindowsApp.lib OneCoreUap.lib)
```

Next, replace the entire contents of `main.cpp` with the following code. This code attempts to retrieve the current package identity using the Windows Runtime API. If it succeeds, it prints the Package Family Name; otherwise, it prints "Not packaged".

```cpp
#include <iostream>
#include <windows.h>
#include <appmodel.h>

int main() {
    UINT32 length = 0;
    LONG result = GetCurrentPackageFamilyName(&length, nullptr);
    
    if (result == ERROR_INSUFFICIENT_BUFFER) {
        // We have a package identity
        std::wstring familyName;
        familyName.resize(length);
        
        result = GetCurrentPackageFamilyName(&length, familyName.data());
        
        if (result == ERROR_SUCCESS) {
            std::wcout << L"Package Family Name: " << familyName.c_str() << std::endl;
        } else {
            std::wcout << L"Error retrieving Package Family Name" << std::endl;
        }
    } else {
        // No package identity
        std::cout << "Not packaged" << std::endl;
    }

    return 0;
}
```

## 3. Run Without Identity

Now, rebuild and run the app as usual:

```powershell
cmake --build build --config Debug
.\build\Debug\cpp-app.exe
```

You should see the output "Not packaged". This confirms that the standard executable is running without any package identity.

## 4. Initialize Project with winapp CLI

The `winapp init` command sets up everything you need in one go: app manifest, assets, and optionally Windows App SDK headers for C++ development.

Run the following command and follow the prompts:

```powershell
winapp init .
```

When prompted:
- **Package name**: Press Enter to accept the default (cpp-app)
- **Publisher name**: Press Enter to accept the default or enter your name
- **Version**: Press Enter to accept 1.0.0.0
- **Entry point**: Press Enter to accept the default (cpp-app.exe)
- **Setup SDKs**: Select "Stable SDKs" to download Windows App SDK and generate C++ headers

This command will:
- Create `Package.appxmanifest` — the manifest that defines your app's identity
- Create `Assets` folder — icons required for MSIX packaging and Store submission
- Create a `.winapp` folder with Windows App SDK headers and libraries
- Create a `winapp.yaml` configuration file for pinning SDK versions

You can open `Package.appxmanifest` to further customize properties like the display name, publisher, and capabilities.

### Add Execution Alias (for console apps)

An execution alias lets users run your app by name from any terminal (like `cpp-app`). winapp also uses it during development, so `winapp run` keeps console output in the current terminal instead of opening a new window.

You can add one automatically:

```powershell
winapp manifest add-alias
```

Or manually: open `Package.appxmanifest` and add the `uap5` namespace to the `<Package>` tag if it's missing, and then add the extension inside `<Applications><Application><Extensions>...`:

```diff
<Package
  ...
  xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
+ xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
  IgnorableNamespaces="uap uap2 uap3 rescap desktop desktop6 uap10">

  ...
  <Applications>
    <Application ...>
      ...
+     <Extensions>
+       <uap5:Extension Category="windows.appExecutionAlias">
+         <uap5:AppExecutionAlias>
+           <uap5:ExecutionAlias Alias="cpp-app.exe" />
+         </uap5:AppExecutionAlias>
+       </uap5:Extension>
+     </Extensions>
    </Application>
  </Applications>
</Package>
```

## 5. Debug with Identity

To test features that require identity (like Notifications) without fully packaging the app, you can use `winapp run`. This registers a loose layout package (just like a real MSIX install) and launches the app in one step. No certificate or signing is needed for debugging.

1.  **Build the executable**:
    ```powershell
    cmake --build build --config Debug
    ```

2.  **Run with identity**:
    ```powershell
    winapp run .\build\Debug
    ```

A console app is launched via its execution alias automatically, so its output stays in the current terminal — winapp detects that from the built binary and uses the `uap5:ExecutionAlias` we added in step 4. Pass `--without-alias` to force AUMID activation instead, in which case the app prints nothing here.

> [!TIP]
> `winapp run` also registers the package on your system. This is why the MSIX may appear as "already installed" when you try to install it later in step 8. Use `winapp unregister` to clean up development packages when done.

You should now see output similar to:
```
Package Family Name: cpp-app_12345abcde
```
This confirms your app is running with a valid package identity!

### Alternative: Sparse package identity

If you need sparse package behavior specifically (identity without copying files), you can use `create-debug-identity` instead:

```powershell
winapp create-debug-identity .\build\Debug\cpp-app.exe
.\build\Debug\cpp-app.exe
```

> [!TIP]
> For advanced debugging workflows (attaching debuggers, IDE setup, startup debugging), see the [Debugging Guide](../debugging.md).

## 6. Using Windows App SDK (Optional)

If you selected to setup the SDKs during `winapp init`, you now have access to Windows App SDK headers in the `.winapp/include` folder. This gives you access to modern Windows APIs like notifications, windowing, on-device AI, and more. If you just need package identity for distribution, you can skip to step 7.

Let's add a simple example that prints the Windows App Runtime version.

### Update CMakeLists.txt

Add the following line to the end of your `CMakeLists.txt` to include the Windows App SDK headers:

```cmake
# Add Windows App SDK include directory
target_include_directories(cpp-app PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/.winapp/include)
```

### Update main.cpp

Replace the entire contents of `main.cpp` to use the Windows App Runtime API:

```cpp
#include <iostream>
#include <windows.h>
#include <appmodel.h>
#include <winrt/Microsoft.Windows.ApplicationModel.WindowsAppRuntime.h>

int main() {
    // Initialize WinRT
    winrt::init_apartment();
    
    UINT32 length = 0;
    LONG result = GetCurrentPackageFamilyName(&length, nullptr);
    
    if (result == ERROR_INSUFFICIENT_BUFFER) {
        // We have a package identity
        std::wstring familyName;
        familyName.resize(length);
        
        result = GetCurrentPackageFamilyName(&length, familyName.data());
        
        if (result == ERROR_SUCCESS) {
            std::wcout << L"Package Family Name: " << familyName.c_str() << std::endl;
            
            // Get Windows App Runtime version using the API
            auto runtimeVersion = winrt::Microsoft::Windows::ApplicationModel::WindowsAppRuntime::RuntimeInfo::AsString();
            std::wcout << L"Windows App Runtime Version: " << runtimeVersion.c_str() << std::endl;
        } else {
            std::wcout << L"Error retrieving Package Family Name" << std::endl;
        }
    } else {
        std::cout << "Not packaged" << std::endl;
    }
    
    return 0;
}
```

### Build and Run

Rebuild the application with the Windows App SDK headers:

```powershell
cmake --build build --config Debug
winapp run .\build\Debug
```

You should now see output like:
```
Package Family Name: cpp-app_12345abcde
Windows App Runtime Version: 1.8-stable (1.8.0)
```

The `.winapp/include` directory contains all the necessary headers for Windows App SDK, including:
- `winrt/` - WinRT C++ projection headers for accessing Windows Runtime APIs
- `Microsoft.UI.*.h` - WinUI 3 headers for modern UI components
- `MddBootstrap.h` - Windows App SDK bootstrapping
- `WindowsAppSDK-VersionInfo.h` - Version information
- And many more Windows App SDK components

For more advanced Windows App SDK usage, check out the [Windows App SDK documentation](https://learn.microsoft.com/windows/apps/windows-app-sdk/).

## 7. Restore headers when needed

The `.winapp` folder is automatically added to `.gitignore` by `winapp init`, so it won't be checked into source control. When others clone your project, they'll need to restore these files before building.

### Manual Setup

Run these two commands after cloning the repo:

```powershell
# Restore Windows App SDK headers
winapp restore

# Generate development certificate (optional - only if planning to package the app and sideload)
winapp cert generate --if-exists skip
```

Then you can build and run normally with `cmake -B build` and `cmake --build build --config Debug`.

### Automated Setup with CMake

Alternatively, you can automate this by adding setup logic to your `CMakeLists.txt`. Here is the full `CMakeLists.txt` with automation, proper linking, and minimal C++20 standard:

```cmake
cmake_minimum_required(VERSION 3.20)
project(cpp-app)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Download winapp CLI if not available in PATH
find_program(WINAPP_CLI winapp)
if(NOT WINAPP_CLI)
    set(WINAPP_DIR "${CMAKE_CURRENT_SOURCE_DIR}/.winapp-tools")
    set(WINAPP_CLI "${WINAPP_DIR}/winapp.exe")
    
    if(NOT EXISTS "${WINAPP_CLI}")
        message(STATUS "Downloading winapp CLI...")
        
        # Determine architecture
        if(CMAKE_SYSTEM_PROCESSOR MATCHES "ARM64|aarch64")
            set(WINAPP_ARCH "arm64")
        else()
            set(WINAPP_ARCH "x64")
        endif()
        
        # Download and extract
        set(WINAPP_ZIP "${CMAKE_CURRENT_BINARY_DIR}/winappcli.zip")
        file(DOWNLOAD 
            "https://github.com/microsoft/WinAppCli/releases/latest/download/winappcli-${WINAPP_ARCH}.zip"
            "${WINAPP_ZIP}"
            SHOW_PROGRESS
        )
        
        file(ARCHIVE_EXTRACT INPUT "${WINAPP_ZIP}" DESTINATION "${WINAPP_DIR}")
        file(REMOVE "${WINAPP_ZIP}")
        message(STATUS "winapp CLI downloaded to ${WINAPP_DIR}")
    endif()
endif()

# Automatically restore Windows App SDK headers and generate certificate if needed
# This runs once during CMake configuration, not on every build
if(NOT EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/.winapp/include")
    message(STATUS "Restoring Windows App SDK headers...")
    execute_process(
        COMMAND "${WINAPP_CLI}" restore
        WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
        RESULT_VARIABLE RESTORE_RESULT
    )
    if(NOT RESTORE_RESULT EQUAL 0)
        message(WARNING "Failed to restore Windows App SDK. Run 'winapp restore' manually.")
    endif()
endif()

if(NOT EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/devcert.pfx")
    message(STATUS "Generating development certificate...")
    execute_process(
        COMMAND "${WINAPP_CLI}" cert generate --if-exists skip
        WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
        RESULT_VARIABLE CERT_RESULT
    )
    if(NOT CERT_RESULT EQUAL 0)
        message(WARNING "Failed to generate certificate. Run 'winapp cert generate' manually.")
    endif()
endif()

add_executable(cpp-app main.cpp)

# Link Windows Runtime libraries
target_link_libraries(cpp-app PRIVATE WindowsApp.lib OneCoreUap.lib)

# Add Windows App SDK include directory
target_include_directories(cpp-app PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/.winapp/include)

```

With this setup:
- When someone clones the repo and runs `cmake -B build`, winapp is automatically downloaded if not found in PATH
- The Windows App SDK headers and certificate are automatically restored
- The commands only run once during configuration (not on every build) because they check if the files already exist
- If the commands fail, CMake shows a warning with instructions to run them manually
- The downloaded winapp is stored in `.winapp-tools/` (add this to `.gitignore` if needed)



## 8. Package with MSIX

Once you're ready to distribute your app, you can package it as an MSIX using the same manifest. MSIX provides clean install/uninstall, auto-updates, and a trusted installation experience.

### Prepare the Package Directory
First, build your application in release mode for optimal performance:

```powershell
cmake --build build --config Release
```

Then, create a directory with just the files needed for distribution and copy your release executable:

```powershell
mkdir dist
copy .\build\Release\cpp-app.exe .\dist\
```

### Generate a Development Certificate

MSIX packages must be signed. For local testing, generate a self-signed development certificate:

```powershell
winapp cert generate --if-exists skip
```

> [!TIP]
> The certificate's publisher must match the `Publisher` in your `Package.appxmanifest`. The `cert generate` command reads this automatically from your manifest.

### Sign and Pack

Now you can package and sign:

```powershell
# package and sign the app with the generated certificate
winapp pack .\dist --cert .\devcert.pfx 
```

> [!TIP]
> The `pack` command automatically uses the Package.appxmanifest from your current directory and copies it to the target folder before packaging. The generated `.msix` file will be in the current directory.

### Install the Certificate

Before you can install the MSIX package, you need to trust the development certificate on your machine. Run this command as administrator (you only need to do this once per certificate):

```powershell
winapp cert install .\devcert.pfx
```

### Install and Run

> [!TIP]
> If you used `winapp run` in step 5, the package may already be registered on your system. Use `winapp unregister` first to remove the development registration, then install the release package.

The `winapp pack` command generates the MSIX file in your project root directory. Install the package by double-clicking the generated `.msix` file, or using PowerShell:

```powershell
Add-AppxPackage .\cpp-app_1.0.0.0_x64.msix
```

> [!TIP]
> The MSIX filename includes the version and architecture (e.g., `cpp-app_1.0.0.0_arm64.msix`). Check your directory for the exact filename.

Now you can run your app from anywhere in the terminal by typing:

```powershell
cpp-app
```

You should see the "Package Family Name" output, confirming it's installed and running with identity.

> [!TIP]
> If you need to repackage your app (e.g., after code changes), increment the `Version` in your `Package.appxmanifest` before running `winapp pack` again. Windows requires a higher version number to update an installed package.

## Tips
1. Once you are ready for distribution, you can sign your MSIX with a code signing certificate from a Certificate Authority so your users don't have to install a self-signed certificate.
2. The [Azure Trusted Signing](https://azure.microsoft.com/products/trusted-signing) service is a great way to manage your certificates securely and integrate signing into your CI/CD pipeline.
3. The Microsoft Store will sign the MSIX for you, no need to sign before submission.
4. You might need to create multiple MSIX packages, one for each architecture you support (x64, Arm64). Configure CMake with the appropriate generator and architecture flags.

## Next Steps

- **Distribute via winget**: Submit your MSIX to the [Windows Package Manager Community Repository](https://github.com/microsoft/winget-pkgs)
- **Publish to the Microsoft Store**: Use `winapp store` to submit your package
- **Set up CI/CD**: Use the [`setup-WinAppCli`](https://github.com/microsoft/setup-WinAppCli) GitHub Action to automate packaging in your pipeline
- **Explore Windows APIs**: With package identity, you can now use [Notifications](https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart), [on-device AI](https://learn.microsoft.com/windows/ai/apis/), and other [identity-dependent APIs](https://learn.microsoft.com/windows/apps/desktop/modernize/desktop-to-uwp-extensions)
