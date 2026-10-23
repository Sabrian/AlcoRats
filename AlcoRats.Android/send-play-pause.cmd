@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0send-play-pause.ps1" %*
exit /b %ERRORLEVEL%
