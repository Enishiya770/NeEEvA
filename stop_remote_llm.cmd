@echo off
rem Stop the local SSH tunnel and the qwen3.6/bge-m3 services on the RTX 5090.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_remote_llm.ps1" stop
pause
