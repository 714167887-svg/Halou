@echo off
setlocal

set PROJECT_ROOT=%~dp0..
set CENTRAL_SCRIPT=

for /f "delims=" %%F in ('dir /s /b "%PROJECT_ROOT%\..\build-autocad-plugin.ps1"') do (
  set CENTRAL_SCRIPT=%%F
  goto :found
)

:found

if not exist "%CENTRAL_SCRIPT%" (
  echo Central build script not found: %CENTRAL_SCRIPT%
  exit /b 1
)

powershell -ExecutionPolicy Bypass -File "%CENTRAL_SCRIPT%" -ProjectRoot "%PROJECT_ROOT%"
if errorlevel 1 exit /b 1

