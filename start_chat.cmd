@echo off
rem Conversation stack: remote LLM/embedding plus local TTS/ASR.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_remote_llm.ps1" start
if errorlevel 1 goto :failed
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_services.ps1" start -Only tts,asr %*
pause
exit /b 0
:failed
echo Remote LLM failed to start. Local voice services were not started.
pause
exit /b 1
