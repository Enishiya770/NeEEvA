@echo off
rem Start everything EXCEPT the local LLM - for when ChatQW.m_Backend = Cloud
rem (DashScope). Leaves the full GPU to TTS / ASR / singing, so the
rem --n-cpu-moe workaround is unnecessary.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_services.ps1" start -Only embed,tts,asr,vc,svs %*
pause
