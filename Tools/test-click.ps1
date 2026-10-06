# Live click-forwarding test: starts the ClickTest window, runs the player in flat mode with a scripted laser click at a source point, and compares the desktop pixel the
# player computed with the pixel the window actually received. Usage: powershell -File Tools\test-click.ps1 [-Points "0.25,0.75","0.9,0.1"] [-X 300 -Y 300 -W 600 -H 400]
param([string[]]$Points = @("0.25,0.75", "0.5,0.5", "0.95,0.2", "0.05,0.95"), [int]$X = 300, [int]$Y = 300, [int]$W = 600, [int]$H = 400, [int]$Tolerance = 2, [string]$Extra = "")
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root "Tools\ClickTest\bin\Release\net8.0-windows\ClickTest.exe"
$ok = $true
foreach ($pt in $Points) {
    $log = Join-Path $env:TEMP ("xrss-click-" + [guid]::NewGuid().ToString("N") + ".txt")
    $win = Start-Process $fixture -ArgumentList "$X $Y $W $H `"$log`"" -PassThru
    Start-Sleep -Seconds 2
    & powershell -File (Join-Path $root "Tools\run-player.ps1") -ExtraArgs "--xrss-title ClickTest --xrss-ephemeral --xrss-selftest-click $pt $Extra" -TimeoutSec 90 | Out-Null
    $out = Get-Content (Join-Path $root "Logs\player.log")
    try { $win.Kill() } catch { }
    $exp = $out | Select-String "expected desktop pixel (\d+),(\d+)" | Select-Object -First 1
    $got = Get-Content $log | Select-String "screen (\d+),(\d+)" | Select-Object -First 1
    if (-not $exp -or -not $got) { "FAIL $pt : expected-line=$([bool]$exp) received-line=$([bool]$got)"; $out | Select-Object -First 6; $ok = $false; continue }
    $ex = [int]$exp.Matches[0].Groups[1].Value; $ey = [int]$exp.Matches[0].Groups[2].Value
    $gx = [int]$got.Matches[0].Groups[1].Value; $gy = [int]$got.Matches[0].Groups[2].Value
    $d = [Math]::Max([Math]::Abs($ex - $gx), [Math]::Abs($ey - $gy))
    $status = if ($d -le $Tolerance) { "PASS" } else { $ok = $false; "FAIL" }
    $all = Get-Content $log -Raw
    if ($Extra -match "selftest-right" -and $all -notmatch "button Right") { $status = "FAIL"; $ok = $false; "  right click not received" }
    if ($Extra -match "selftest-wheel" -and $all -notmatch "wheel (\d+)") { $status = "FAIL"; $ok = $false; "  wheel not received" }
    "$status $pt : expected $ex,$ey received $gx,$gy (off by $d px)"
}
if ($ok) { "ALL PASS" } else { "SOME FAILED" }

