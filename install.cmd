@echo off
rem Double-click to install Meeting Recorder for this account from the latest published build.
rem Run once per account; after that the app updates itself. Re-running repairs the install.
rem   install.cmd /quiet            no pause at the end
rem   install.cmd /quiet -NoStart   install without starting
rem   install.cmd /quiet -Force     also while a recording is in progress (it is saved and ends)
set "HERE=%~dp0"
set "QUIET="
if /i "%~1"=="/quiet" (
    set "QUIET=1"
    shift
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%HERE%scripts\install.ps1" %1 %2 %3
set "RESULT=%ERRORLEVEL%"
if not defined QUIET pause
exit /b %RESULT%
