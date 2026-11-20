@echo off
rem ==========================================================================================================
rem dump-live-database.bat - run ON THE SERVER to make a full, consistent dump of the LIVE e-voting database.
rem   * --single-transaction: a consistent copy WITHOUT locking the tables (safe while the system is in use)
rem   * includes routines, triggers and events
rem   * the file holds ALL member and vote data: keep it on the server or an encrypted drive, never in chat/email
rem ==========================================================================================================
setlocal
set "MYSQLDUMP=mysqldump"
where mysqldump >nul 2>&1 || (
  for %%P in ("C:\Program Files\MySQL\MySQL Server 8.0\bin" "C:\Program Files\MySQL\MySQL Server 8.4\bin" "C:\Program Files\MySQL\MySQL Server 9.0\bin") do (
    if exist "%%~P\mysqldump.exe" set "MYSQLDUMP=%%~P\mysqldump.exe"
  )
)
set /p DB=Database name [rrms]: 
if "%DB%"=="" set DB=rrms
set /p DBUSER=MySQL user [root]: 
if "%DBUSER%"=="" set DBUSER=root
for /f %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmm"') do set STAMP=%%T
set "OUT=%~dp0%DB%-live-%STAMP%.sql"
echo.
echo Dumping %DB% to %OUT%
echo (MySQL will ask for the password of %DBUSER%)
"%MYSQLDUMP%" -u %DBUSER% -p --single-transaction --routines --triggers --events --hex-blob --default-character-set=utf8mb4 --databases %DB% > "%OUT%"
if errorlevel 1 (
  echo.
  echo DUMP FAILED - check the password / database name. The file may be incomplete: delete it.
  pause
  exit /b 1
)
for %%F in ("%OUT%") do echo Done: %%~zF bytes
pause
