@echo off
rem Double-click to uninstall Meeting Recorder for this account. Settings and recordings are kept.
rem   uninstall.cmd /quiet                     no pause at the end
rem   uninstall.cmd -RemoveData                also remove settings and local data
rem   uninstall.cmd -RemoveRecordings          also remove the recordings
rem   uninstall.cmd -Force                     also while a recording is in progress (it is saved and ends)
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
