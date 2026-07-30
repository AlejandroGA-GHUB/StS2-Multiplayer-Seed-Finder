@echo off
rem ---------------------------------------------------------------------------
rem  Lets you type the commands this project prints.
rem
rem  Everything the command line does is one executable with one name:
rem
rem      sts2seed --doctor                 is anything broken?
rem      sts2seed --refresh                rewrite the data tables from your game
rem      sts2seed --show <Type.Method>     read a game method beside ours
rem      sts2seed --verify-history         check against runs you have played
rem      sts2seed --relic silken_tress ... search for seeds
rem      sts2seed --help                   all of it
rem
rem  This is NOT the file to double-click. It exists so that, in a terminal
rem  already open in this folder, `sts2seed --doctor` works as written instead
rem  of having to be typed as
rem  `dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --doctor`.
rem
rem  If you do not already have a terminal here, double-click cli.bat instead.
rem ---------------------------------------------------------------------------
setlocal EnableExtensions

rem No arguments means nobody asked this to do anything: either it was double-clicked,
rem or somebody typed the name and forgot the command. Both want the same answer, and
rem both need the window to stay open long enough to read it.
rem
rem Arguments are the discriminator rather than %cmdcmdline%, because PowerShell also
rem launches a .bat through `cmd /c "...bat"` - so the command line looks identical
rem whether you double-clicked it or typed `.\sts2seed --doctor`.
if "%~1"=="" (
    echo.
    echo   Nothing has happened, because no command was given.
    echo.
    echo   This file is not meant to be double-clicked. It exists so that, in a
    echo   terminal already open in this folder, you can type commands the way this
    echo   project writes them:
    echo.
    echo       sts2seed --help              everything it can do
    echo       sts2seed --doctor            is anything broken after a game update
    echo.
    echo   If you want that terminal, double-click  cli.bat  instead. It opens one
    echo   here with everything ready to go.
    echo.
    echo   The two you normally want:
    echo       seed-finder.bat   the seed finder itself, in your browser
    echo       repair.bat        check and fix after a game update
    echo.
    pause
    exit /b 0
)

set "EXE=%~dp0src\Sts2.SeedFinder.Cli\bin\Release\net10.0\sts2seed.exe"

if not exist "%EXE%" (
    echo Not built yet. Building once...
    dotnet build "%~dp0src\Sts2.SeedFinder.Cli" -c Release --nologo -v quiet
    if errorlevel 1 (
        echo.
        echo The build failed - see the errors above.
        exit /b 1
    )
)

"%EXE%" %*
