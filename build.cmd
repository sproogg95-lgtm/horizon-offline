@echo off
rem Build both executables (needs only Windows + .NET Framework 4.x, which every Windows 10/11 has).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
pause
