@echo off
rem ===========================================================================
rem  BootWakeMailer - installation entry point (requirements.md FR-09).
rem
rem  Double-click this file. It checks for administrator privileges, requests
rem  elevation through UAC when needed and then runs install.ps1 next to it.
rem
rem  This file must use CRLF line endings; cmd.exe mis-parses "goto :label" in a
rem  file that uses bare LF endings.
rem
rem  Exit codes: 0 success, 1 failure, 2 elevation refused or failed.
rem ===========================================================================
setlocal EnableExtensions

set "BWM_DIR=%~dp0"
set "BWM_PS1=%BWM_DIR%install.ps1"
set "BWM_SELF=%~f0"
set "BWM_ARGS=%*"
set "BWM_EXIT_CODE=0"

if not exist "%BWM_PS1%" (
    echo [ERROR] install.ps1 was not found next to this file:
    echo         %BWM_PS1%
    echo.
    echo Extract the complete release package before running install.cmd.
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

echo Administrator privileges are required for the installation.
echo Requesting elevation...
echo.

rem Re-launch this script elevated and forward its exit code. The script path and
rem the arguments travel through environment variables so that spaces and quotes
rem survive unchanged; passing %* inline would re-quote it incorrectly and
rem dropping it would silently turn "install.cmd -WhatIf" into a real
rem installation. BWM_ELEVATED_CHILD is inherited by the elevated child, which
rem uses it to keep its own console window open.
set "BWM_ELEVATED_CHILD=1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; try { $a = @(); if ($env:BWM_ARGS) { $a = @($env:BWM_ARGS) }; $p = Start-Process -FilePath $env:BWM_SELF -ArgumentList $a -Verb RunAs -Wait -PassThru; exit $p.ExitCode } catch { Write-Host ('[ERROR] Elevation failed or was cancelled: ' + $_.Exception.Message); exit 2 }"
set "BWM_EXIT_CODE=%ERRORLEVEL%"
goto :finish

:run
rem Arguments are forwarded so the script stays usable from a console, for
rem example: install.cmd -WhatIf
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%BWM_PS1%" %*
set "BWM_EXIT_CODE=%ERRORLEVEL%"

:finish
if not "%BWM_EXIT_CODE%"=="0" (
    echo.
    echo Installation did not complete successfully. Exit code: %BWM_EXIT_CODE%
    echo.
    echo Possible causes:
    echo   - the UAC prompt was cancelled
    echo   - the release package is incomplete
    echo   - a previous installation is still holding its files open
)

rem An elevated instance runs in its own console window that would otherwise
rem close immediately, hiding the result of the installation.
if defined BWM_ELEVATED_CHILD (
    echo.
    pause
)

endlocal & exit /b %BWM_EXIT_CODE%
