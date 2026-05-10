@echo off
setlocal
chcp 65001 >nul
set "ANCHOR=install-autocad-autostart.ps1"
set "SCRIPT="

for /f "delims=" %%F in ('dir /s /b "%~dp0%ANCHOR%" 2^>nul') do (
    if not defined SCRIPT set "SCRIPT=%%F"
)

if not defined SCRIPT (
    for /f "delims=" %%F in ('dir /s /b "%~dp0W\%ANCHOR%" 2^>nul') do (
        if not defined SCRIPT set "SCRIPT=%%F"
    )
)

if not defined SCRIPT (
    echo [错误] 未找到 %ANCHOR%。
    pause
    exit /b 1
)

echo 使用脚本：%SCRIPT%
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Uninstall
set ERR=%ERRORLEVEL%
echo.
if %ERR% NEQ 0 (
    echo [失败] 退出码 %ERR%
) else (
    echo [完成]
)
pause
exit /b %ERR%
