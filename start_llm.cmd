@echo off
rem Start ONLY the local LLM (llama-server on 8080).
rem Use this when you run the local backend and want to bring it up/down
rem independently of the rest of the stack.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_services.ps1" start -Only llm %*
pause
