@echo off
rem ============================================================
rem  云梭下载器 launcher (debug build)
rem  Double-click this file to run the freshly built app.
rem ============================================================
setlocal
cd /d "%~dp0"

set "EXE=src\YunsuoDownloader.App\bin\Debug\net8.0-windows\YunsuoDownloader.exe"

if not exist "%EXE%" (
  echo.
  echo   [X] Not built yet: %EXE%
  echo.
  echo   Build it first with:
  echo       powershell -ExecutionPolicy Bypass -File scripts\build.ps1
  echo.
  pause
  exit /b 1
)

echo Starting 云梭下载器 ...
start "" "%EXE%"
exit /b 0
