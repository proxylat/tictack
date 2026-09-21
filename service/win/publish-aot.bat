@echo off
setlocal
set "DIR=%~dp0"
set "ROOT=%DIR%..\.."
set "OUT=%DIR%aot"

echo Publishing NativeAOT TicTackSv (needs VS Build Tools with MSVC on this machine)...
dotnet publish "%ROOT%\src\TicTack.csproj" -p:PublishProfile=Aot -o "%OUT%"
if errorlevel 1 pause & exit /b 1

if not exist "%OUT%\TicTackSv.exe" (
    echo ERROR: Publish did not produce %OUT%\TicTackSv.exe
    pause
    exit /b 1
)

echo.
echo AOT binary:
dir "%OUT%\TicTackSv.exe"
echo.
echo Smoke test: copy config.yaml next to it, then run --validate and --once.
pause
