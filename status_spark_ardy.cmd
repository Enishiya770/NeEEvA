@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_spark_ardy_stack.ps1" status %*
set "exitCode=%errorlevel%"
echo.
if "%exitCode%"=="0" (
  echo Spark LLM and native ARDY are ready.
) else (
  echo One or more Spark services are not ready. Check the lines above.
)
echo This window is being kept open because this script was started by double-click.
pause
exit /b %exitCode%
