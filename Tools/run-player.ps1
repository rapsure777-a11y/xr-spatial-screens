# Builds nothing; runs the built player in flat-window mode and waits. Usage: powershell -File Tools\run-player.ps1 -Shot Screenshots\a.png [-Args "--xrss-pattern --xrss-quickscreen"]
param([string]$Shot = "", [string]$ExtraArgs = "--xrss-pattern --xrss-quickscreen", [double]$Delay = 5, [int]$Width = 1600, [int]$Height = 900, [int]$TimeoutSec = 120)
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "Builds\Windows\XrSpatialScreens.exe"
$log = Join-Path $root "Logs\player.log"
$args = @('--xrss-desktop') + ($ExtraArgs -split ' ' | Where-Object { $_ }) + @('-screen-width', $Width, '-screen-height', $Height, '-screen-fullscreen', '0', '-logFile', "`"$log`"")
if ($Shot) { $full = Join-Path $root $Shot; $args += @('--xrss-shot', "`"$full`"", '--xrss-shot-delay', $Delay) }
$p = Start-Process $exe -ArgumentList $args -PassThru -WorkingDirectory (Split-Path $exe)
if (-not $p.WaitForExit($TimeoutSec * 1000)) { $p.Kill(); "player timeout" }
if ($Shot) { "shot: " + (Test-Path (Join-Path $root $Shot)) }
Select-String $log -Pattern "\[XrSpatial\]|Exception|NullReference|error CS" | Select-Object -First 12 | ForEach-Object { $_.Line }
