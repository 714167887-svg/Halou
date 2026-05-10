@echo off
chcp 65001 >nul
setlocal
set SCRIPT=%~dp0CAD Halou插件\publish-release.ps1
set /p MSG=本次发布说明（回车跳过）: 
if "%MSG%"=="" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Message "%MSG%"
)
echo.
pause
