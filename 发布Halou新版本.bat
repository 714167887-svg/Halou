@echo off
chcp 65001 >nul
setlocal
set "VER=%~1"
shift
set "MSG=%~1"
if "%VER%"=="" (
    echo 用法： 发布Halou新版本.bat 2.0.1 "本次更新说明"
    echo.
    echo 当前 license.json 里的 latest_version 必须比新版本号小才会推送。
    pause
    exit /b 1
)
powershell -ExecutionPolicy Bypass -NoProfile -File "%~dp0CAD Halou插件\publish-halou-suite.ps1" -Version %VER% -Message "%MSG%"
pause
