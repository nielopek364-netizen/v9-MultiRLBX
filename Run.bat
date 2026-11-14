@echo off
setlocal
cd /d "%~dp0"
if not exist "v9 MultiRLBX.exe" (
  call "%~dp0Build.bat"
  if errorlevel 1 exit /b 1
)
start "" "%~dp0v9 MultiRLBX.exe" %*
exit /b 0
