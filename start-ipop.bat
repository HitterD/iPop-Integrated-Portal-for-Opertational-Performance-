@echo off
REM Double-click to start IPop API + Web for https://ipop.santos.co.id  (-NoBuild, -Stop)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-ipop.ps1" %*
echo.
pause
