@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-proof.ps1" %*
exit /b %ERRORLEVEL%
