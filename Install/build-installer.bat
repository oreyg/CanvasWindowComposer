@echo off
setlocal

:: Run from repo root
pushd "%~dp0.."

echo === Canvas Window Composer - Installer Builder ===
echo.

:: --- Check prerequisites ---

where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: .NET SDK not found. Install from https://dotnet.microsoft.com/download
    popd
    exit /b 1
)

set "NSIS="
if exist "%ProgramFiles(x86)%\NSIS\makensis.exe" set "NSIS=%ProgramFiles(x86)%\NSIS\makensis.exe"
if exist "%ProgramFiles%\NSIS\makensis.exe" set "NSIS=%ProgramFiles%\NSIS\makensis.exe"
if "%NSIS%"=="" (
    echo ERROR: NSIS not found. Install from https://nsis.sourceforge.io/Download
    popd
    exit /b 1
)

echo [OK] .NET SDK
echo [OK] NSIS: %NSIS%
echo.

:: --- Publish C# app ---

echo Publishing C# application...
:: Wipe stale output so a previous publish (or the test project) can't leave
:: behind a deps.json mismatching the current main-app build.
if exist "Install\publish-fd" rmdir /s /q "Install\publish-fd"
dotnet publish CanvasDesktop.csproj -c Release -o Install\publish-fd >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: dotnet publish failed.
    popd
    exit /b 1
)
echo [OK] C# publish
echo.

:: --- Build installer ---

echo Building installer...
"%NSIS%" Install\installer.nsi
if %errorlevel% neq 0 (
    echo ERROR: NSIS build failed.
    popd
    exit /b 1
)

echo.
echo === Build complete ===
echo Installer: Install\CanvasWindowComposer-Setup.exe
popd
