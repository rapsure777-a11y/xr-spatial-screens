@echo off
rem One click: start the PC host (supervised), connect to the Steam Frame, restore the tunnel and open the app. Wake the Frame and start Lepton Development first.
title XR Spatial Screens - start
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0host.ps1" -Action start
echo.
echo Host is running in the background. Put the headset on.
timeout /t 6 >nul
