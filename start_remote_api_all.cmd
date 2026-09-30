@echo off
setlocal
cd /d "%~dp0"

rem Smoke check for the remote ARDY/Gradio and vLLM APIs.
rem This does not start a model or an SSH tunnel; the remote processes must
rem already be running. Use start_direct_api_all.cmd for Unity via 127.0.0.1:18080.
if /I "%~1"=="status" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_remote_api_stack.ps1" status
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_remote_api_stack.ps1" start %*
)
if errorlevel 1 goto :failed
echo.
echo Remote API smoke check finished successfully.
pause
exit /b 0

:failed
echo.
echo Remote API smoke check failed. Review the messages above and the saved event response.
pause
exit /b 1
