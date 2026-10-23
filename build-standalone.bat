@echo off
setlocal
cd /d "%~dp0"
echo ============================================================
echo  Building DS4-SoundLink Standalone Windows Executable
echo ============================================================
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\build.ps1" -Standalone
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Build failed with exit code %ERRORLEVEL%.
    pause
    exit /b %ERRORLEVEL%
)
echo.
echo [SUCCESS] Standalone executable is ready at bin\standalone\DS4SoundLink.exe
pause
