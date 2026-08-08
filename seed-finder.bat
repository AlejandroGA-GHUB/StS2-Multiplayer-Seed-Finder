@echo off
rem ---------------------------------------------------------------------------
rem  Opens the seed finder. This is the one to double-click.
rem
rem    seed-finder.bat             build and open the app
rem    seed-finder.bat /browser    ... in your browser instead, on port 5173
rem    seed-finder.bat /browser 8080   ... on a different port
rem    seed-finder.bat /nobuild    skip the build (fastest restart)
rem
rem  Two ways in, one program. The app is a window around the same seed finder the
rem  browser route serves: it starts the same server on a port of its own and shows
rem  it without an address bar. Nothing about searching differs between them.
rem
rem  /browser is worth keeping for three real cases. It is the only route on Linux
rem  and macOS, where the app cannot follow (it needs WebView2, which is Windows
rem  only). It is the only way to have two searches open side by side, since the app
rem  is a single window. And it is the fallback if WebView2 is missing or refuses to
rem  start, which the app will do for you automatically rather than failing.
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
set "BROWSER=0"

:parse
if "%~1"=="" goto parsed
if /i "%~1"=="/nobuild" (
    set "BUILD=0"
) else if /i "%~1"=="/browser" (
    set "BROWSER=1"
) else (
    set "PORT=%~1"
)
shift
goto parse
:parsed

set "APP=src\Sts2.SeedFinder.Shell\bin\Release\net10.0-windows\sts2seedfinder.exe"
set "WEB=src\Sts2.SeedFinder.Web\bin\Release\net10.0\sts2seedweb.exe"

rem taskkill alone, rather than tasklist piped through find: it already does nothing
rem harmlessly when no such process exists, and returns 0 only when it actually killed
rem something. Piping through `find` looked tidier but depends on Windows' find.exe
rem winning the PATH. A user with Git for Windows' Unix tools installed gets GNU find,
rem which rejects /i, reports "not running", and leaves a live server holding the DLL
rem open so the build fails for a reason nothing on screen explains.
rem
rem Both names, whichever route was asked for: either one holds the build output open,
rem and the app owns a server of its own that has to go with it.
set "STOPPED=0"
taskkill /f /im sts2seedfinder.exe >nul 2>&1
if not errorlevel 1 set "STOPPED=1"
taskkill /f /im sts2seedweb.exe >nul 2>&1
if not errorlevel 1 set "STOPPED=1"
if "%STOPPED%"=="1" (
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

if "%BROWSER%"=="1" goto browser

rem ---- App ------------------------------------------------------------------
rem
rem No port argument here on purpose. The app takes an unused port of its own so it
rem can run alongside a browser instance without either refusing to start, and the
rem address is never shown, so there is nothing a fixed port would make predictable.

if not exist "%APP%" (
    echo.
    echo Not built yet: %APP%
    echo Run this once without /nobuild.
    pause
    exit /b 1
)

rem Started detached, then this window closes. The app is a window in its own right and
rem owns the server it starts, so leaving a console behind it would be clutter that also
rem looks like the thing you are meant to keep open.
start "" "%APP%"
exit /b 0

rem ---- Browser --------------------------------------------------------------
:browser

if not exist "%WEB%" (
    echo.
    echo Not built yet: %WEB%
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

rem The server runs in the foreground of THIS window, so closing the window or
rem pressing Ctrl+C stops it. Nothing is left listening afterwards.
"%WEB%" --urls "http://localhost:%PORT%"
