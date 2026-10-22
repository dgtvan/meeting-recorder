@echo off
rem Build Meeting Recorder and publish it to D:\Apps\MeetingRecorder; running apps install it by themselves.
rem   publish.cmd /quiet   no pause at the end (for scripts and tools)
set "HERE=%~dp0"
set "QUIET="
if /i "%~1"=="/quiet" (
    set "QUIET=1"
    shift
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%HERE%scripts\publish.ps1" %1 %2 %3
set "RESULT=%ERRORLEVEL%"
if not defined QUIET pause
exit /b %RESULT%
