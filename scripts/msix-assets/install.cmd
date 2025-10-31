@echo off
REM Batch file wrapper to run the PowerShell installer script
REM This bypasses PowerShell execution policy restrictions

echo.
echo ================================================
echo  WINDOWS APP DEVELOPMENT CLI - MSIX Installation
echo ================================================
echo.

REM Run PowerShell with bypass execution policy
powershell.exe -ExecutionPolicy Bypass -File "%~dp0install.ps1"

REM Check if the script succeeded
if %ERRORLEVEL% EQU 0 (
    echo.
    echo Installation completed successfully!
) else (
    echo.
    echo Installation encountered an error.
    echo Please check the output above for details.
)

echo.
pause
