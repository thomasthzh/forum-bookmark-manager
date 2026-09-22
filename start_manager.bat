@echo off
setlocal
cd /d "%~dp0"

if exist "dist\win-x64\Rfbm.Native.exe" (
  start "R-fbm" "dist\win-x64\Rfbm.Native.exe"
  exit /b 0
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo R-fbm has not been published and the .NET 8 SDK was not found.
  echo Run scripts\publish.ps1 on a machine with the .NET 8 SDK first.
  pause
  exit /b 1
)

start "R-fbm" dotnet run --project "src\Rfbm.Native\Rfbm.Native.csproj"
exit /b 0
