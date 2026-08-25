@echo off
rem Stop local voice services, SSH tunnel, and remote 5090 model services.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_services.ps1" stop -Only tts,asr,vc,svs %*
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_remote_llm.ps1" stop
pause
