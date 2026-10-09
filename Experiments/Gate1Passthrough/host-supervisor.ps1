# Keeps the PC host running: starts XrssStream and starts it again a moment after it exits for any reason (a crash, a kill). The headset reconnects by itself and its
# screens reopen their windows, so a host crash costs a few seconds instead of a frozen session. Stopped by host.ps1 -Action stop.
# It also keeps the adb link to the Frame alive: if the adb server restarted (the device list empties and the `adb reverse` tunnel is lost) it reconnects and
# re-creates the tunnel within a few seconds, so the headset app finds the host again without anyone touching a command line.
param([int]$Port = 5600, [int]$MaxW = 2880, [int]$Fps = 60, [string]$Exe, [string]$Log,
      [string]$Device = "10.35.78.1:5555", [string]$Adb = "C:\Users\fence\UnityAndroid\SDK\platform-tools\adb.exe")
$restarts = [IO.Path]::ChangeExtension($Log, ".restarts.log")
$linkLog = [IO.Path]::ChangeExtension($Log, ".link.log")
function Keep-Link {
    try {
        $devs = & $Adb devices 2>&1 | Out-String
        if ($devs -notmatch [regex]::Escape($Device) + "\s+device") {
            & $Adb connect $Device 2>&1 | Out-Null
            Add-Content $linkLog ("{0}  device not listed; reconnected" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))
        }
        $rev = & $Adb -s $Device reverse --list 2>&1 | Out-String
        if ($rev -notmatch "tcp:$Port") {
            & $Adb -s $Device reverse "tcp:$Port" "tcp:$Port" 2>&1 | Out-Null
            Add-Content $linkLog ("{0}  tunnel tcp:{1} was missing; restored" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Port)
        }
    } catch { }
}
$run = 0
while ($true) {
    $run++
    if (Test-Path $Log) { Copy-Item $Log ([IO.Path]::ChangeExtension($Log, ".prev.log")) -Force -ErrorAction SilentlyContinue }
    $p = Start-Process $Exe -ArgumentList "--port $Port --fps $Fps --maxw $MaxW" -PassThru -WindowStyle Hidden -RedirectStandardOutput $Log
    while (-not $p.WaitForExit(5000)) { Keep-Link }
    Add-Content $restarts ("{0}  host run {1} exited with code {2}; restarting" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $run, $p.ExitCode)
    Start-Sleep -Seconds 1
}
