# PC host launcher for the Steam Frame client: starts XrssStream (many windows, one connection), tunnels it to the Frame with `adb reverse`, optionally installs and starts the app.
# usage: powershell -File host.ps1 [-Action start|stop|log] [-Install] [-Apk path] [-MaxW 2880] [-Fps 60] [-Device 10.35.78.1:5555]
# Prereq: Lepton Development running on the Frame; `adb connect 10.35.78.1:5555` (the Frame's Valve-dongle address: 1 ms ping; the router address and the "frame" hostname are worse or fail).
param([string]$Action = "start", [string]$Device = "10.35.78.1:5555", [int]$Port = 5600, [int]$MaxW = 2880, [int]$Fps = 60,
      [string]$Pkg = "com.gamebreaklabs.xrspatialscreens", [switch]$Install, [string]$Apk = "C:\Users\fence\Projects\XrSpatialScreens-headset\Builds\Headset\XrssApp.apk")
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$adb = "C:\Users\fence\UnityAndroid\SDK\platform-tools\adb.exe"
$host_ = Join-Path $root "Tools\XrssStream\bin\Release\net8.0-windows\XrssStream.exe"
$logs = Join-Path $PSScriptRoot "Logs"; New-Item -ItemType Directory -Force $logs | Out-Null
$log = Join-Path $logs "host.out.log"

function Stop-Host {
    Get-Process XrssStream, XrssCapture -ErrorAction SilentlyContinue | Stop-Process -Force
    & $adb -s $Device reverse --remove "tcp:$Port" 2>&1 | Out-Null
}

switch ($Action) {
  "stop" { Stop-Host; & $adb -s $Device shell am force-stop $Pkg 2>&1 | Out-Null; "stopped" }
  "log"  { Get-Content $log -Tail 20 }
  default {
      Stop-Host
      & $adb connect $Device | Out-Null
      if ($Install) { & $adb -s $Device shell am force-stop $Pkg | Out-Null; & $adb -s $Device install -r $Apk }
      $p = Start-Process $host_ -ArgumentList "--port $Port --fps $Fps --maxw $MaxW" -PassThru -WindowStyle Hidden -RedirectStandardOutput $log
      & $adb -s $Device reverse "tcp:$Port" "tcp:$Port"
      & $adb -s $Device logcat -c
      & $adb -s $Device shell am start -n "$Pkg/com.unity3d.player.UnityPlayerGameActivity"
      "host pid $($p.Id) on 127.0.0.1:$Port; log: $log"
  }
}
