@echo off
rem Default stack: qwen3.6 + embeddings on RTX 5090, voice services on RTX 4090.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_remote_llm.ps1" start
if errorlevel 1 goto :failed
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_services.ps1" start -Only tts,asr,vc,svs %*
pause
exit /b 0
:failed
echo Remote LLM failed to start. Nothing else was started.
pause
exit /b 1
