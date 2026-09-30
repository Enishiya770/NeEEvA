@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\start_unity_direct_services.ps1" %*
set "exitCode=%errorlevel%"
if not "%exitCode%"=="0" (
  echo Unity was not launched. Review the message above.
  pause
)
exit /b %exitCode%
