@echo off
sc stop TicTackSv
for /l %%i in (1,1,30) do (
    sc query TicTackSv | find "STOPPED" >nul && goto :stopped
    timeout /t 1 /nobreak >nul
)
echo ERROR: TicTackSv did not stop in time; refusing to force-kill it.
exit /b 1
:stopped
sc config TicTackSv start=disabled >nul 2>&1
exit
