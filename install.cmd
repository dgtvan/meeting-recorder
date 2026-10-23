@echo off
rem Installs the build in dist\ for this account, starts it at sign-in and starts it now.
rem Run build.cmd first. Re-running it is safe.
rem   install.cmd /quiet            no pause at the end
rem   install.cmd /quiet -NoStart   install without starting
rem   install.cmd /quiet -Force     close the app even while recording (the recording is saved and ends)
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
