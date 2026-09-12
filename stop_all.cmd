@echo off
setlocal
set "stack_exit=0"
rem Stop managed ARDY, local voice services, SSH tunnel, and remote models.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_ardy.ps1" stop
if errorlevel 1 set "stack_exit=1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_services.ps1" stop -Only tts,asr,vc,svs %*
if errorlevel 1 set "stack_exit=1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_remote_llm.ps1" stop
if errorlevel 1 set "stack_exit=1"
pause
exit /b %stack_exit%
