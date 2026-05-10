@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ================================================
echo   钣金激光图拆分工具
echo   将本文件夹内的 .dxf 拆分为独立的钣金图 DXF
echo ================================================
echo.

python split.py %*

echo.
pause
