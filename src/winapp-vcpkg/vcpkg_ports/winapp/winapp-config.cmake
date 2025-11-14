get_filename_component(_packages_dir "${CMAKE_CURRENT_LIST_DIR}" PATH)
get_filename_component(_packages_dir  "${_packages_dir}" PATH)

if(NOT TARGET WindowsAppSdk::DWriteCore)
   add_library(WindowsAppSdk::DWriteCore STATIC IMPORTED)
   set_property(
      TARGET WindowsAppSdk::DWriteCore 
      PROPERTY IMPORTED_LOCATION "${_packages_dir}/lib/DWriteCore.lib"
    )
endif()

if(NOT TARGET WindowsAppSdk::Bootstrap)
   add_library(WindowsAppSdk::Bootstrap SHARED IMPORTED)
   set_target_properties(WindowsAppSdk::Bootstrap 
      PROPERTIES
        INTERFACE_INCLUDE_DIRECTORIES "${_packages_dir}/include"
        IMPORTED_IMPLIB "${_packages_dir}/lib/Microsoft.WindowsAppRuntime.Bootstrap.lib"
        IMPORTED_LOCATION "${_packages_dir}/bin/Microsoft.WindowsAppRuntime.Bootstrap.dll"
        RUNTIME_DLLS "${_packages_dir}/bin/Microsoft.WindowsAppRuntime.Bootstrap.dll"
    )
endif()

if(NOT TARGET WindowsAppSdk::Runtime)
   add_library(WindowsAppSdk::Runtime STATIC IMPORTED)
   set_property(
      TARGET WindowsAppSdk::Runtime 
      PROPERTY IMPORTED_LOCATION "${_packages_dir}/lib/Microsoft.WindowsAppRuntime.lib"
    )
endif()

if(NOT TARGET Microsoft::WindowsAppSdk)
   add_library(Microsoft::WindowsAppSdk INTERFACE IMPORTED)
 #  add_custom_target( DEPENDS "${wasdk_stamp}")
   target_link_libraries(Microsoft::WindowsAppSdk INTERFACE
      WindowsAppSdk::DWriteCore
      WindowsAppSdk::Bootstrap
      WindowsAppSdk::Runtime
   )
endif()

# Function to copy AppX package files to build directory
function(winapp_copy_appx_files)
    set(options)
    set(oneValueArgs MANIFEST_FILE ASSETS_DIR DESTINATION)
    set(multiValueArgs)
    cmake_parse_arguments(PARSE_ARGV 0 ARG "${options}" "${oneValueArgs}" "${multiValueArgs}")
    
    # Set default values
    if(NOT ARG_MANIFEST_FILE)
        set(ARG_MANIFEST_FILE "AppxManifest.xml")
    endif()
    
    if(NOT ARG_ASSETS_DIR)
        set(ARG_ASSETS_DIR "Images")
    endif()
    
    if(NOT ARG_DESTINATION)
        set(ARG_DESTINATION "${CMAKE_BINARY_DIR}")
    endif()
    
    # Copy the AppxManifest content to the binary directory
    file(
        COPY 
            "${ARG_ASSETS_DIR}"
            "${ARG_MANIFEST_FILE}"
        DESTINATION 
            "${ARG_DESTINATION}"
    )
endfunction()

# Function to register AppX package for debugging
function(winapp_register_appx_package TARGET_NAME)
    set(options)
    set(oneValueArgs MANIFEST_FILE PACKAGE_LOCATION)
    set(multiValueArgs)
    cmake_parse_arguments(PARSE_ARGV 1 ARG "${options}" "${oneValueArgs}" "${multiValueArgs}")
    
    # Set default values
    if(NOT ARG_MANIFEST_FILE)
        set(ARG_MANIFEST_FILE "AppxManifest.xml")
    endif()
    
    if(NOT ARG_PACKAGE_LOCATION)
        set(ARG_PACKAGE_LOCATION "${CMAKE_BINARY_DIR}")
    endif()
    
    # Register the app package after build for in-place launch & debugging
    add_custom_command(TARGET ${TARGET_NAME} POST_BUILD
        COMMAND powershell -ExecutionPolicy Bypass -Command "Add-AppxPackage -Path \"${ARG_PACKAGE_LOCATION}/${ARG_MANIFEST_FILE}\" -ExternalLocation \"${ARG_PACKAGE_LOCATION}\" -Register -ForceUpdateFromAnyVersion"
        COMMENT "Registering the app package for ${TARGET_NAME}"
    )
endfunction()

# Function to copy self-contained Windows App SDK runtime files to build directory
function(winapp_copy_self_contained_files)
    set(options)
    set(oneValueArgs TARGET_NAME ARCHITECTURE DESTINATION SOURCE_DIR)
    set(multiValueArgs)
    cmake_parse_arguments(PARSE_ARGV 0 ARG "${options}" "${oneValueArgs}" "${multiValueArgs}")
    
    # Set default values - match CMake architecture
    if(NOT ARG_ARCHITECTURE)
        # Use CMAKE_GENERATOR_PLATFORM if available, otherwise detect from pointer size
        if(CMAKE_GENERATOR_PLATFORM STREQUAL "x64" OR CMAKE_GENERATOR_PLATFORM STREQUAL "Win64")
            set(ARG_ARCHITECTURE "x64")
        elseif(CMAKE_GENERATOR_PLATFORM STREQUAL "ARM64")
            set(ARG_ARCHITECTURE "arm64")
        elseif(CMAKE_SIZEOF_VOID_P EQUAL 8)
            set(ARG_ARCHITECTURE "x64")
        else()
            set(ARG_ARCHITECTURE "x86")
        endif()
    endif()
    
    if(NOT ARG_DESTINATION)
        set(ARG_DESTINATION "${CMAKE_BINARY_DIR}")
    endif()
    
    if(NOT ARG_SOURCE_DIR)
        set(ARG_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}")
    endif()
    
    # Find winapp CLI
    find_program(WINAPP_CLI_EXE 
        NAMES winapp.exe winapp.exe
        PATHS 
            "${CMAKE_CURRENT_SOURCE_DIR}/../../../winapp-npm/bin/win-x64"
            "${CMAKE_CURRENT_SOURCE_DIR}/../../winapp-CLI/WinApp.Cli/bin/Debug/net10.0-windows/win-x64"
            "${CMAKE_CURRENT_SOURCE_DIR}/../../winapp-CLI/WinApp.Cli/bin/Release/net10.0-windows/win-x64"
    )
    
    if(NOT WINAPP_CLI_EXE)
        message(WARNING "winapp CLI not found - cannot prepare self-contained files")
        message(STATUS "Please build the CLI first or install the npm package")
        return()
    endif()
    
    if(ARG_TARGET_NAME)
        # Set up post-build command to create self-contained MSIX package
        add_custom_command(TARGET ${ARG_TARGET_NAME} POST_BUILD
            COMMAND "${WINAPP_CLI_EXE}" package 
                "${ARG_DESTINATION}" 
                "${ARG_DESTINATION}"
                --self-contained
                --name "${ARG_TARGET_NAME}"
            WORKING_DIRECTORY "${ARG_SOURCE_DIR}"
            COMMENT "Creating self-contained MSIX package for ${ARG_TARGET_NAME} (${ARG_ARCHITECTURE})"
            VERBATIM
        )
        message(STATUS "Configured self-contained MSIX packaging for ${ARG_TARGET_NAME} (${ARG_ARCHITECTURE})")
    else()
        message(STATUS "TARGET_NAME required for self-contained deployment")
    endif()
endfunction()

unset(_packages_dir)
