@echo off
setlocal
rem Start GPT-SoVITS (9880), SenseVoice (9881), and SeedVC/RVC (9882).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\start_voice_services.ps1"
set "voice_exit=%errorlevel%"
echo.
if not "%voice_exit%"=="0" echo Voice startup failed. Review the messages above and Server\RuntimeLogs.
pause
exit /b %voice_exit%
