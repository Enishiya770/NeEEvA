@echo off
setlocal
set "stack_exit=0"
rem Show local ARDY, voice services, tunnel health, and both GPUs.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_ardy.ps1" status
if errorlevel 1 set "stack_exit=1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_services.ps1" status -Only tts,asr,vc,svs %*
if errorlevel 1 set "stack_exit=1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_remote_llm.ps1" status
if errorlevel 1 set "stack_exit=1"
pause
exit /b %stack_exit%
