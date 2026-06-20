@echo off
REM FetchCoreClrSDK.bat - download the Android arm64 CoreCLR runtime + BCL from NuGet
REM and populate Source\ThirdParty\CoreClrSDK\Android\{lib,runtime}.
REM
REM The CoreClrSDK binaries (.so / .dll) are git-ignored (large, version-bound build
REM artifacts). Run this script once after cloning (or when upgrading the .NET version)
REM to populate the SDK. Mirrors how MonoSDK is populated from GitHub Releases.
REM
REM Usage:
REM   FetchCoreClrSDK.bat              (default version, see RUNTIME_VERSION below)
REM   FetchCoreClrSDK.bat 10.0.9       (explicit version)
REM
REM Requirements: PowerShell (Invoke-WebRequest / Expand-Archive) - bundled on Win10+.

setlocal enabledelayedexpansion

set "RUNTIME_VERSION=%~1"
if "%RUNTIME_VERSION%"=="" set "RUNTIME_VERSION=10.0.9"

set "NUGET_URL=https://globalcdn.nuget.org/packages/microsoft.netcore.app.runtime.android-arm64.%RUNTIME_VERSION%.nupkg"

REM SDK root = directory of this script.
set "SDK_ROOT=%~dp0"
if "%SDK_ROOT:~-1%"=="\" set "SDK_ROOT=%SDK_ROOT:~0,-1%"
set "LIB_DIR=%SDK_ROOT%\Android\lib"
set "RUNTIME_DIR=%SDK_ROOT%\Android\runtime"
set "WORK_DIR=%SDK_ROOT%\..\.FetchCoreClrSDK_tmp"

echo === FetchCoreClrSDK: .NET %RUNTIME_VERSION% (android-arm64) ===
echo SDK root: %SDK_ROOT%

REM Clean any prior temp dir.
if exist "%WORK_DIR%" rmdir /S /Q "%WORK_DIR%"
mkdir "%WORK_DIR%" 2>nul

REM Download + extract the nupkg (it's a zip) via PowerShell.
REM Expand-Archive only accepts .zip, so download as .zip.
echo Downloading %NUGET_URL% ...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "Invoke-WebRequest -Uri '%NUGET_URL%' -OutFile '%WORK_DIR%\runtime.zip';" ^
  "if ((Get-Item '%WORK_DIR%\runtime.zip').Length -eq 0) { Write-Error 'download empty'; exit 1 };" ^
  "Expand-Archive -Path '%WORK_DIR%\runtime.zip' -DestinationPath '%WORK_DIR%\extracted' -Force;"
if errorlevel 1 (
  echo ERROR: download/extract failed ^(check version %RUNTIME_VERSION% exists on NuGet^)
  rmdir /S /Q "%WORK_DIR%" 2>nul
  exit /b 1
)

REM Native .so -> Android\lib\ (only .so; skip .a/.dex/.jar).
if not exist "%LIB_DIR%" mkdir "%LIB_DIR%"
del /Q "%LIB_DIR%\*.so" 2>nul
copy /Y "%WORK_DIR%\extracted\runtimes\android-arm64\native\*.so" "%LIB_DIR%\" >nul
echo Installed native .so to %LIB_DIR%

REM BCL managed .dll -> Android\runtime\.
if not exist "%RUNTIME_DIR%" mkdir "%RUNTIME_DIR%"
del /Q "%RUNTIME_DIR%\*.dll" 2>nul
copy /Y "%WORK_DIR%\extracted\runtimes\android-arm64\lib\net10.0\*.dll" "%RUNTIME_DIR%\" >nul

REM Count installed DLLs.
set "DLL_COUNT=0"
for %%f in ("%RUNTIME_DIR%\*.dll") do set /a "DLL_COUNT+=1"
echo Installed !DLL_COUNT! BCL .dll to %RUNTIME_DIR%

REM Cleanup temp.
rmdir /S /Q "%WORK_DIR%" 2>nul

echo === Done. CoreClrSDK populated for android-arm64 (.NET %RUNTIME_VERSION%). ===
endlocal
