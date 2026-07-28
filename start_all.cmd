@echo off
rem 一键启动 NeEEvA 的全部本地服务（LLM / 嵌入 / TTS / ASR / 歌声转换 / 歌声合成）
rem 已在运行的服务会自动跳过。日志在 Server\RuntimeLogs\
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\neeeva_services.ps1" start %*
pause
