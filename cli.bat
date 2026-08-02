@echo off
rem ---------------------------------------------------------------------------
rem  Opens a command line ready to use, so you do not have to get a terminal
rem  into this folder yourself.
rem
rem  Double-click it. You get a normal prompt where `sts2seed` already works.
rem
rem  It uses cmd rather than PowerShell on purpose: cmd will run a command from
rem  the current folder, PowerShell refuses to unless you prefix it with `.\`,
rem  and that distinction is not worth making anyone learn.
rem
rem  Nothing is installed and nothing is left behind. The PATH entry below lives
rem  only inside the window this opens, and goes when you close it.
rem ---------------------------------------------------------------------------
setlocal EnableExtensions
cd /d "%~dp0"
title StS2 Co-op Seed Finder - command line

rem MSBuild leaves its worker processes running for 15 minutes after a build so the next
rem one starts faster. Right for a developer building all day, wrong for a launcher: a
rem user who closes this window would otherwise still have three dotnet.exe running and
rem no way to tell whose they are. The prompt this opens inherits it, so builds typed in
rem there behave the same way.
set "MSBUILDDISABLENODEREUSE=1"

set "OUT=%~dp0src\Sts2.SeedFinder.Cli\bin\Release\net10.0"

if not exist "%OUT%\sts2seed.exe" (
    echo Building once, this takes a moment...
    dotnet build "%~dp0src\Sts2.SeedFinder.Cli" -c Release --nologo -v quiet
    if errorlevel 1 (
        echo.
        echo The build failed - see the errors above.
        echo You need the .NET 10 SDK: https://dotnet.microsoft.com/download
        echo.
        pause
        exit /b 1
    )
)

rem Point at the build output rather than the shim, so `sts2seed` here is the
rem executable itself.
set "PATH=%OUT%;%PATH%"

cls
echo.
echo   StS2 Co-op Seed Finder - command line
echo   =====================================
echo.
echo   Try:
echo       sts2seed --help              everything it can do
echo       sts2seed --list              relics, cards, bosses, events you can search for
echo       sts2seed --explain ^<SEED^>     break down one seed
echo.
echo   After a game update:
echo       sts2seed --doctor            is anything broken, and what fixes it
echo       sts2seed --verify-history    check against runs you have played
echo.
echo   Searching is the same command, e.g.
echo       sts2seed --relic silken_tress --players 2 --require all
echo.
echo   Docs: README.md, and docs\PATCH_RECOVERY.md after a game update.
echo   Close this window when you are done.
echo.

cmd /k
