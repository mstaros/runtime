@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-pipeline.ps1" %*
exit /b %ERRORLEVEL%
