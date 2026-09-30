@echo off
setlocal
rem One-click Spark stack: reuse/start the Qwen LLM, deploy/start native ARDY,
rem and expose it on the private switch at 192.168.114.163:8093.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_spark_ardy_stack.ps1" start -AllowIndependentFeatureModel -AllowUnverifiedAdapter %*
set "exitCode=%errorlevel%"
if not "%exitCode%"=="0" (
  echo Spark stack did not become ready. Check Server\RuntimeLogs and the Spark ARDY log.
) else (
  echo.
  echo Spark LLM and native ARDY are ready. Unity ARDY URL: http://192.168.114.163:8093
)
echo.
echo This window is being kept open because this script was started by double-click.
pause
exit /b %exitCode%
