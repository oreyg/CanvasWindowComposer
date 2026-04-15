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

where cmake >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: CMake not found. Install from https://cmake.org/download/
    popd
    exit /b 1
)

echo [OK] .NET SDK
echo [OK] NSIS: %NSIS%
echo [OK] CMake
echo.

:: --- Build native DLL ---

echo Building native DpiHook.dll...
pushd native
cmake -B build -G "Visual Studio 17 2022" -A x64 >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: CMake configure failed. Make sure Visual Studio 2022 with C++ workload is installed.
    popd & popd
    exit /b 1
)
cmake --build build --config Release >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Native DLL build failed.
    popd & popd
    exit /b 1
)
popd
echo [OK] DpiHook.dll
echo.

:: --- Publish C# app ---

echo Publishing C# application...
dotnet publish -c Release -o Install\publish-fd >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: dotnet publish failed.
    popd
    exit /b 1
)

:: Copy native DLL into publish output
copy /y "bin\Release\net8.0-windows\DpiHook.dll" "Install\publish-fd\" >nul 2>&1
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
