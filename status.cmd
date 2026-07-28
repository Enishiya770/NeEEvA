@echo off
rem 查看 NeEEvA 各本地服务的运行状态与显存占用
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_services.ps1" status %*
pause
