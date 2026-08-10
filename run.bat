@echo off
REM Double-click launcher — tries pwsh first (PowerShell 7), falls back to Windows PowerShell 5.1
where pwsh >nul 2>nul
if %errorlevel%==0 (
  pwsh -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
) else (
  powershell -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
)
set RC=%errorlevel%
if %RC% neq 0 (
  echo.
  echo [run.bat] exited with code %RC%
  pause
)
exit /b %RC%
