@echo off
rem ---------------------------------------------------------------------------
rem  Opens the seed finder in your browser. This is the one to double-click.
rem
rem    seed-finder.bat             build, serve on 5173, open the browser
rem    seed-finder.bat 8080        same, on a different port
rem    seed-finder.bat /nobuild    skip the build (fastest restart)
rem
rem  The server runs in the foreground of THIS window, so closing the window or
rem  pressing Ctrl+C stops it. Nothing is left listening afterwards.
rem ---------------------------------------------------------------------------
setlocal EnableExtensions
cd /d "%~dp0"
title StS2 Co-op Seed Finder

rem MSBuild leaves its worker processes running for 15 minutes after a build so the next
rem one starts faster. Right for a developer building all day, wrong for a launcher: a
rem user who closes this window would otherwise still have three dotnet.exe running and
rem no way to tell whose they are. A variable rather than a build switch, so anything
rem this script calls that builds in turn inherits it.
set "MSBUILDDISABLENODEREUSE=1"

set "PORT=5173"
set "BUILD=1"

:parse
if "%~1"=="" goto parsed
if /i "%~1"=="/nobuild" (set "BUILD=0") else (set "PORT=%~1")
shift
goto parse
:parsed

set "EXE=src\Sts2.SeedFinder.Web\bin\Release\net10.0\sts2seedweb.exe"

rem taskkill alone, rather than tasklist piped through find: it already does nothing
rem harmlessly when no such process exists, and returns 0 only when it actually killed
rem something. Piping through `find` looked tidier but depends on Windows' find.exe
rem winning the PATH. A user with Git for Windows' Unix tools installed gets GNU find,
rem which rejects /i, reports "not running", and leaves a live server holding the DLL
rem open so the build fails for a reason nothing on screen explains.
taskkill /f /im sts2seedweb.exe >nul 2>&1
if not errorlevel 1 (
    echo Stopped a seed finder that was already running.
    rem let Windows release the file lock before we build over it
    ping -n 2 127.0.0.1 >nul
)

if "%BUILD%"=="1" (
    echo Building...
    dotnet build -c Release --nologo -v quiet
    if errorlevel 1 (
        echo.
        echo Build failed - see the errors above.
        pause
        exit /b 1
    )
)

if not exist "%EXE%" (
    echo.
    echo Not built yet: %EXE%
    echo Run this once without /nobuild.
    pause
    exit /b 1
)

rem Open the browser only once the port accepts connections, so the first paint
rem is the app rather than "can't reach this page".
start "" /b powershell -NoProfile -WindowStyle Hidden -Command "for($i=0;$i -lt 160;$i++){try{$c=New-Object Net.Sockets.TcpClient;$c.Connect('127.0.0.1',%PORT%);$c.Close();Start-Process 'http://localhost:%PORT%';break}catch{Start-Sleep -Milliseconds 250}}"

echo.
echo   Seed finder:  http://localhost:%PORT%
echo   Close this window or press Ctrl+C to stop it.
echo.

"%EXE%" --urls "http://localhost:%PORT%"
