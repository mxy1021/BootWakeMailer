@echo off
rem ===========================================================================
rem  BootWakeMailer - uninstallation entry point (requirements.md FR-10).
rem
rem  Double-click this file. It checks for administrator privileges, requests
rem  elevation through UAC when needed and then runs uninstall.ps1 next to it.
rem
rem  The service, the installed files and the application data (config.json,
rem  queue.json, status.json) are removed. Application data is not preserved.
rem
rem  This file must use CRLF line endings; cmd.exe mis-parses "goto :label" in a
rem  file that uses bare LF endings.
rem
rem  Exit codes: 0 success, 1 failure, 2 elevation refused or failed.
rem ===========================================================================
setlocal EnableExtensions

set "BWM_DIR=%~dp0"
set "BWM_PS1=%BWM_DIR%uninstall.ps1"
set "BWM_SELF=%~f0"
set "BWM_ARGS=%*"
set "BWM_EXIT_CODE=0"

if not exist "%BWM_PS1%" (
    echo [ERROR] uninstall.ps1 was not found next to this file:
    echo         %BWM_PS1%
    echo.
    echo Run uninstall.cmd from the extracted release package.
    set "BWM_EXIT_CODE=1"
    goto :finish
)

rem --- Administrator check ---------------------------------------------------
rem BWM_ELEVATED_CHILD means this process was already re-launched through UAC,
rem so control flow never loops back into the elevation below.
if defined BWM_ELEVATED_CHILD goto :run

rem "net session" fails for a process without administrator rights.
net session >nul 2>&1
if not errorlevel 1 goto :run

echo Administrator privileges are required for the uninstallation.
echo Requesting elevation...
echo.

rem Re-launch this script elevated and forward its exit code. The script path and
rem the arguments travel through environment variables so that spaces and quotes
rem survive unchanged; passing %* inline would re-quote it incorrectly and
rem dropping it would silently turn "uninstall.cmd -WhatIf" into a real
rem uninstallation that deletes the application data. BWM_ELEVATED_CHILD is
rem inherited by the elevated child, which uses it to keep its own console window
rem open.
set "BWM_ELEVATED_CHILD=1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; try { $a = @(); if ($env:BWM_ARGS) { $a = @($env:BWM_ARGS) }; $p = Start-Process -FilePath $env:BWM_SELF -ArgumentList $a -Verb RunAs -Wait -PassThru; exit $p.ExitCode } catch { Write-Host ('[ERROR] Elevation failed or was cancelled: ' + $_.Exception.Message); exit 2 }"
set "BWM_EXIT_CODE=%ERRORLEVEL%"
goto :finish

:run
rem Arguments are forwarded so the script stays usable from a console, for
rem example: uninstall.cmd -WhatIf
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%BWM_PS1%" %*
set "BWM_EXIT_CODE=%ERRORLEVEL%"

:finish
if not "%BWM_EXIT_CODE%"=="0" (
    echo.
    echo Uninstallation did not complete successfully. Exit code: %BWM_EXIT_CODE%
    echo.
    echo Possible causes:
    echo   - the UAC prompt was cancelled
    echo   - the service could not be stopped
    echo   - a file in the installation directory is still in use
)

rem An elevated instance runs in its own console window that would otherwise
rem close immediately, hiding the result of the uninstallation.
if defined BWM_ELEVATED_CHILD (
    echo.
    pause
)

endlocal & exit /b %BWM_EXIT_CODE%
