@echo off
setlocal
set "DIR=%~dp0"
set "ROOT=%DIR%..\.."
set "EXE=%DIR%Corpus.exe"
set "CFG=%DIR%config_corpus.yaml"
set "EXAMPLE=%DIR%config_corpus.yaml.example"

if "%~1"=="--aot" goto :aot
if "%~1"=="--jit" goto :jit
if not "%~1"=="" goto :usage

echo Publishing framework-dependent (needs the .NET 10 runtime on this machine)...
dotnet publish "%ROOT%\corpus\Corpus.csproj" -c Release -o "%DIR%."
if errorlevel 1 pause & exit /b 1
goto :done

:jit
echo Publishing JIT single-file (runtime bundled, compiles at startup)...
dotnet publish "%ROOT%\corpus\Corpus.csproj" -c Release -r win-x64 --self-contained /p:PublishSingleFile=true -o "%DIR%."
if errorlevel 1 pause & exit /b 1
goto :done

:aot
:: Drop stale JIT sidecars so the dir holds exactly the AOT exe + config.
del /q "%DIR%*.dll" "%DIR%*.runtimeconfig.json" "%DIR%*.deps.json" "%DIR%*.pdb" 2>nul

echo Publishing NativeAOT (needs VS Build Tools with MSVC on this machine)...
dotnet publish "%ROOT%\corpus\Corpus.csproj" -p:PublishProfile=Aot -o "%DIR%."
if errorlevel 1 pause & exit /b 1
goto :done

:usage
echo Usage: %~nx0 [--jit ^| --aot]
echo   (no args)         framework-dependent, needs the .NET 10 runtime
echo   --jit             single exe with the runtime bundled (compiles at startup)
echo   --aot             NativeAOT single exe (experimental: NO VSS imaging, COM interop unsupported)
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
    echo Created %CFG% from the example - edit it before imaging.
)

echo.
echo Built: %EXE%
echo Smoke test (elevated): "%EXE%" --validate
echo.
pause
