@echo off
REM Simple batch wrapper for the PowerShell build script
REM This allows calling the build from Command Prompt

echo Starting Windows SDK build process...
powershell.exe -ExecutionPolicy Bypass -File "%~dp0build-cli.ps1" %*

if %ERRORLEVEL% neq 0 (
    echo Build failed with exit code %ERRORLEVEL%
    exit /b %ERRORLEVEL%
)

echo Build completed successfully!