@echo off
setlocal

set "SCRIPT="

for /f "delims=" %%F in ('dir /s /b "%~dp0build-autocad-plugin.ps1"') do (
  set "SCRIPT=%%F"
  goto :found
)

:found

if not exist "%SCRIPT%" (
  echo Build script not found: %SCRIPT%
  exit /b 1
)

powershell -ExecutionPolicy Bypass -File "%SCRIPT%"
exit /b %errorlevel%
