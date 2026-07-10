@echo off
REM Double-click this to register the Hooker hooks in your user settings.json.
REM It runs entirely as you, outside Claude.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-hook.ps1"
echo.
echo Done. Restart any running Claude Code sessions to load the hooks.
pause
