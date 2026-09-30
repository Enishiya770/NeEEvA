@echo off
rem Show the direct API stack status, using the persisted profile when available.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_direct_api_stack.ps1" status %*
pause
