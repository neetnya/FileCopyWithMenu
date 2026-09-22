@echo off
rem ============================================================
rem  FileCopyWithMenu - build script
rem  Output: dist\FileCopyWithMenu.exe  (needs .NET 8 Desktop Runtime)
rem ============================================================
setlocal
cd /d "%~dp0"

set "DOTNET=dotnet"
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"

"%DOTNET%" build FileCopyWithMenu.csproj -c Release -o dist -v minimal --nologo
if errorlevel 1 (
  echo.
  echo [X] BUILD FAILED
  pause
  exit /b 1
)

echo.
echo [OK] BUILD SUCCEEDED  --^>  dist\FileCopyWithMenu.exe
exit /b 0
