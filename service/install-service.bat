@echo off
setlocal
set "DIR=%~dp0"
set "ROOT=%DIR%.."
set "EXE=%DIR%TicTackSv.exe"
set "CFG=%DIR%config.yaml"
set "SERVICE_NAME=TicTackSv"

if not exist "%CFG%" (
    echo ERROR: config.yaml not found.
    pause
    exit /b 1
)

echo Building...
dotnet restore "%ROOT%\src\TicTack.csproj"
if errorlevel 1 pause & exit /b 1

dotnet build "%ROOT%\src\TicTack.csproj" -c Release --no-restore
if errorlevel 1 pause & exit /b 1
copy /y "%ROOT%\src\bin\Release\net10.0-windows\TicTackSv.exe" "%EXE%" >nul
copy /y "%ROOT%\src\bin\Release\net10.0-windows\TicTackSv.runtimeconfig.json" "%DIR%" >nul 2>&1
copy /y "%ROOT%\src\bin\Release\net10.0-windows\TicTackSv.deps.json" "%DIR%" >nul 2>&1
copy /y "%ROOT%\src\bin\Release\net10.0-windows\*.dll" "%DIR%"
if not exist "%DIR%System.ServiceProcess.ServiceController.dll" copy /y "%USERPROFILE%\.nuget\packages\system.serviceprocess.servicecontroller\10.0.9\runtimes\win\lib\net10.0\System.ServiceProcess.ServiceController.dll" "%DIR%"
if not exist "%DIR%System.ServiceProcess.ServiceController.dll" copy /y "%USERPROFILE%\.nuget\packages\system.serviceprocess.servicecontroller\10.0.9\runtimes\win\lib\net9.0\System.ServiceProcess.ServiceController.dll" "%DIR%"
if not exist "%DIR%System.Diagnostics.EventLog.dll" copy /y "%USERPROFILE%\.nuget\packages\system.diagnostics.eventlog\10.0.9\runtimes\win\lib\net10.0\System.Diagnostics.EventLog.dll" "%DIR%"
if not exist "%DIR%System.Diagnostics.EventLog.dll" copy /y "%USERPROFILE%\.nuget\packages\system.diagnostics.eventlog\10.0.9\runtimes\win\lib\net9.0\System.Diagnostics.EventLog.dll" "%DIR%"
if not exist "%DIR%e_sqlite3.dll" copy /y "%USERPROFILE%\.nuget\packages\sqlitepclraw.lib.e_sqlite3\2.1.11\runtimes\win-x64\native\e_sqlite3.dll" "%DIR%"
if not exist "%DIR%e_sqlite3.dll" copy /y "%USERPROFILE%\.nuget\packages\sqlitepclraw.lib.e_sqlite3\2.1.11\runtimes\win-x86\native\e_sqlite3.dll" "%DIR%"
if not exist "%DIR%System.ServiceProcess.ServiceController.dll" echo WARNING: System.ServiceProcess.ServiceController.dll not found - service may fail with 1053
if not exist "%DIR%System.Diagnostics.EventLog.dll" echo WARNING: System.Diagnostics.EventLog.dll not found - service may fail with 1053
if not exist "%DIR%e_sqlite3.dll" echo WARNING: e_sqlite3.dll not found - StateDB will fail

if not exist "%EXE%" (
    echo ERROR: Build did not produce %EXE%
    pause
    exit /b 1
)

:: Stop and remove existing service
sc query "%SERVICE_NAME%" >nul 2>&1 && (
    sc stop "%SERVICE_NAME%" >nul 2>&1
    sc delete "%SERVICE_NAME%" >nul 2>&1
    timeout /t 3 /nobreak >nul
)

:: Install and start
sc create "%SERVICE_NAME%" binPath= "\"%EXE%\"" start= auto obj= LocalSystem DisplayName= "TicTack File Sync"
sc description "%SERVICE_NAME%" "Watches folders and syncs files to destinations."
sc failure "%SERVICE_NAME%" reset= 60 actions= restart/5000/restart/10000/restart/30000
sc start "%SERVICE_NAME%"

echo.
sc query "%SERVICE_NAME%"
echo.
pause
