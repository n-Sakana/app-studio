@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0app-studio.ps1"
endlocal

