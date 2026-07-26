@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install_soulx.ps1" %*
set "NEEEVA_EXIT_CODE=%ERRORLEVEL%"
if not "%NEEEVA_EXIT_CODE%"=="0" (
    echo.
    echo SoulX-Singer installation failed with exit code %NEEEVA_EXIT_CODE%.
)
exit /b %NEEEVA_EXIT_CODE%
