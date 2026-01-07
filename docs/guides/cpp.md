# Using WinAppCLI with C++ and CMake

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

3.  **WinAppCLI**: Install the `winapp` tool via winget:
    ```powershell
    winget install Microsoft.WinAppCli
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

set(CMAKE_CXX_STANDARD 17)
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

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(cpp-app main.cpp)

# Link Windows Runtime libraries
target_link_libraries(cpp-app PRIVATE WindowsApp.lib)
```

Next, replace the contents of `main.cpp` with the following code. This code attempts to retrieve the current package identity using the Windows Runtime API. If it succeeds, it prints the Package Family Name; otherwise, it prints "Not packaged".

```cpp
#include <iostream>
#include <windows.h>
#include <appmodel.h>

#pragma comment(lib, "kernel32.lib")

int main() {
    UINT32 length = 0;
    LONG result = GetCurrentPackageFamilyName(&length, nullptr);
    
    if (result == ERROR_INSUFFICIENT_BUFFER) {
        // We have a package identity, get the family name
        wchar_t* familyName = new wchar_t[length];
        result = GetCurrentPackageFamilyName(&length, familyName);
        
        if (result == ERROR_SUCCESS) {
            std::wcout << L"Package Family Name: " << familyName << std::endl;
            delete[] familyName;
            return 0;
        }
        
        delete[] familyName;
    }
    
    // No package identity or error
    std::cout << "Not packaged" << std::endl;
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

## 4. Generate App Manifest

To give your application an identity, you need an `appxmanifest.xml`. This file describes your application to Windows. We will generate a default one now, and use it for both debugging and final packaging.

```powershell
winapp manifest generate
```

This creates an `appxmanifest.xml` file and an `Assets` folder in your current directory. You can open `appxmanifest.xml` to customize properties like the display name, publisher, and logo.

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

## 6. Package with MSIX

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

### Sign and Pack
If you haven't already, generate and install a self-signed certificate for local testing:

```powershell
# will generate devcert.pfx with publisher details matching the appxmanifest.xml
winapp cert generate --manifest .\appxmanifest.xml

# install certificate locally - run with sudo or as administrator
sudo winapp cert install .\devcert.pfx
```

Now, pack the application:

```powershell
# package and sign the app with the generated certificate
winapp pack .\dist --cert .\devcert.pfx 
```

> Note: The appxmanifest.xml and assets need to be in the target folder for packaging. To simplify, the `pack` command by default uses the appxmanifest.xml in your current directory and copies it to the target folder before packaging.

### Install and Run
Install the package by double-clicking the generated *.msix file in the `dist` folder.

Now you can run your app from anywhere in the terminal by typing:

```powershell
cpp-app
```

You should see the "Package Family Name" output, confirming it's installed and running with identity.

### Tips:
1. Once you are ready for distribution, you can sign your MSIX with a code signing certificate from a Certificate Authority so your users don't have to install a self-signed certificate.
2. The Microsoft Store will sign the MSIX for you, no need to sign before submission.
3. You might need to create multiple MSIX packages, one for each architecture you support (x64, Arm64). Configure CMake with the appropriate generator and architecture flags.