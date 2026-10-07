# Live input-forwarding scenarios. Each one starts the ClickTest window (which logs every mouse event it REALLY receives), runs the player in flat mode with a scripted laser
# (Assets/Scripts/App/SelfTest.cs) and checks the window's own log. Real SendInput, real windows; nothing is mocked.
# Usage: powershell -File Tools\test-input.ps1 [-Only drag,loss]
# Not covered here: refusing a press when Windows will not raise the target above a topmost window (this PC has an always-above fullscreen browser window that contaminates the setup).
param([string]$Only = "", [int]$Tol = 2)
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root "Tools\ClickTest\bin\Release\net8.0-windows\ClickTest.exe"
$playerLog = Join-Path $root "Logs\player.log"
$results = New-Object System.Collections.ArrayList

function Parse-Events($path, $kind) {
    $rx = [regex]"^$kind client (\d+),(\d+) screen (-?\d+),(-?\d+)"
    $out = @()
    foreach ($line in Get-Content $path) { $m = $rx.Match($line); if ($m.Success) { $out += [pscustomobject]@{ X = [int]$m.Groups[3].Value; Y = [int]$m.Groups[4].Value } } }
    return ,$out
}
function Near($a, $bx, $by) { [Math]::Max([Math]::Abs($a.X - $bx), [Math]::Abs($a.Y - $by)) }

function Scenario($name, $point, $extra, [scriptblock]$check, [switch]$Cover) {
    if ($Only -ne "" -and (($Only -split ",") -notcontains $name)) { return }
    $log = Join-Path $env:TEMP ("xrss-in-" + [guid]::NewGuid().ToString("N") + ".txt")
    $log2 = Join-Path $env:TEMP ("xrss-cover-" + [guid]::NewGuid().ToString("N") + ".txt")
    $procs = @(Start-Process $fixture -ArgumentList "300 300 600 400 `"$log`" `"XRSS ClickTest`"" -PassThru)
    if ($Cover) { Start-Sleep -Seconds 1; $procs += Start-Process $fixture -ArgumentList "250 250 750 500 `"$log2`" Blocker topmost" -PassThru }
    Start-Sleep -Seconds 2
    & powershell -File (Join-Path $root "Tools\run-player.ps1") -ExtraArgs "--xrss-title ClickTest --xrss-ephemeral --xrss-selftest-click $point $extra" -TimeoutSec 120 | Out-Null
    Start-Sleep -Milliseconds 300
    $pl = Get-Content $playerLog
    foreach ($p in $procs) { try { if (-not $p.HasExited) { $p.Kill() } } catch { } }
    $exp = $pl | Select-String "expected desktop pixel (-?\d+),(-?\d+)" | Select-Object -First 1
    $drag = $pl | Select-String "expected drag end pixel (-?\d+),(-?\d+)" | Select-Object -First 1
    $ctx = @{ Log = $log; Log2 = $log2; Player = $pl; Exp = $null; DragEnd = $null }
    if ($exp) { $ctx.Exp = [pscustomobject]@{ X = [int]$exp.Matches[0].Groups[1].Value; Y = [int]$exp.Matches[0].Groups[2].Value } }
    if ($drag) { $ctx.DragEnd = [pscustomobject]@{ X = [int]$drag.Matches[0].Groups[1].Value; Y = [int]$drag.Matches[0].Groups[2].Value } }
    try { $msg = & $check $ctx } catch { $msg = "FAIL check error: $_" }
    if (-not $msg) { $msg = "FAIL no result" }
    [void]$results.Add("$name : $msg")
    "$name : $msg"
}

$pixel = { param($c) $p = Parse-Events $c.Log "press"; if (-not $c.Exp -or $p.Count -lt 1) { return "FAIL expected=$([bool]$c.Exp) presses=$($p.Count)" }
    $d = Near $p[0] $c.Exp.X $c.Exp.Y; if ($d -le $Tol) { "PASS expected $($c.Exp.X),$($c.Exp.Y) got $($p[0].X),$($p[0].Y) (off by $d px)" } else { "FAIL expected $($c.Exp.X),$($c.Exp.Y) got $($p[0].X),$($p[0].Y) (off by $d px)" } }

Scenario "click-full" "0.25,0.75" "" $pixel
Scenario "click-crop" "0.75,0.7" "--xrss-selftest-crop 0.5,0.4,0.5,0.6" $pixel
Scenario "click-crop-tilt" "0.9,0.9" "--xrss-selftest-crop 0.5,0.4,0.5,0.6 --xrss-selftest-tilt 40" $pixel
Scenario "right-wheel" "0.5,0.5" "--xrss-selftest-right --xrss-selftest-wheel" {
    param($c) $all = Get-Content $c.Log -Raw
    if ($all -match "press .* button Right" -and $all -match "wheel (\d+)") { "PASS right click and wheel received" } else { "FAIL right=$($all -match 'button Right') wheel=$($all -match 'wheel')" } }
Scenario "double-click" "0.4,0.5" "--xrss-selftest-double" {
    param($c) $all = Get-Content $c.Log -Raw; $n = ([regex]::Matches($all, "(?m)^press ")).Count
    if ($all -match "(?m)^double " -and $n -ge 2) { "PASS Windows saw a double click ($n presses, second 3 px away)" } else { "FAIL presses=$n double=$($all -match '(?m)^double ')" } }
Scenario "drag" "0.2,0.3" "--xrss-selftest-drag 0.7,0.8" {
    param($c) $p = Parse-Events $c.Log "press"; $u = Parse-Events $c.Log "up"; $m = (Parse-Events $c.Log "move-held").Count
    if ($p.Count -lt 1 -or $u.Count -lt 1 -or -not $c.Exp -or -not $c.DragEnd) { return "FAIL press=$($p.Count) up=$($u.Count)" }
    $d1 = Near $p[0] $c.Exp.X $c.Exp.Y; $d2 = Near $u[0] $c.DragEnd.X $c.DragEnd.Y
    if ($d1 -le $Tol -and $d2 -le $Tol + 1 -and $m -ge 3) { "PASS pressed at start (off $d1), released at end (off $d2), $m moves while held" } else { "FAIL start off $d1, end off $d2, moves $m" } }
Scenario "drag-leaves-panel" "0.5,0.5" "--xrss-selftest-drag 1.6,0.5" {
    param($c) $u = Parse-Events $c.Log "up"
    if ($u.Count -lt 1 -or -not $c.DragEnd) { return "FAIL up=$($u.Count)" }
    $d = Near $u[0] $c.DragEnd.X $c.DragEnd.Y
    if ($d -le $Tol + 1) { "PASS laser left the panel; the drag stayed on the window and ended at its edge ($($u[0].X),$($u[0].Y), off $d)" } else { "FAIL end off $d" } }
Scenario "tracking-lost-while-held" "0.5,0.5" "--xrss-selftest-loss" {
    param($c) $p = (Parse-Events $c.Log "press").Count; $u = (Parse-Events $c.Log "up").Count
    if ($p -eq 1 -and $u -eq 1) { "PASS the held button was released when tracking was lost" } else { "FAIL presses=$p releases=$u" } }
Scenario "window-moved" "0.5,0.5" "--xrss-selftest-move 150,80" $pixel
Scenario "window-resized" "0.8,0.7" "--xrss-selftest-resize 800,550" $pixel
Scenario "window-closed" "0.5,0.5" "--xrss-selftest-close" {
    param($c) $p = (Parse-Events $c.Log "press").Count; $r = $c.Player | Select-String "refused: (\d+)" | Select-Object -First 1
    if ($p -eq 0 -and $r -and $r.Matches[0].Groups[1].Value -ne "0") { "PASS nothing was sent to a window that no longer exists (press dropped, message shown)" } else { "FAIL presses=$p refused=$($r.Line)" } }

if ($results.Count -eq 0) { "NO SCENARIO RAN" } elseif (($results | Where-Object { $_ -match ": FAIL" }).Count -gt 0) { "SOME FAILED" } else { "ALL PASS" }
