# Gate 2 orchestration: capture one PC window, serve it as JPEG over loopback TCP, tunnel it to the Frame with `adb reverse`, launch the headset app.
# usage: powershell -File gate2.ps1 -Action start|stop|log   [-Process notepad] [-Title text] [-Device 10.0.0.171:5555]
# Prereq: Lepton Development running on the Frame, `adb connect <ip>:5555` works (use the IPv4 address, not the "frame" hostname).
param([string]$Action = "start", [string]$Process = "notepad", [string]$Title = "", [long]$Hwnd = 0, [string]$Device = "10.0.0.171:5555", [int]$Port = 5600)
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$adb = "C:\Users\fence\UnityAndroid\SDK\platform-tools\adb.exe"
$cap = Join-Path $root "Tools\XrssCapture\publish\XrssCapture.exe"
$str = Join-Path $root "Tools\XrssStream\bin\Release\net8.0-windows\XrssStream.exe"
$pkg = "com.gamebreaklabs.gate1passthrough"
$logs = Join-Path $PSScriptRoot "Logs"; New-Item -ItemType Directory -Force $logs | Out-Null
$pidFile = Join-Path $logs "gate2.pids"

function Stop-All {
    if (Test-Path $pidFile) { Get-Content $pidFile | ForEach-Object { try { Stop-Process -Id ([int]$_) -Force -ErrorAction Stop } catch {} }; Remove-Item $pidFile }
    & $adb -s $Device shell am force-stop $pkg 2>&1 | Out-Null
    & $adb -s $Device reverse --remove "tcp:$Port" 2>&1 | Out-Null
}

switch ($Action) {
  "stop" { Stop-All; "stopped" }
  "log"  {
      & $adb -s $Device logcat -d | Select-String "\[Gate[12]\]" | Select-Object -Last 40 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length)) }
      "--- PC sender"; if (Test-Path "$logs\xrssstream.out.log") { Get-Content "$logs\xrssstream.out.log" -Tail 12 }
  }
  default {
      Stop-All
      # capture helper: stdin must stay open (closing it makes the helper exit), so start it with a redirected stdin we never close
      $psi = New-Object System.Diagnostics.ProcessStartInfo $cap
      $filter = if ($Hwnd) { "--hwnd $Hwnd" } elseif ($Title) { "--title `"$Title`"" } else { "--process $Process" }
      $psi.Arguments = "--run --id gate2 $filter --fps 30"
      $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
      $c = [System.Diagnostics.Process]::Start($psi)
      $global:keepAlive = $c
      Start-Sleep 2
      $s = Start-Process $str -ArgumentList "--id gate2 --port $Port --fps 30 --maxw 1600 --quality 75" -PassThru -WindowStyle Hidden -RedirectStandardOutput "$logs\xrssstream.out.log"
      @($c.Id, $s.Id) | Set-Content $pidFile
      & $adb -s $Device reverse "tcp:$Port" "tcp:$Port"
      & $adb -s $Device logcat -c
      & $adb -s $Device shell am start -n "$pkg/com.unity3d.player.UnityPlayerGameActivity"
      "capture pid $($c.Id), sender pid $($s.Id). Leave this PowerShell window open; run -Action stop when done."
      Wait-Process -Id $s.Id
  }
}
