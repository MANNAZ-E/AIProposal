@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem ============================================================
rem Saga Web Controller - isolated test copy
rem
rem SOURCE_ROOT is the live project (for example, Claude Code).
rem ROOT is a disposable mirror used only by this controller.
rem Change PORT below if 5044 is already used by something else.
rem ============================================================

set "SOURCE_ROOT=C:\Emil\Offline - Mannaz\Code\Projects\1 ACTIVE\AIProposal"
set "ROOT=C:\Emil\Offline - Mannaz\Code\Projects\1 ACTIVE\AIProposal_WebControl"
set "WEB=%ROOT%\src\Saga.Web"
set "PORT=5044"
set "URL=http://localhost:%PORT%"
set "RUNNING=0"
set "ADOPTED=0"

title Saga Web Controller - Isolated Copy

rem ------------------------------------------------------------
rem Initial state:
rem If the isolated port is already listening, adopt that instance.
rem Otherwise sync/build the copy and start Saga from there.
rem ------------------------------------------------------------
call :IS_PORT_OPEN
if not errorlevel 1 (
    set "RUNNING=1"
    set "ADOPTED=1"
    echo.
    echo Existing isolated app detected on %URL%.
    echo Adopting it instead of starting a second instance.
) else (
    call :START_APP
)

goto MAIN


:MAIN
call :SHOW_STATUS
call :READ_KEY
set "KEY=!ERRORLEVEL!"

if "!KEY!"=="1" (
    rem ESC = Stop
    if "!RUNNING!"=="1" call :STOP_APP
    goto MAIN
)

if "!KEY!"=="2" (
    rem ENTER = Start when stopped
    if "!RUNNING!"=="0" call :START_APP
    goto MAIN
)

if "!KEY!"=="3" (
    rem R or BACKSPACE = Restart
    rem Restart also re-syncs the latest source and performs an incremental build.
    if "!RUNNING!"=="1" call :STOP_APP
    call :START_APP
    goto MAIN
)

if "!KEY!"=="4" (
    rem O or SPACE = Open browser when running
    if "!RUNNING!"=="1" (
        start "" "%URL%"
    ) else (
        echo.
        echo Saga is stopped. Press ENTER to start it.
    )
    goto MAIN
)

if "!KEY!"=="5" (
    rem B = Re-sync, full rebuild and restart
    call :REBUILD_AND_RESTART
    goto MAIN
)

goto MAIN


:SHOW_STATUS
echo.
echo ============================================================
if "!RUNNING!"=="1" (
    if "!ADOPTED!"=="1" (
        echo   SAGA TEST COPY: RUNNING  ^(existing instance adopted^)
    ) else (
        echo   SAGA TEST COPY: RUNNING
    )
    echo   %URL%
    echo.
    echo   ESC           = Stop
    echo   R / Backspace = Sync latest source, build ^& restart
    echo   B             = Sync latest source, FULL rebuild ^& restart
    echo   O / Space     = Open browser
) else (
    echo   SAGA TEST COPY: STOPPED
    echo.
    echo   ENTER         = Sync latest source, build ^& start
    echo   R / Backspace = Sync latest source, build ^& start
    echo   B             = Sync latest source, FULL rebuild ^& start
)
echo.
echo   Live source : %SOURCE_ROOT%
echo   Test copy   : %ROOT%
echo ============================================================
echo.
exit /b 0


:READ_KEY
powershell -NoLogo -NoProfile -Command ^
  "$k = [Console]::ReadKey($true).Key;" ^
  "switch ($k) {" ^
  "  'Escape'    { exit 1 }" ^
  "  'Enter'     { exit 2 }" ^
  "  'Backspace' { exit 3 }" ^
  "  'R'         { exit 3 }" ^
  "  'O'         { exit 4 }" ^
  "  'Spacebar'  { exit 4 }" ^
  "  'B'         { exit 5 }" ^
  "  default     { exit 99 }" ^
  "}"
exit /b !ERRORLEVEL!


:START_APP
rem Never launch another copy if the isolated port is already occupied.
call :IS_PORT_OPEN
if not errorlevel 1 (
    set "RUNNING=1"
    set "ADOPTED=1"
    echo.
    echo Existing app detected on %URL%.
    echo Adopting it instead of starting a second instance.
    exit /b 0
)

set "RUNNING=0"
set "ADOPTED=0"

call :SYNC_SOURCE
if errorlevel 1 exit /b 1

cd /d "%WEB%" || (
    echo.
    echo ERROR: Could not open copied web project:
    echo   %WEB%
    echo.
    exit /b 1
)

echo.
echo Building isolated copy...
echo.
dotnet build
if errorlevel 1 (
    echo.
    echo ERROR: Build failed. Saga was not started.
    exit /b 1
)

call :LAUNCH_APP
exit /b !ERRORLEVEL!


:LAUNCH_APP
call :IS_PORT_OPEN
if not errorlevel 1 (
    set "RUNNING=1"
    set "ADOPTED=1"
    echo.
    echo Port %PORT% became occupied before launch; adopting it.
    exit /b 0
)

echo.
echo Starting isolated Saga at %URL% ...
echo.

rem The explicit --urls application argument overrides the URL from
rem launchSettings.json, so the copied app does not collide with port 5033.
rem <nul prevents dotnet from stealing controller keystrokes.
start "" /b cmd /d /c "set Logging__LogLevel__Default=Warning&& set Logging__LogLevel__Microsoft=Warning&& set ASPNETCORE_URLS=%URL%&& dotnet run --launch-profile http --no-build -- --urls %URL% ^<nul"

call :WAIT_FOR_PORT_OPEN
if errorlevel 1 (
    set "RUNNING=0"
    set "ADOPTED=0"
    echo.
    echo ERROR: Saga did not open port %PORT%.
    echo Check the dotnet output above for the startup error.
    exit /b 1
)

set "RUNNING=1"
set "ADOPTED=0"
start "" "%URL%"

rem Let the final ASP.NET startup log lines print before showing controls.
timeout /t 1 /nobreak >nul

echo.
echo Saga test copy started successfully.
exit /b 0


:REBUILD_AND_RESTART
echo.
echo ============================================================
echo   RE-SYNCING AND REBUILDING SAGA TEST COPY
echo ============================================================
echo.

rem Stop the isolated instance first so copied build files are not locked.
if "!RUNNING!"=="1" (
    call :STOP_APP
)

call :SYNC_SOURCE
if errorlevel 1 exit /b 1

cd /d "%WEB%" || (
    echo.
    echo ERROR: Could not open copied web project:
    echo   %WEB%
    echo.
    exit /b 1
)

echo.
echo Rebuilding isolated Saga copy...
echo.

dotnet build -t:Rebuild

if errorlevel 1 (
    set "RUNNING=0"
    set "ADOPTED=0"
    echo.
    echo ============================================================
    echo   REBUILD FAILED
    echo ============================================================
    echo.
    echo Saga was NOT restarted.
    echo Fix the build errors above, then press B to try again.
    echo.
    exit /b 1
)

echo.
echo ============================================================
echo   REBUILD SUCCESSFUL
echo ============================================================
echo.
echo Starting isolated Saga copy...
echo.

call :LAUNCH_APP
exit /b !ERRORLEVEL!


:SYNC_SOURCE
echo.
echo Syncing live source to isolated test copy...
echo   FROM: %SOURCE_ROOT%
echo   TO:   %ROOT%
echo.

if not exist "%SOURCE_ROOT%\src\Saga.Web" (
    echo ERROR: Source web project was not found:
    echo   %SOURCE_ROOT%\src\Saga.Web
    echo.
    exit /b 1
)

if not exist "%ROOT%" mkdir "%ROOT%" >nul 2>&1

rem /MIR keeps the disposable test tree aligned with the live source.
rem Build outputs, IDE state, VCS metadata, packages and common caches are
rem excluded because they are not needed as source and can be regenerated.
robocopy "%SOURCE_ROOT%" "%ROOT%" /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP ^
  /XD ".git" ".vs" ".idea" ".claude" "bin" "obj" "node_modules" "TestResults" ^
  /XF "*.user" "*.suo" >nul

set "ROBOCOPY_RC=!ERRORLEVEL!"
if !ROBOCOPY_RC! GEQ 8 (
    echo ERROR: Robocopy failed with exit code !ROBOCOPY_RC!.
    echo The test copy was not started.
    echo.
    exit /b 1
)

echo Sync complete.
exit /b 0


:STOP_APP
echo.
echo Stopping isolated Saga...

rem Stop the process (or processes) actually listening on the isolated port.
powershell -NoLogo -NoProfile -Command ^
  "$ids = @(Get-NetTCPConnection -LocalPort %PORT% -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique);" ^
  "foreach ($processId in $ids) {" ^
  "  Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue" ^
  "}" >nul 2>&1

call :WAIT_FOR_PORT_CLOSED

set "RUNNING=0"
set "ADOPTED=0"

echo Saga test copy stopped.
exit /b 0


:IS_PORT_OPEN
rem Exit 0 when something is listening on the isolated port.
rem Exit 1 when the port is free.
powershell -NoLogo -NoProfile -Command ^
  "$c = Get-NetTCPConnection -LocalPort %PORT% -State Listen -ErrorAction SilentlyContinue;" ^
  "if ($null -ne $c) { exit 0 } else { exit 1 }" >nul 2>&1
exit /b !ERRORLEVEL!


:WAIT_FOR_PORT_OPEN
set /a "TRIES=0"

:WAIT_FOR_PORT_OPEN_LOOP
call :IS_PORT_OPEN
if not errorlevel 1 exit /b 0

set /a "TRIES+=1"
if !TRIES! GEQ 60 exit /b 1

timeout /t 1 /nobreak >nul
goto WAIT_FOR_PORT_OPEN_LOOP


:WAIT_FOR_PORT_CLOSED
set /a "TRIES=0"

:WAIT_FOR_PORT_CLOSED_LOOP
call :IS_PORT_OPEN
if errorlevel 1 exit /b 0

set /a "TRIES+=1"
if !TRIES! GEQ 15 (
    echo WARNING: Port %PORT% is still open.
    exit /b 1
)

timeout /t 1 /nobreak >nul
goto WAIT_FOR_PORT_CLOSED_LOOP
