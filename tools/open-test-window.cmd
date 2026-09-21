@echo off
setlocal

rem S10: starts the live test window from a fresh console. Builds Release first if it has not
rem been built yet; never touches Earshot.exe itself, never elevates, never sets EARSHOT_SAFE_MODE
rem or EARSHOT_DATA_ROOT (a real run needs neither: the window's own StartupGate refuses if either
rem is set without --sandbox).

set "REPO_ROOT=%~dp0.."
set "PROJECT=%REPO_ROOT%\src\Earshot.TestWindow\Earshot.TestWindow.csproj"
set "EXE=%REPO_ROOT%\src\Earshot.TestWindow\bin\Release\net10.0-windows10.0.19041.0\Earshot.TestWindow.exe"

if not exist "%EXE%" (
    echo Earshot.TestWindow has not been built yet. Building Release...
    dotnet build "%PROJECT%" -c Release
    if errorlevel 1 (
        echo Build failed. The test window was not started.
        exit /b 1
    )
)

if not exist "%EXE%" (
    echo Build finished but the executable was still not found at:
    echo   %EXE%
    exit /b 1
)

start "" "%EXE%"
