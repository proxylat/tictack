@echo off
set "DIR=%~dp0"
rmdir /s /q "%DIR%..\..\src\bin" 2>nul
rmdir /s /q "%DIR%..\..\src\obj" 2>nul
rmdir /s /q "%DIR%..\..\tests\bin" 2>nul
rmdir /s /q "%DIR%..\..\tests\obj" 2>nul
