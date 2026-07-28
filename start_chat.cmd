@echo off
rem Start only the conversation stack: LLM / embedding / TTS / ASR.
rem Skips the singing services (9882/9883) so VRAM stays roomy.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_services.ps1" start -Only llm,embed,tts,asr %*
pause
