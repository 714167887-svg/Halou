@echo off
setlocal

set "PROJECT_ROOT=%~dp0.."
set "CENTRAL_SCRIPT="

for /f "delims=" %%F in ('dir /s /b "%PROJECT_ROOT%\..\open-jsqcad-generator.bat" ^| findstr /i /v "autocad-clipboard-plugin-autoon-test\\scripts"') do (
    set "CENTRAL_SCRIPT=%%F"
    goto :found
)

:found

if not exist "%CENTRAL_SCRIPT%" (
    echo Central launcher not found: %CENTRAL_SCRIPT%
    exit /b 1
)

call "%CENTRAL_SCRIPT%"
exit /b %errorlevel%
