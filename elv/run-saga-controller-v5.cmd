@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=C:\Emil\Offline - Mannaz\Code\Projects\1 ACTIVE\AIProposal"
set "WEB=%ROOT%\src\Saga.Web"
set "PORT=5033"
set "URL=http://localhost:%PORT%"
set "RUNNING=0"
set "ADOPTED=0"

cd /d "%WEB%" || (
    echo.
    echo ERROR: Could not open:
    echo   %WEB%
    echo.
    pause
    exit /b 1
)

title Saga Web Controller

rem ------------------------------------------------------------
rem Initial state:
rem If port 5033 is already listening, adopt that instance.
rem Otherwise start Saga.
rem ------------------------------------------------------------
call :IS_PORT_OPEN
if not errorlevel 1 (
    set "RUNNING=1"
    set "ADOPTED=1"
    echo.
    echo Existing app detected on %URL%.
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
    rem B = Rebuild and restart
    call :REBUILD_AND_RESTART
    goto MAIN
)

goto MAIN


:SHOW_STATUS
echo.
echo ============================================================
if "!RUNNING!"=="1" (
    if "!ADOPTED!"=="1" (
        echo   SAGA: RUNNING  ^(existing instance adopted^)
    ) else (
        echo   SAGA: RUNNING
    )
    echo   %URL%
    echo.
    echo   ESC           = Stop
    echo   R / Backspace = Restart
    echo   B             = Rebuild ^& Restart
    echo   O / Space     = Open browser
) else (
    echo   SAGA: STOPPED
    echo.
    echo   ENTER         = Start
    echo   R / Backspace = Start
    echo   B             = Rebuild ^& Start
)
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
rem Never launch another copy if the port is already occupied.
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

echo.
echo Starting Saga at %URL% ...
echo.

rem Run dotnet in the background in this same console.
rem <nul prevents dotnet from stealing controller keystrokes.
start "" /b cmd /d /c "set Logging__LogLevel__Default=Warning&& set Logging__LogLevel__Microsoft=Warning&& dotnet run --launch-profile http --no-build ^<nul"

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
echo Saga started successfully.
exit /b 0


:REBUILD_AND_RESTART
echo.
echo ============================================================
echo   REBUILDING SAGA
echo ============================================================
echo.

rem Stop the current instance first so files are not locked.
if "!RUNNING!"=="1" (
    call :STOP_APP
)

echo.
echo Rebuilding Saga...
echo.

rem Perform a full rebuild.
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
echo Starting Saga...
echo.

call :START_APP
exit /b !ERRORLEVEL!


:STOP_APP
echo.
echo Stopping Saga...

rem Stop the process (or processes) that are actually listening on port 5033.
powershell -NoLogo -NoProfile -Command ^
  "$ids = @(Get-NetTCPConnection -LocalPort %PORT% -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique);" ^
  "foreach ($processId in $ids) {" ^
  "  Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue" ^
  "}" >nul 2>&1

call :WAIT_FOR_PORT_CLOSED

set "RUNNING=0"
set "ADOPTED=0"

echo Saga stopped.
exit /b 0


:IS_PORT_OPEN
rem Exit 0 when something is listening on port 5033.
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