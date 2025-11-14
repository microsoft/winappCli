set(VCPKG_BUILD_TYPE release)

# Pick the correct published runtime artifact based on vcpkg target architecture
if(VCPKG_TARGET_ARCHITECTURE STREQUAL "arm64")
    set(WINAPP_RUNTIME_ARCH "win-arm64")
else()
    set(WINAPP_RUNTIME_ARCH "win-x64")
endif()

# Attempt to locate the repository root so local-built CLI paths work from arbitrary working directories.
# We look for the CLI project file which lives at: src/winapp-CLI/WinApp.Cli/WinApp.Cli.csproj
set(_search_dir "${CMAKE_CURRENT_LIST_DIR}")
set(_repo_root "")
while(TRUE)
  if(EXISTS "${_search_dir}/src/winapp-CLI/WinApp.Cli/WinApp.Cli.csproj")
    set(_repo_root "${_search_dir}")
    break()
  endif()
  get_filename_component(_parent_dir "${_search_dir}" PATH)
  if("${_parent_dir}" STREQUAL "${_search_dir}")
    break()
  endif()
  set(_search_dir "${_parent_dir}")
endwhile()

if(NOT "${_repo_root}" STREQUAL "")
    # Use the dotnet publish output (publish folder) as the canonical local CLI location
    set(LOCAL_CLI_RELEASE "${_repo_root}/src/winapp-CLI/WinApp.Cli/bin/Release/net10.0-windows/${WINAPP_RUNTIME_ARCH}/publish/winapp.exe")
    set(LOCAL_CLI_DEBUG   "${_repo_root}/src/winapp-CLI/WinApp.Cli/bin/Debug/net10.0-windows/${WINAPP_RUNTIME_ARCH}/publish/winapp.exe")
endif()

if(EXISTS "${LOCAL_CLI_RELEASE}")
    set(WINAPP_CLI "${LOCAL_CLI_RELEASE}")
elseif(EXISTS "${LOCAL_CLI_DEBUG}")
    set(WINAPP_CLI "${LOCAL_CLI_DEBUG}")
endif()

# If not available locally, look on PATH for a winapp executable
if(NOT WINAPP_CLI)
    # Prefer a plain PATH lookup so unit tests and minimal CMake environments don't have to provide vcpkg helpers
    find_program(WINAPP_CLI NAMES winapp winapp.exe)

    # If not found on PATH and vcpkg's helper exists, ask vcpkg to locate or acquire the program
    if(NOT WINAPP_CLI AND COMMAND vcpkg_find_acquire_program)
        vcpkg_find_acquire_program(WINAPP_CLI NAMES winapp winapp.exe)
    endif()
endif()

# Attempt to download the latest GitHub release asset matching the runtime arch
if(NOT WINAPP_CLI)
    message(STATUS "winapp CLI not found locally; attempting to download a prebuilt release asset from GitHub (best-effort)...")
    vcpkg_execute_required_process(
        ALLOW_IN_DOWNLOAD_MODE
        COMMAND powershell -NoProfile -Command "try {
            $r = Invoke-RestMethod -Uri 'https://api.github.com/repos/microsoft/WinAppCli/releases/latest' -Headers @{ 'User-Agent' = 'winapp-vcpkg' }
            $asset = $r.assets |
            Where-Object {
                $_.name -match '${WINAPP_RUNTIME_ARCH}' -and
                ($_.name -match 'winapp|winapp-cli|cli-binaries|WinApp.Cli') -and
                ($_.name -match '\.zip$' -or $_.name -match '\.exe$')
            } |
            Select-Object -First 1

            if(-not $asset) {
            $asset = $r.assets |
                Where-Object {
                $_.name -match '${WINAPP_RUNTIME_ARCH}' -and
                ($_.name -match '\.zip$' -or $_.name -match '\.exe$')
                } |
                Select-Object -First 1
            }

            if(-not $asset) {
            Write-Error 'No matching winapp CLI asset found in latest release'
            exit 2
            }

            $out = '${CURRENT_BUILDTREES_DIR}\\winapp-cli-download'
            New-Item -ItemType Directory -Force -Path $out | Out-Null

            $dl = Join-Path $out $asset.name
            Invoke-WebRequest -UseBasicParsing -Uri $asset.browser_download_url -OutFile $dl

            if($dl -like '*zip') {
            Expand-Archive -LiteralPath $dl -DestinationPath $out -Force
            $exe = Get-ChildItem -Path $out -Filter 'winapp.exe' -Recurse | Select-Object -First 1
            if(-not $exe) {
                Write-Error 'Downloaded archive did not contain winapp.exe'
                exit 3
            }
            Write-Output $exe.FullName
            } else {
            Write-Output $dl
            }
        } catch {
            Write-Error $_.Exception.Message
            exit 4
        }"
        WORKING_DIRECTORY "${CURRENT_BUILDTREES_DIR}"
        LOGNAME winapp-download-${TARGET_TRIPLET})

    file(GLOB downloaded_cli "${CURRENT_BUILDTREES_DIR}/winapp-cli-download/**/winapp.exe")
    if(downloaded_cli)
        list(GET downloaded_cli 0 WINAPP_CLI)
    endif()
endif()

# If we have a winapp CLI executable, use it to setup the workspace. Otherwise, fall back to NuGet.
if(WINAPP_CLI)
    message(STATUS "Using winapp CLI at ${WINAPP_CLI} to setup SDKs")
    vcpkg_execute_required_process(
        ALLOW_IN_DOWNLOAD_MODE
        COMMAND "${WINAPP_CLI}" setup "${CURRENT_BUILDTREES_DIR}" --yes
        WORKING_DIRECTORY "${CURRENT_BUILDTREES_DIR}"
        LOGNAME winapp-setup-${TARGET_TRIPLET})

    # winapp CLI creates a workspace under <base>/.winapp
    set(WINAPP_LAYOUT_DIR "${CURRENT_BUILDTREES_DIR}/.winapp")
else()
    message(FATAL_ERROR "winapp CLI not found locally, on PATH, or downloadable from GitHub releases. This port requires the winapp CLI — aborting.")
endif()

# Collect all component include files
file(INSTALL "${WINAPP_LAYOUT_DIR}/include/"
    DESTINATION "${CURRENT_PACKAGES_DIR}/include")

# Collect all component libraries
file(GLOB_RECURSE
    winappcli_import_libs
    LIST_DIRECTORIES false
    "${WINAPP_LAYOUT_DIR}/lib/**/${VCPKG_TARGET_ARCHITECTURE}/*.lib"
    "${WINAPP_LAYOUT_DIR}/lib/**/win-${VCPKG_TARGET_ARCHITECTURE}/*.lib"
    "${WINAPP_LAYOUT_DIR}/lib/**/win10-${VCPKG_TARGET_ARCHITECTURE}/*.lib"
    "${WINAPP_LAYOUT_DIR}/packages/**/lib/**/win-${VCPKG_TARGET_ARCHITECTURE}/*.lib"
    "${WINAPP_LAYOUT_DIR}/packages/**/lib/**/*.lib")
file(INSTALL ${winappcli_import_libs}
    DESTINATION "${CURRENT_PACKAGES_DIR}/lib")

# Collect all component runtime files
file(GLOB
    winappcli_runtime_files
    LIST_DIRECTORIES false
    "${WINAPP_LAYOUT_DIR}/bin/**/*.*"
    "${WINAPP_LAYOUT_DIR}/packages/**/runtimes/win-${VCPKG_TARGET_ARCHITECTURE}/native/*.*")
file(INSTALL ${winappcli_runtime_files}
    DESTINATION "${CURRENT_PACKAGES_DIR}/bin")

file(INSTALL "${CMAKE_CURRENT_LIST_DIR}/winapp-config.cmake"
    DESTINATION "${CURRENT_PACKAGES_DIR}/share/${PORT}")

file(INSTALL "${CMAKE_CURRENT_LIST_DIR}/usage" 
    DESTINATION "${CURRENT_PACKAGES_DIR}/share/${PORT}")

#--- Copy license
configure_file("${WINAPP_LAYOUT_DIR}/share/Microsoft.WindowsAppSDK/copyright" "${CURRENT_PACKAGES_DIR}/share/${PORT}/copyright" COPYONLY)

include_guard(GLOBAL)
