@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0analyze-profile.ps1" %*
exit /b %ERRORLEVEL%
