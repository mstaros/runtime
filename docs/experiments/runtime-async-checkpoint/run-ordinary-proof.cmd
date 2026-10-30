@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-ordinary-proof.ps1" %*
exit /b %ERRORLEVEL%
