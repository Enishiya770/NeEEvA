@echo off
rem 停止 NeEEvA 的全部本地服务。按端口对应的 PID 精确停止，
rem 不会像 llama.bat stop 那样按进程名连带杀掉其他 llama-server 实例。
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_services.ps1" stop %*
pause
