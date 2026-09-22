@echo off
title Pulse
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 goto NODOTNET

echo [Pulse] Starting... Hotkey Ctrl+Alt+P toggles the rail
dotnet run --project src\Pulse.App -c Release
if errorlevel 1 goto FAILED
exit /b 0

:NODOTNET
echo [Pulse] dotnet not found.
echo Install .NET 10 SDK first:
echo   winget install Microsoft.DotNet.SDK.10
pause
exit /b 1

:FAILED
echo.
echo [Pulse] Start failed. Try a full build:
echo   dotnet build Pulse.Win.slnx -c Release
pause
exit /b 1
