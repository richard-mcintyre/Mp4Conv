@echo off
setlocal

REM Set paths
set DB_DIR=p:\Mp4Conv
set BACKUP_DIR=%TEMP%\Mp4Conv_backup

REM Create backup directory if it doesn't exist
if not exist "%BACKUP_DIR%" mkdir "%BACKUP_DIR%"

REM Backup database files
copy "%DB_DIR%\conv.db" "%BACKUP_DIR%\" /Y >nul 2>&1
copy "%DB_DIR%\conv.db-shm" "%BACKUP_DIR%\" /Y >nul 2>&1
copy "%DB_DIR%\conv.db-wal" "%BACKUP_DIR%\" /Y >nul 2>&1

echo Bringing app offline...
echo. > p:\Mp4Conv\app_offline.htm
timeout /t 3 /nobreak > nul

echo Publishing...
dotnet publish src\Mp4Conv.Web\Mp4Conv.Web.csproj -c Release -o p:\Mp4Conv

REM Restore database files after publish
copy "%BACKUP_DIR%\conv.db" "%DB_DIR%\" /Y >nul 2>&1
copy "%BACKUP_DIR%\conv.db-shm" "%DB_DIR%\" /Y >nul 2>&1
copy "%BACKUP_DIR%\conv.db-wal" "%DB_DIR%\" /Y >nul 2>&1

REM clean up backup
rmdir /S /Q "%BACKUP_DIR%"

echo Bringing app back online...
del p:\Mp4Conv\app_offline.htm

echo Done.
endlocal