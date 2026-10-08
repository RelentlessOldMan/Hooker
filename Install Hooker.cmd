@echo off
REM Double-click this to register the Hooker hooks in your user settings.json.
REM It runs entirely as you, outside Claude.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-hook.ps1"
if errorlevel 1 (
    echo.
    echo Install FAILED - see the error above. Your settings.json was left as it was.
    pause
    exit /b 1
)
echo.
echo Done. Restart any running Claude Code sessions to load the hooks.
pause
