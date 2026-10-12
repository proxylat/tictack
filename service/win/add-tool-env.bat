@echo off
:: add-tool-env.bat <name-or-path> — put a tool on the MACHINE PATH so TicTack
:: jobs (which run as LocalSystem) can resolve it. Restart the service after.
::   add-tool-env.bat py                      -- locate 'py' via where, add its dir
::   add-tool-env.bat C:\tools\restic         -- add this directory
::   add-tool-env.bat C:\tools\restic\restic.exe  -- add the exe's directory
setlocal EnableDelayedExpansion

if "%~1"=="" (
    echo Usage: %~nx0 ^<command-name-or-path^>
    echo   %~nx0 py                          -- locate via where, add its dir
    echo   %~nx0 C:\tools\restic             -- add this directory
    echo   %~nx0 C:\tools\restic\restic.exe  -- add the exe's directory
    exit /b 2
)

set "ARG=%~1"
:: Trailing-backslash-in-quotes mangles %~1 (\" escapes the quote): chop it.
if "%ARG:~-1%"=="""" set "ARG=%ARG:~0,-1%"
set "ARG=%ARG:/=\%"
set "TARGETDIR="
set "TOOL="

:: --- path form? (contains a backslash) ---
echo "%ARG%" | findstr "\\" >nul
if %errorlevel% equ 0 goto :pathform

:: --- name form: locate via where in the caller's context ---
set "HIT="
for /f "delims=" %%H in ('where "%ARG%" 2^>nul') do (
    if not defined HIT set "HIT=%%H"
)
if not defined HIT (
    echo ERROR: '%ARG%' not found via where. Pass its location instead:
    echo   %~nx0 C:\path\to\dir   or   %~nx0 C:\path\to\%ARG%.exe
    exit /b 1
)
set "TOOL=%ARG%"
for %%D in ("!HIT!") do set "TARGETDIR=%%~dpD"
goto :normalized

:pathform
if exist "%ARG%\*" (
    set "TARGETDIR=%ARG%"
) else if exist "%ARG%" (
    for %%F in ("%ARG%") do (
        set "TARGETDIR=%%~dpF"
        set "TOOL=%%~nF"
    )
) else (
    echo ERROR: path not found: %ARG%
    exit /b 1
)

:normalized
:: Strip one trailing backslash (but never break a drive root: C:\ stays).
if "!TARGETDIR:~-1!"=="\" set "TARGETDIR=!TARGETDIR:~0,-1!"
if "!TARGETDIR:~-1!"==":" set "TARGETDIR=!TARGETDIR!\"
if not exist "!TARGETDIR!\*" (
    echo ERROR: not a directory: !TARGETDIR!
    exit /b 1
)

:: --- append to machine PATH once (PowerShell: setx truncates long PATHs),
:: --- then broadcast WM_SETTINGCHANGE so new processes see it (no reboot).
set "PSCMD=$p=[Environment]::GetEnvironmentVariable('Path','Machine'); if ([string]::IsNullOrEmpty($p)) { $p='' }; $want=$env:TARGETDIR.TrimEnd('\'); $have=$p -split ';' | ForEach-Object { $_.Trim().TrimEnd('\') }; if ($have -notcontains $want) { $np=$p.TrimEnd(';'); if ($np -ne '') { $np+=';' }; $np+=$env:TARGETDIR; [Environment]::SetEnvironmentVariable('Path',$np,'Machine'); 'ADDED' } else { 'PRESENT' }; Add-Type -Namespace Win32 -Name EnvB -MemberDefinition '[DllImport(\"user32.dll\",CharSet=CharSet.Auto)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd,uint Msg,UIntPtr wParam,string lParam,uint fuFlags,uint uTimeout,out UIntPtr lpdwResult);'; $z=[UIntPtr]::Zero; [Win32.EnvB]::SendMessageTimeout([IntPtr]0xffff,0x1A,[UIntPtr]::Zero,'Environment',2,5000,[ref]$z) | Out-Null"
set "RESULT="
for /f "delims=" %%R in ('powershell -NoProfile -ExecutionPolicy Bypass -Command "%PSCMD%"') do (
    if not defined RESULT set "RESULT=%%R"
)
if not defined RESULT (
    echo ERROR: failed to update the machine PATH.
    exit /b 1
)

:: --- verify: re-read machine PATH, resolve exactly as the service will ---
for /f "delims=" %%P in ('powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('Path','Machine')"') do set "MPATH=%%P"
echo "!MPATH!" | findstr /I /C:"!TARGETDIR!" >nul
if %errorlevel% neq 0 (
    echo ERROR: verification failed, !TARGETDIR! is not on the machine PATH.
    exit /b 1
)
if defined TOOL (
    set "PATH=!MPATH!"
    "%SystemRoot%\System32\where.exe" "!TOOL!" >nul 2>&1
    if !errorlevel! neq 0 (
        echo ERROR: verification failed, '!TOOL!' does not resolve on the machine PATH.
        exit /b 1
    )
    echo Verified: '!TOOL!' resolves on the machine PATH.
)

echo.
if "%RESULT%"=="PRESENT" (
    echo Already present: !TARGETDIR! was on the machine PATH.
) else (
    echo Done: !TARGETDIR! added to the machine PATH.
)
echo Restart the TicTack service so jobs pick it up ^(env is snapshotted at start^):
echo   sc stop TicTackSv ^&^& sc start TicTackSv
