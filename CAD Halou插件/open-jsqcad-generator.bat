@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "SCRIPT_PATH=%SCRIPT_DIR%\popup_jsqcad_generator.py"

if exist "%SCRIPT_DIR%\..\.venv\Scripts\python.exe" (
    "%SCRIPT_DIR%\..\.venv\Scripts\python.exe" "%SCRIPT_PATH%"
    goto :eof
)

if exist "%SCRIPT_DIR%\..\venv\Scripts\python.exe" (
    "%SCRIPT_DIR%\..\venv\Scripts\python.exe" "%SCRIPT_PATH%"
    goto :eof
)

for /d %%D in ("%LocalAppData%\Programs\Python\Python3*") do (
    if exist "%%~fD\python.exe" (
        "%%~fD\python.exe" "%SCRIPT_PATH%"
        goto :eof
    )
)

where py >nul 2>nul
if not errorlevel 1 (
    py -3 "%SCRIPT_PATH%"
    goto :eof
)

where python >nul 2>nul
if not errorlevel 1 (
    python "%SCRIPT_PATH%"
    goto :eof
)

echo [open-jsqcad-generator] Python was not found.
echo Install Python 3 and enable PATH, or create .venv\Scripts\python.exe in the project.
pause
exit /b 1
