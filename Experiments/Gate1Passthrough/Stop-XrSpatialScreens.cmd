@echo off
rem Stops the PC host and closes the app on the Frame.
title XR Spatial Screens - stop
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0host.ps1" -Action stop
timeout /t 4 >nul
