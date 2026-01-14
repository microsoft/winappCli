# Using winapp CLI with C++ and CMake

This guide demonstrates how to use `winappcli` with a C++ application to debug with package identity and package your application as an MSIX.

Package identity is a core concept in the Windows app model. It allows your application to access specific Windows APIs (like Notifications, Security, AI APIs, etc), have a clean install/uninstall experience, and more.

A standard executable (like one created with `cmake --build`) does not have package identity. This guide shows how to add it for debugging and then package it for distribution.

## Prerequisites

1.  **Build Tools**: Use a compiler toolchain supported by CMake. This example uses Visual Studio. You can install the community edition with:
    ```powershell
    winget install Microsoft.VisualStudio.BuildTools
    ```

2.  **CMake**: Install CMake:
    ```powershell
    winget install Kitware.CMake
    ```

3.  **winapp CLI**: Install the `winapp` tool via winget:
    ```powershell
    winget install Microsoft.winappcli
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

We'll update the app to check if it's running with package identity. We'll use the Windows Runtime C++ API to access the Package APIs.

First, update your `CMakeLists.txt` to link against the Windows App Model library:

```cmake
cmake_minimum_required(VERSION 3.20)
project(cpp-app)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(cpp-app main.cpp)

# Link Windows Runtime libraries
target_link_libraries(cpp-app PRIVATE WindowsApp.lib OneCoreUap.lib)
```

Next, replace the contents of `main.cpp` with the following code. This code attempts to retrieve the current package identity using the Windows Runtime API. If it succeeds, it prints the Package Family Name; otherwise, it prints "Not packaged".

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

The `winapp init` command sets up everything you need in one go: app manifest, assets, development certificate, and optionally Windows App SDK headers for C++ development.

Run the following command and follow the prompts:

```powershell
winapp init
```

When prompted:
- **Package name**: Press Enter to accept the default (cpp-app)
- **Publisher name**: Press Enter to accept the default or enter your name
- **Version**: Press Enter to accept 1.0.0.0
- **Entry point**: Press Enter to accept the default (cpp-app.exe)
- **Setup SDKs**: Select "Stable SDKs" to download Windows App SDK and generate headers

This command will:
- Create `appxmanifest.xml` and `Assets` folder for your app identity
- Generate a development certificate (`devcert.pfx`) for signing
- Create a `.winapp` folder with Windows App SDK headers and libraries
- Create a `winapp.yaml` configuration file for pinning sdk versions

You can open `appxmanifest.xml` to further customize properties like the display name, publisher, and capabilities.

## 5. Debug with Identity

To test features that require identity (like Notifications) without fully packaging the app, you can use `winapp create-debug-identity`. This applies a temporary identity to your executable using the manifest we just generated.

1.  **Build the executable**:
    ```powershell
    cmake --build build --config Debug
    ```

2.  **Apply Debug Identity**:
    Run the following command on your built executable:
    ```powershell
    winapp create-debug-identity .\build\Debug\cpp-app.exe
    ```

3.  **Run the Executable**:
    Run the executable directly:
    ```powershell
    .\build\Debug\cpp-app.exe
    ```

You should now see output similar to:
```
Package Family Name: cpp-app_12345abcde
```
This confirms your app is running with a valid package identity!

### Automating Debug Identity (Optional)

To streamline your development workflow, you can configure CMake to automatically apply debug identity after building in Debug configuration. Add this to your `CMakeLists.txt`:

```cmake
# Add a post-build command to apply debug identity in Debug builds
add_custom_command(TARGET cpp-app POST_BUILD
    COMMAND $<$<CONFIG:Debug>:winapp>
            $<$<CONFIG:Debug>:create-debug-identity>
            $<$<CONFIG:Debug>:$<TARGET_FILE:cpp-app>>
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
    COMMAND_EXPAND_LISTS
    COMMENT "Applying debug identity to executable..."
)
```

With this configuration, simply running `cmake --build build --config Debug` will automatically apply the debug identity, and you can immediately run the executable with identity without the manual step.

## 6. Using Windows App SDK (Optional)

If you selected to setup the SDKs during `winapp init`, you now have access to Windows App SDK headers in the `.winapp/include` folder. This gives you access to modern Windows APIs like notifications, windowing, and more.

Let's add a simple example that prints the Windows App Runtime version.

### Update CMakeLists.txt

Add the Windows App SDK include directory and link the necessary libraries:

```cmake
cmake_minimum_required(VERSION 3.20)
project(cpp-app)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(cpp-app main.cpp)

# Link Windows Runtime libraries
target_link_libraries(cpp-app PRIVATE WindowsApp.lib OneCoreUap.lib)

# Add Windows App SDK include directory
target_include_directories(cpp-app PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/.winapp/include)

# Add post-build command to apply debug identity in Debug builds
add_custom_command(TARGET cpp-app POST_BUILD
    COMMAND $<$<CONFIG:Debug>:winapp>
            $<$<CONFIG:Debug>:create-debug-identity>
            $<$<CONFIG:Debug>:$<TARGET_FILE:cpp-app>>
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
    COMMAND_EXPAND_LISTS
    COMMENT "Applying debug identity to executable..."
)
```

### Update main.cpp

Replace the contents of `main.cpp` to use the Windows App Runtime API:

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
.\build\Debug\cpp-app.exe
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

## 7. Restore headers and certificate when needed

The `.winapp` folder and `devcert.pfx` are automatically added to `.gitignore` by `winapp init`, so they won't be checked into source control. When others clone your project, they'll need to restore these files before building.

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

# Add a post-build command to apply debug identity in Debug builds
add_custom_command(TARGET cpp-app POST_BUILD
    COMMAND $<$<CONFIG:Debug>:winapp>
            $<$<CONFIG:Debug>:create-debug-identity>
            $<$<CONFIG:Debug>:$<TARGET_FILE:cpp-app>>
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
    COMMAND_EXPAND_LISTS
    COMMENT "Applying debug identity to executable..."
)
```

With this setup:
- When someone clones the repo and runs `cmake -B build`, winapp is automatically downloaded if not found in PATH
- The Windows App SDK headers and certificate are automatically restored
- The commands only run once during configuration (not on every build) because they check if the files already exist
- If the commands fail, CMake shows a warning with instructions to run them manually
- The downloaded winapp is stored in `.winapp-tools/` (add this to `.gitignore` if needed)



## 8. Package with MSIX

Once you're ready to distribute your app, you can package it as an MSIX using the same manifest.

### Prepare the Package Directory
First, build your application in release mode for optimal performance:

```powershell
cmake --build build --config Release
```

Then, create a directory to hold your package files and copy your release executable:

```powershell
mkdir dist
copy .\build\Release\cpp-app.exe .\dist\
```

### Add Execution Alias
To allow users to run your app from the command line after installation (like `cpp-app`), add an execution alias to the `appxmanifest.xml`.

Open `appxmanifest.xml` and add the `uap5` namespace to the `<Package>` tag if it's missing, and then add the extension inside `<Applications><Application><Extensions>...`:

```xml
<Package
  ...
  xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
  xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
  IgnorableNamespaces="uap uap2 uap3 uap5 rescap desktop desktop6 uap10">

  ...
  <Applications>
    <Application ...>
      ...
      <!-- Add this Extensions section and the uap5 namespace above-->
      <Extensions>
        <uap5:Extension Category="windows.appExecutionAlias">
          <uap5:AppExecutionAlias>
            <uap5:ExecutionAlias Alias="cpp-app.exe" />
          </uap5:AppExecutionAlias>
        </uap5:Extension>
      </Extensions>
      ...
    </Application>
  </Applications>
</Package>
```

### Sign and Pack

Since `winapp init` already generated the development certificate, you can proceed directly to packaging:

```powershell
# package and sign the app with the generated certificate
winapp pack .\dist --cert .\devcert.pfx 
```

> Note: The appxmanifest.xml and assets need to be in the target folder for packaging. To simplify, the `pack` command by default uses the appxmanifest.xml in your current directory and copies it to the target folder before packaging.

### Install the Certificate

Before you can install the MSIX package, you need to install the development certificate. Run this command as administrator (you only need to do this once):

```powershell
# install certificate locally - run with sudo or as administrator
sudo winapp cert install .\devcert.pfx
```

### Install and Run
The `winapp pack` command generates the MSIX file in your project root directory. You can install the package using PowerShell:

```powershell
Add-AppxPackage .\cpp-app.msix
```

Now you can run your app from anywhere in the terminal by typing:

```powershell
cpp-app
```

You should see the "Package Family Name" output, confirming it's installed and running with identity.

### Tips:
1. Once you are ready for distribution, you can sign your MSIX with a code signing certificate from a Certificate Authority so your users don't have to install a self-signed certificate.
2. The [Azure Trusted Signing](https://azure.microsoft.com/products/trusted-signing) service is a great way to manage your certificates securely and integrate signing into your CI/CD pipeline.
3. The Microsoft Store will sign the MSIX for you, no need to sign before submission.
4. You might need to create multiple MSIX packages, one for each architecture you support (x64, Arm64). Configure CMake with the appropriate generator and architecture flags.
