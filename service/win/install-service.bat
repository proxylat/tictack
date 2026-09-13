@echo off
setlocal
set "DIR=%~dp0"
set "ROOT=%DIR%..\.."
set "EXE=%DIR%TicTackSv.exe"
set "CFG=%DIR%config.yaml"
set "SERVICE_NAME=TicTackSv"

if not exist "%CFG%" (
    echo ERROR: config.yaml not found. Copy config_win.yaml.example to config.yaml and edit it.
    pause
    exit /b 1
)

echo Publishing...
dotnet publish "%ROOT%\src\TicTack.csproj" -c Release -o "%DIR%."
if errorlevel 1 pause & exit /b 1

if not exist "%EXE%" (
    echo ERROR: Publish did not produce %EXE%
    pause
    exit /b 1
)

:: Stop and remove existing service
sc query "%SERVICE_NAME%" >nul 2>&1 && (
    sc stop "%SERVICE_NAME%" >nul 2>&1
    sc delete "%SERVICE_NAME%" >nul 2>&1
    for /l %%i in (1,1,30) do (
        sc query "%SERVICE_NAME%" >nul 2>&1 || goto :service_stopped
        timeout /t 1 /nobreak >nul
    )
    echo ERROR: Existing service did not stop in time.
    exit /b 1
)
:service_stopped

:: Install and start
sc create "%SERVICE_NAME%" binPath= "\"%EXE%\"" start= auto obj= LocalSystem DisplayName= "TicTack File Sync"
sc description "%SERVICE_NAME%" "Watches folders and syncs files to destinations."
sc failure "%SERVICE_NAME%" reset= 60 actions= restart/5000/restart/10000/restart/30000
sc start "%SERVICE_NAME%"

echo.
sc query "%SERVICE_NAME%"
echo.
pause
