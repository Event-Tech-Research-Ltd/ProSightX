@echo off
rem Double-click to build a standalone ProSight.exe (win-x64). For Arm: build-windows.cmd -Runtime win-arm64
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-windows.ps1" %*
echo.
pause
