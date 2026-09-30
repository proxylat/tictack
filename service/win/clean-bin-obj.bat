@echo off
set "DIR=%~dp0"
rmdir /s /q "%DIR%..\..\benchmarks\bin" 2>nul
rmdir /s /q "%DIR%..\..\benchmarks\obj" 2>nul
rmdir /s /q "%DIR%..\..\src\bin" 2>nul
rmdir /s /q "%DIR%..\..\src\obj" 2>nul
rmdir /s /q "%DIR%..\..\tests\bin" 2>nul
rmdir /s /q "%DIR%..\..\tests\obj" 2>nul
rmdir /s /q "%DIR%..\..\tests\CrashWorker\bin" 2>nul
rmdir /s /q "%DIR%..\..\tests\CrashWorker\obj" 2>nul
rmdir /s /q "C:\ProgramData\TicTack" 2>nul

