@echo off
rem Stop only services managed by start_direct_api_all.cmd.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_direct_api_stack.ps1" stop %*
pause
