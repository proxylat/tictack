@echo off
sc stop TicTackSv >nul 2>&1
sc delete TicTackSv
pause
