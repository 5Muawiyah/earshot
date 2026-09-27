@echo off
setlocal

rem Starts the phase 0 capture in this console: a read-only Bluetooth listener that walks the owner
rem through the case and bud steps and writes a capture log under %LOCALAPPDATA%\Earshot\phase0.
rem Builds Release every time so the capture never runs an old build. Never touches Earshot.exe,
rem never elevates, sends nothing to any device.

set "REPO_ROOT=%~dp0.."
set "PROJECT=%REPO_ROOT%\src\Earshot.AdvertProbe\Earshot.AdvertProbe.csproj"
set "EXE=%REPO_ROOT%\src\Earshot.AdvertProbe\bin\x64\Release\net10.0-windows10.0.19041.0\Earshot.AdvertProbe.exe"

echo Building the capture tool...
dotnet build "%PROJECT%" -c Release -p:Platform=x64 -v quiet -nologo
if errorlevel 1 (
    echo Build failed. The capture was not started.
    exit /b 1
)

if not exist "%EXE%" (
    echo Build finished but the executable was not found at:
    echo   %EXE%
    exit /b 1
)

"%EXE%" %*
