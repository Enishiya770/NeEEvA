@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start_svs_server.ps1" %*
exit /b %ERRORLEVEL%
