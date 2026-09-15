@echo off
setlocal
powershell.exe -NoLogo -NoProfile -NonInteractive -File "%~dp0run.ps1" %*
exit /b %ERRORLEVEL%
