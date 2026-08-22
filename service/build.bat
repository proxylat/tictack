@echo off
setlocal
set "DIR=%~dp0"
set "ROOT=%DIR%.."

dotnet clean "%ROOT%\src\TicTack.csproj" -c Release >nul 2>&1
dotnet restore "%ROOT%\src\TicTack.csproj"
pause
if errorlevel 1 pause

dotnet build "%ROOT%\src\TicTack.csproj" -c Release --no-restore
if errorlevel 1 pause

copy /y "%ROOT%\src\bin\Release\net10.0-windows\TicTackSv.exe" "%DIR%" >nul
copy /y "%ROOT%\src\bin\Release\net10.0-windows\TicTackSv.runtimeconfig.json" "%DIR%" >nul 2>&1
copy /y "%ROOT%\src\bin\Release\net10.0-windows\TicTackSv.deps.json" "%DIR%" >nul 2>&1
copy /y "%ROOT%\src\bin\Release\net10.0-windows\*.dll" "%DIR%" >nul
copy /y "%USERPROFILE%\.nuget\packages\system.serviceprocess.servicecontroller\10.0.9\lib\net10.0\System.ServiceProcess.ServiceController.dll" "%DIR%"
if errorlevel 1 echo WARNING: Could not find SPC DLL in NuGet cache (CopyLocalLockFileAssemblies should handle it)
copy /y "%USERPROFILE%\.nuget\packages\system.diagnostics.eventlog\10.0.9\lib\net10.0\System.Diagnostics.EventLog.dll" "%DIR%"
if errorlevel 1 echo WARNING: Could not find EventLog DLL in NuGet cache (CopyLocalLockFileAssemblies should handle it)
echo Built: %DIR%TicTackSv.exe
