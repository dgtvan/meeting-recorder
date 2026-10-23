@echo off
rem Builds Meeting Recorder into dist\ (release). Installs nothing.
rem   build.cmd /quiet              no pause at the end
set "HERE=%~dp0"
set "QUIET="
if /i "%~1"=="/quiet" (
    set "QUIET=1"
    shift
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%HERE%scripts\build.ps1" %1 %2 %3
set "RESULT=%ERRORLEVEL%"
if not defined QUIET pause
exit /b %RESULT%
