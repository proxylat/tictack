@echo off
setlocal
set "DIR=%~dp0"
set "ROOT=%DIR%..\.."
set "EXE=%DIR%TicTackSv.exe"
set "CFG=%DIR%config.yaml"
set "EXAMPLE=%ROOT%\service\win\config_win.yaml.example"
set "SERVICE_NAME=TicTackSv"
set "DESC=TicTack real-time one-way file sync. Mirrors active user folders to a backup drive."

if "%~1"=="--aot" goto :aot
if "%~1"=="--jit" goto :jit
if not "%~1"=="" goto :usage

echo Publishing framework-dependent (needs the .NET 10 runtime on this machine)...
dotnet publish "%ROOT%\src\TicTack.csproj" -c Release -o "%DIR%."
if errorlevel 1 pause & exit /b 1
goto :done

:jit
echo Publishing JIT single-file (runtime bundled, compiles at startup)...
dotnet publish "%ROOT%\src\TicTack.csproj" -c Release -r win-x64 --self-contained /p:PublishSingleFile=true -o "%DIR%."
if errorlevel 1 pause & exit /b 1
goto :done

:aot
:: Drop stale JIT sidecars so the dir holds exactly the AOT exe + config.
del /q "%DIR%*.dll" "%DIR%*.runtimeconfig.json" "%DIR%*.deps.json" "%DIR%*.pdb" 2>nul

echo Publishing NativeAOT (needs VS Build Tools with MSVC on this machine)...
dotnet publish "%ROOT%\src\TicTack.csproj" -p:PublishProfile=Aot -o "%DIR%."
if errorlevel 1 pause & exit /b 1
goto :done

:usage
echo Usage: %~nx0 [--jit ^| --aot]
echo   (no args)         framework-dependent, needs the .NET 10 runtime
echo   --jit             single exe with the runtime bundled (compiles at startup)
echo   --aot             NativeAOT single exe, instant start (needs VS Build Tools with MSVC)
echo Builds TicTackSv.exe then installs/starts the %SERVICE_NAME% service.
pause
exit /b 1

:done
if not exist "%EXE%" (
    echo ERROR: Publish did not produce %EXE%
    pause
    exit /b 1
)

if not exist "%CFG%" (
    copy "%EXAMPLE%" "%CFG%" >nul
    echo Created %CFG% from the example - edit it for this machine, then run %~nx0 again.
    pause
    exit /b 1
)

:: Install the service (always).
sc query "%SERVICE_NAME%" >nul 2>&1 && (
    echo Stopping/removing existing %SERVICE_NAME% service...
    sc stop "%SERVICE_NAME%" >nul 2>&1

    for /L %%i in (1,1,20) do (
        sc query "%SERVICE_NAME%" | find /i "STOPPED" >nul 2>&1
        if not errorlevel 1 goto :service_stopped
        timeout /t 1 /nobreak >nul 2>&1
    )

    echo ERROR: Service %SERVICE_NAME% did not stop. Aborting install.
    pause
    exit /b 1

    :service_stopped
)

sc delete "%SERVICE_NAME%" >nul 2>&1

echo Installing %SERVICE_NAME% service...
sc create "%SERVICE_NAME%" binPath= "\"%EXE%\"" start= auto error= normal depend= "LanmanWorkstation/VSS"
if errorlevel 1 (
    echo ERROR: Failed to create %SERVICE_NAME% service.
    pause
    exit /b 1
)

sc description "%SERVICE_NAME%" "%DESC%"
sc failure "%SERVICE_NAME%" reset= 86400 actions= restart/60000/restart/60000/restart/60000

echo Starting %SERVICE_NAME% service...
sc start "%SERVICE_NAME%"

echo.
sc query "%SERVICE_NAME%"
echo.
pause
