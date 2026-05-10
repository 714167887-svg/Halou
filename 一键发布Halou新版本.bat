@echo off
setlocal
chcp 65001 >nul
set "SCRIPT_DIR=%~dp0"

if "%~1"=="" (
    echo 用法: 一键发布Halou新版本.bat ^<新版本号^> "<更新说明>"
    echo 示例: 一键发布Halou新版本.bat 1.1.44 "1.1.44 修复 XXX 问题"
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%CAD Halou插件\release.ps1" -NewVersion %1 -Notes %2
pause
