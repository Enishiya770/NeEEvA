@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\start_spark_all.ps1" %*
set "spark_exit=%errorlevel%"
if not "%spark_exit%"=="0" pause
exit /b %spark_exit%
