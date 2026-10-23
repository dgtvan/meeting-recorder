@echo off
rem Removes Meeting Recorder for this account. Settings, logs and recordings are kept.
rem   uninstall.cmd /quiet          no pause at the end
rem   uninstall.cmd /quiet -Force   close the app even while recording (the recording is saved and ends)
set "HERE=%~dp0"
set "QUIET="
if /i "%~1"=="/quiet" (
    set "QUIET=1"
    shift
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%HERE%scripts\uninstall.ps1" %1 %2 %3
set "RESULT=%ERRORLEVEL%"
if not defined QUIET pause
exit /b %RESULT%
