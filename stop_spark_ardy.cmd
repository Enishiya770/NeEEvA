@echo off
setlocal
rem Stop the recorded native ARDY process and the Spark Qwen LLM service.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_spark_ardy_stack.ps1" stop -StopLlm
set "exitCode=%errorlevel%"
echo.
if "%exitCode%"=="0" (
  echo Spark LLM and native ARDY stop command completed.
) else (
  echo Stop command failed. Check the lines above.
)
echo This window is being kept open because this script was started by double-click.
pause
exit /b %exitCode%
