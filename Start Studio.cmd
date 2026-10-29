@echo off
rem Opens LaunchReel Studio from its one build folder, rebuilding first if the sources changed.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1" -IfStale
if errorlevel 1 (pause & exit /b 1)
start "" "%~dp0artifacts\studio\LaunchReel Studio.exe"
