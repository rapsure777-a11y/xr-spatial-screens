# Keeps the PC host running: starts XrssStream and starts it again a moment after it exits for any reason (a crash, a kill). The headset reconnects by itself and its
# screens reopen their windows, so a host crash costs a few seconds instead of a frozen session. Stopped by host.ps1 -Action stop.
param([int]$Port = 5600, [int]$MaxW = 2880, [int]$Fps = 60, [string]$Exe, [string]$Log)
$restarts = [IO.Path]::ChangeExtension($Log, ".restarts.log")
$run = 0
while ($true) {
    $run++
    if (Test-Path $Log) { Copy-Item $Log ([IO.Path]::ChangeExtension($Log, ".prev.log")) -Force -ErrorAction SilentlyContinue }
    $p = Start-Process $Exe -ArgumentList "--port $Port --fps $Fps --maxw $MaxW" -PassThru -WindowStyle Hidden -RedirectStandardOutput $Log
    $p.WaitForExit()
    Add-Content $restarts ("{0}  host run {1} exited with code {2}; restarting" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $run, $p.ExitCode)
    Start-Sleep -Seconds 1
}
