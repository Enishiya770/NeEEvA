@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\start_spark_all.ps1" -Action status %*
set "spark_exit=%errorlevel%"
pause
exit /b %spark_exit%
