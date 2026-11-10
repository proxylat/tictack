@echo off
rem TicTackSv SCM failure action: snapshot service state for post-mortem.
rem Runs as LocalSystem on crash; never fails the recovery chain.
setlocal
set "LOG=%~dp0failure-actions.log"
echo [%DATE% %TIME%] TicTackSv failure action fired>> "%LOG%"
sc query TicTackSv>> "%LOG%" 2>&1
tasklist /FI "IMAGENAME eq TicTackSv.exe" /FO TABLE>> "%LOG%" 2>&1
echo.>> "%LOG%"
