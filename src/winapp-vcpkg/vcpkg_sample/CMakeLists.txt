# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
cmake_minimum_required(VERSION 3.21)

project(cmake-ai-generator)

# Option for self-contained deployment
option(WINAPP_SELF_CONTAINED "Enable Windows App SDK self-contained deployment" OFF)

find_package(wil CONFIG REQUIRED)
find_package(winapp CONFIG REQUIRED)

set(CMAKE_CXX_STANDARD 23)

add_executable(cmake-ai-generator
    main.cpp
    app.manifest
    formatters.h)

target_link_libraries(cmake-ai-generator
    INTERFACE
        WindowsAppSdk::Bootstrap
    PRIVATE
        Microsoft::WindowsAppSdk
        OneCoreUAP
)

# Handle self-contained deployment
if(WINAPP_SELF_CONTAINED)
    message(STATUS "Configuring for Windows App SDK self-contained deployment")
    
    # Set compiler definition for self-contained mode
    target_compile_definitions(cmake-ai-generator PRIVATE MICROSOFT_WINDOWSAPPSDK_SELFCONTAINED=1)
    
    message(STATUS "Self-contained runtime files will be copied during build")
endif()

function(install_target_runtime_dlls)
  set(single_value_args DESTINATION)
  set(multi_value_args TARGETS)
  cmake_parse_arguments(THIS "" "${single_value_args}" "${multi_value_args}" ${ARGN})

  foreach(TARGET ${THIS_TARGETS})
    get_target_property(TARGET_RUNTIME_DLLS ${TARGET} RUNTIME_DLLS)
    if ("${TARGET_RUNTIME_DLLS}" STREQUAL "")
      message(FATAL_ERROR "Provided target ${TARGET} has no RUNTIME_DLLS")
    endif()

    foreach(ONEFILE ${TARGET_RUNTIME_DLLS})
        file(COPY ${ONEFILE}
          DESTINATION ${THIS_DESTINATION}
        )
        endforeach()
    endforeach()
endfunction()

# Copy AppX package files to build directory
winapp_copy_appx_files()

# Handle runtime files based on deployment mode
if(WINAPP_SELF_CONTAINED)
    # Copy self-contained runtime files to build directory
    winapp_copy_self_contained_files(
        TARGET_NAME cmake-ai-generator
        DESTINATION ${CMAKE_BINARY_DIR}
    )
else()
    # Standard non-self-contained build: install runtime DLLs
    install_target_runtime_dlls(TARGETS WindowsAppSdk::Bootstrap
        DESTINATION ${CMAKE_BINARY_DIR}
    )
endif()

# Register AppX package for debugging
winapp_register_appx_package(cmake-ai-generator)
