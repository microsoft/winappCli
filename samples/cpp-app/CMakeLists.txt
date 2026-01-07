cmake_minimum_required(VERSION 3.20)
project(cpp-app)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Download winapp CLI if not available in PATH
find_program(WINAPP_CLI winapp)
if(NOT WINAPP_CLI)
    set(WINAPP_DIR "${CMAKE_CURRENT_SOURCE_DIR}/.winapp-tools")
    set(WINAPP_CLI "${WINAPP_DIR}/winapp.exe")
    
    if(NOT EXISTS "${WINAPP_CLI}")
        message(STATUS "Downloading WinAppCLI...")
        
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
        message(STATUS "WinAppCLI downloaded to ${WINAPP_DIR}")
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
target_link_libraries(cpp-app PRIVATE WindowsApp.lib)

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