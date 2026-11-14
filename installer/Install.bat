@echo off
title Install v9 MultiRLBX v1.0.0
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 pause
