@echo off
rem ---------------------------------------------------------------------------
rem  StS2 Co-op Seed Finder - check and repair after a game patch.
rem
rem  Double-click this if the app shows a banner saying your game has changed.
rem  It checks whether predictions are still correct, and offers to fix what can
rem  be fixed without editing code.
rem
rem  Everything it does is also available as commands - see the bottom of
rem  `sts2seed --help`, and docs\PATCH_RECOVERY.md for the full runbook.
rem ---------------------------------------------------------------------------
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
title StS2 Seed Finder - repair

rem MSBuild leaves its worker processes running for 15 minutes after a build so the next
rem one starts faster. Right for a developer building all day, wrong for a launcher: a
rem user who closes this window would otherwise still have three dotnet.exe running and
rem no way to tell whose they are. A variable rather than a build switch, so anything
rem this script calls that builds in turn inherits it.
set "MSBUILDDISABLENODEREUSE=1"

set "CLI=call "%~dp0sts2seed.bat""

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

echo Building...
dotnet build -c Release --nologo -v quiet
if errorlevel 1 (
    echo.
    echo The build failed. That is not something this script can fix - the errors
    echo above say what is wrong.
    echo.
    pause
    exit /b 1
)

echo.
%CLI% --doctor
rem --doctor exits 0 when nothing is broken, 2 when something is.
if not errorlevel 1 goto :verify

echo.
echo ---------------------------------------------------------------------------
set "ANSWER="
set /p ANSWER=Regenerate the data tables from your game? [y/N] 
if /i not "%ANSWER%"=="y" goto :done

echo.
%CLI% --refresh
if errorlevel 3 (
    rem Exit code 3 is a structural change: the generator wrote nothing on purpose.
    echo.
    echo Nothing was written. This one needs a code change - see the message above
    echo and docs\PATCH_RECOVERY.md.
    echo.
    pause
    exit /b 3
)

echo.
echo Rebuilding with the new tables...
dotnet build -c Release --nologo -v quiet
if errorlevel 1 (
    echo.
    echo The rebuild failed. Read the errors above before assuming the tables are at
    echo fault - a file still open in another program will do this too.
    echo.
    echo If the errors are in the regenerated files themselves, this patch needs a
    echo code change: see docs\PATCH_RECOVERY.md.
    echo.
    pause
    exit /b 1
)

echo.
%CLI% --doctor
if errorlevel 1 (
    echo.
    echo Some of it still needs a code edit. The lines above name which methods and
    echo which files; docs\PATCH_RECOVERY.md walks through it.
    echo.
    pause
    exit /b 2
)

:verify
rem Passing the checks above is necessary but not sufficient: they cannot see
rem content that was ADDED, and the assembled draw chain has no headless test.
rem Only a real run settles it, which is why this is not optional before
rem recording the build as verified.
echo.
echo ---------------------------------------------------------------------------
echo Checking against runs you have actually played...
echo.
%CLI% --verify-history
if errorlevel 2 (
    echo.
    echo A run played on your CURRENT game build did not match, which is the case
    echo worth taking seriously. Read the notes above first: a partner's unlock
    echo state and mods both explain failures that are not this tool being wrong.
    echo.
    pause
    exit /b 2
)
rem Runs from older builds may still be listed as not matching. That is expected and
rem does not gate anything: their lobby unlock state and content pools are both
rem unrecoverable, so they cannot answer whether this checkout predicts your game.

rem The draw-order baseline is a record of what the game's code looked like when this
rem checkout was last known good. Offering to move it is only a real decision when it no
rem longer matches, and only safe once a run has confirmed our predictions, which is the
rem case we are in here. Asked rather than done silently: a change a run does not exercise
rem would otherwise have its only warning erased.
%CLI% --snapshot --check >nul 2>&1
if errorlevel 2 (
    echo.
    echo The game's draw-order code has changed since this checkout was last
    echo baselined, but your runs still match it.
    set "ANSWER="
    set /p ANSWER=Record the new shape as the baseline? Saying no just means you get told again. [y/N] 
    if /i "!ANSWER!"=="y" %CLI% --snapshot
)

echo.
set "ANSWER="
set /p ANSWER=Record your game version as verified? [y/N] 
if /i "%ANSWER%"=="y" (
    rem --verify-history passed just above, so accept need not ask for it again.
    %CLI% --accept --run-verified
) else (
    echo Left as is. The banner will keep showing until you record it.
)

:done
echo.
pause
