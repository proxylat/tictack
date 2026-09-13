@echo off
set "DIR=%~dp0"

sc stop TicTackSv >nul 2>&1
for /l %%i in (1,1,30) do (
    sc query TicTackSv >nul 2>&1 || goto :service_stopped
    timeout /t 1 /nobreak >nul
)
echo ERROR: TicTackSv did not stop in time. Aborting without deleting live files.
exit /b 1
:service_stopped
sc delete TicTackSv

:: Remove publish artifacts, keep config.yaml (user data)
del /q "%DIR%TicTackSv.exe" "%DIR%*.dll" "%DIR%*.runtimeconfig.json" "%DIR%*.deps.json" "%DIR%*.pdb" 2>nul

:: Remove MSBuild intermediates from src/
rmdir /s /q "%DIR%..\..\src\bin" 2>nul
rmdir /s /q "%DIR%..\..\src\obj" 2>nul
rmdir /s /q "%DIR%..\..\tests\bin" 2>nul
rmdir /s /q "%DIR%..\..\tests\obj" 2>nul

pause
