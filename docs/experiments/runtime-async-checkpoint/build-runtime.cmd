@echo off
setlocal
if "%~1"=="" (
  echo Usage: build-runtime.cmd path-to-existing-runtime-repo
  exit /b 2
)
set "DOTNET_INSTALL_DIR=%~1\.dotnet"
set "DOTNET_ROOT=%DOTNET_INSTALL_DIR%"
call "%~dp0..\..\..\build.cmd" clr.runtime+clr.corelib -c Release -rc Release -lc Release
exit /b %ERRORLEVEL%
