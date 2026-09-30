@echo off
setlocal
chcp 65001 >nul
title NeEEvA - Personal Voice Learning
set "learning_script=%~dp0Tools\SingingSegmentation\manage-spark-learning.ps1"
set "learning_exit=1"
if not exist "%learning_script%" (
    echo [错误] 找不到学习服务脚本：
    echo "%learning_script%"
    echo 请将本启动文件保留在 NeEEvA 项目根目录。
    goto finish
)
echo 正在启动 NeEEvA 声音学习服务，请稍候...
echo.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%learning_script%" -Action start
set "learning_exit=%errorlevel%"
echo.
if not "%learning_exit%"=="0" goto failed
echo [就绪] Spark 学习服务地址：http://192.168.114.163:9884
echo 在 NeEEvA 中打开“声音学习”页，点击“连接”即可。
echo 关闭此窗口不会停止后台学习服务。
goto finish
:failed
echo [启动失败] 请查看上方错误信息及日志路径。
:finish
echo.
if /i "%~1"=="--no-pause" goto done
echo 按任意键关闭此窗口。
pause >nul
:done
exit /b %learning_exit%
