@echo off
sc stop TicTackSv
taskkill /F /IM TicTackSv.exe >nul 2>&1
sc config TicTackSv start=disabled >nul 2>&1
exit