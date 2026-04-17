@echo off
setlocal

echo ============================================
echo  Canvas Window Composer — Test Runner
echo ============================================
echo.

:: Unit tests
echo [1/2] Running unit tests...
dotnet test "%~dp0CanvasDesktop.Tests.csproj" --nologo -v quiet
set UNIT_EXIT=%ERRORLEVEL%
echo.

if %UNIT_EXIT%==0 (
    echo Unit tests: PASSED
) else (
    echo Unit tests: FAILED
)
echo.

:: Smoke tests
echo [2/2] Running smoke tests (AHK)...
set AHK=C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe
if not exist "%AHK%" (
    echo AutoHotkey v2 not found, skipping smoke tests.
    set SMOKE_EXIT=0
    goto :results
)

del /q "%~dp0smoke_test.log" 2>nul
start "" /wait "%AHK%" "%~dp0smoke_test.ahk"

if exist "%~dp0smoke_test.log" (
    echo.
    type "%~dp0smoke_test.log"
    findstr /c:"failed, 0 failed" "%~dp0smoke_test.log" >nul 2>&1
    if %ERRORLEVEL%==0 (
        set SMOKE_EXIT=0
    ) else (
        set SMOKE_EXIT=1
    )
) else (
    echo Smoke test log not found.
    set SMOKE_EXIT=1
)

:results
echo.
echo ============================================
if %UNIT_EXIT%==0 if %SMOKE_EXIT%==0 (
    echo  ALL TESTS PASSED
    exit /b 0
)
echo  SOME TESTS FAILED
exit /b 1
