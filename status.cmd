@echo off
rem Show local voice services, tunnel health, and both GPUs.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_services.ps1" status -Only tts,asr,vc,svs %*
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_remote_llm.ps1" status
pause
