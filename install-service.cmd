@echo off
cd /d "%~dp0"
set "PS1=%~dp0app\install-core.ps1"
if not exist "%PS1%" set "PS1=%~dp0install.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" -ServiceOnly
if errorlevel 1 pause
