@echo off
setlocal
cd /d "%~dp0"
title Building v9 MultiRLBX v1.0.0

set "CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find the .NET Framework 4 compiler on this PC.
  echo It is part of Windows 10 and 11. Please run Windows Update and try again.
  pause
  exit /b 1
)

echo Compiling v9 MultiRLBX v1.0.0 - this takes a few seconds...
"%CSC%" /nologo /target:winexe /optimize+ /debug- /warn:1 /nowarn:0168,0219,0414,0649,0169,0067 ^
  /out:"v9 MultiRLBX.exe" ^
  /win32icon:app.ico ^
  /win32manifest:app.manifest ^
  /resource:ui\app.html,MultiRLBX.app.html ^
  /r:System.dll ^
  /r:System.Core.dll ^
  /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll ^
  /r:System.Web.Extensions.dll ^
  /r:System.Net.Http.dll ^
  /r:System.Security.dll ^
  /r:System.Management.dll ^
  /recurse:src\*.cs

if errorlevel 1 (
  echo.
  echo ============================ BUILD FAILED ============================
  echo Please copy ALL the text above and send it back, so it can be fixed.
  pause
  exit /b 1
)

echo.
echo Build OK  -  v9 MultiRLBX.exe (v1.0.0) is ready.
exit /b 0
