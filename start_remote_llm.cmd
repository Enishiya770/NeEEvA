@echo off
rem Start qwen3.6 and bge-m3 on the RTX 5090, then forward ports locally.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_remote_llm.ps1" start
pause
