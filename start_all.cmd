@echo off
rem Default stack: shared Qwen action features + embeddings on 5090, ARDY + voice on 4090.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_remote_llm.ps1" start -Mode feature
if errorlevel 1 goto :failed
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_ardy.ps1" start
if errorlevel 1 goto :ardy_failed
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_services.ps1" start -Only tts,asr,vc,svs %*
if errorlevel 1 goto :local_failed
pause
exit /b 0
:failed
echo Shared Qwen action-feature startup failed. Local startup was not attempted.
echo If a legacy Qwen is running, stop the stack when idle, then run start_all.cmd again.
pause
exit /b 1
:ardy_failed
echo ARDY failed to become ready. See Server\RuntimeLogs\ardy.err.log.
echo Existing services were left running. Voice startup was not attempted.
pause
exit /b 1
:local_failed
echo Local voice startup failed. Existing Qwen and ARDY were left running.
pause
exit /b 1
