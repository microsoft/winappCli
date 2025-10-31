cmake_minimum_required(VERSION 3.12)
project(portfile-test)

# Minimal stubs for vcpkg helper functions used by the portfile
function(vcpkg_find_acquire_program VAR)
    if("${VAR}" STREQUAL "WINAPP_CLI")
        find_program(_TMP winapp.exe winapp)
        if(_TMP)
            set(${VAR} ${_TMP} PARENT_SCOPE)
        else()
            set(${VAR} "" PARENT_SCOPE)
        endif()
    elseif("${VAR}" STREQUAL "NUGET")
        find_program(_TMP nuget.exe nuget)
        if(_TMP)
            set(${VAR} ${_TMP} PARENT_SCOPE)
        else()
            set(${VAR} "" PARENT_SCOPE)
        endif()
    else()
        # Default to empty
        set(${VAR} "" PARENT_SCOPE)
    endif()
endfunction()

function(vcpkg_execute_required_process)
    # No-op for testing; print the invoked command for inspection
    message(STATUS "vcpkg_execute_required_process invoked with: ${ARGN}")
endfunction()

# Expose variables the portfile expects
set(VCPKG_TARGET_ARCHITECTURE x64 CACHE STRING "")
set(CURRENT_BUILDTREES_DIR "${CMAKE_CURRENT_BINARY_DIR}")
set(CURRENT_PACKAGES_DIR "${CMAKE_CURRENT_BINARY_DIR}/packages")
set(BUILDTREES_DIR "${CMAKE_CURRENT_BINARY_DIR}")

# Ensure an actual dotnet publish is performed so the portfile test uses a real CLI binary
# (relative paths adjusted for test location inside the port folder)
set(_WINAPP_CLI_CSproj "${CMAKE_SOURCE_DIR}/../../../../src/winapp-CLI/WinApp.Cli/WinApp.Cli.csproj")
set(_WINAPP_CLI_RUNTIME "win-x64")
set(_WINAPP_CLI_CONFIG "Debug")
message(STATUS "Running: dotnet publish ${_WINAPP_CLI_CSproj} -c ${_WINAPP_CLI_CONFIG} -r ${_WINAPP_CLI_RUNTIME} --self-contained")
execute_process(
    COMMAND dotnet publish "${_WINAPP_CLI_CSproj}" -c ${_WINAPP_CLI_CONFIG} -r ${_WINAPP_CLI_RUNTIME} --self-contained
    WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}/../.."
    RESULT_VARIABLE _DOTNET_PUBLISH_EXIT
    OUTPUT_VARIABLE _DOTNET_PUBLISH_OUT
    ERROR_VARIABLE _DOTNET_PUBLISH_ERR
    OUTPUT_STRIP_TRAILING_WHITESPACE
)
if(NOT _DOTNET_PUBLISH_EXIT EQUAL 0)
    message(WARNING "dotnet publish failed with exit code ${_DOTNET_PUBLISH_EXIT}: ${_DOTNET_PUBLISH_ERR}")
else()
    message(STATUS "dotnet publish completed: ${_DOTNET_PUBLISH_OUT}")
endif()

# Include the portfile (path adjusted for being inside the port folder)
include("${CMAKE_SOURCE_DIR}/../../portfile.cmake")

message(STATUS "WINAPP_CLI=${WINAPP_CLI}")
message(STATUS "WINAPP_LAYOUT_DIR=${WINAPP_LAYOUT_DIR}")

# Persist detection results to a file for automated inspection
file(WRITE "${CMAKE_BINARY_DIR}/detection-results.txt" "WINAPP_CLI=${WINAPP_CLI}\nWINAPP_LAYOUT_DIR=${WINAPP_LAYOUT_DIR}\n")
