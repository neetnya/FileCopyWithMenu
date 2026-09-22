@echo off
rem ============================================================
rem  FileCopyWithMenu launcher
rem  Runs dist\FileCopyWithMenu.exe; builds it first if missing.
rem ============================================================
setlocal
cd /d "%~dp0"

if exist "dist\FileCopyWithMenu.exe" goto run

echo dist\FileCopyWithMenu.exe not found, building ...
call "%~dp0build.bat"
if not exist "dist\FileCopyWithMenu.exe" (
  echo.
  echo [X] Cannot start: build failed.
  pause
  exit /b 1
)

:run
start "" "%~dp0dist\FileCopyWithMenu.exe"
exit /b 0
