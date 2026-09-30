@echo off
rem One-click Unity stack using the direct LAN vLLM endpoint.
rem The default chat endpoint is 192.168.114.175:8888. Use
rem -Transport Tunnel -InteractiveSsh to switch to the DGX SSH tunnel.
rem The launcher then starts the voice services and remote ARDY on local port 8093.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_direct_api_stack.ps1" start %*
if errorlevel 1 goto :failed
echo.
echo Direct API stack startup finished.
pause
exit /b 0
:failed
echo.
echo One or more services failed. See Server\RuntimeLogs\*.err.log.
pause
exit /b 1
