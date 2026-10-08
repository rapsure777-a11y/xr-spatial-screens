# Starts the Comet browser in a way that keeps its pages rendering while another window covers it.
#
# Why: Chromium-based browsers (Comet, Edge, Chrome) stop or throttle drawing in a window that Windows says is fully covered ("native window occlusion"), and throw away the
# detail they had drawn. A streamed browser window is often covered on the PC (by the Steam streaming window, a terminal, ...), so in the headset it can turn blocky and stay
# that way until the page is refreshed. These switches turn that behaviour off. They only take effect when Comet is started with them, so close every Comet window first.
#
# usage: powershell -ExecutionPolicy Bypass -File start-comet-streamable.ps1 [-Url https://suno.com]
param([string]$Url = "")
$exe = "C:\Program Files\Perplexity\Comet\Application\comet.exe"
if (-not (Test-Path $exe)) { throw "Comet not found at $exe" }
if (Get-Process comet -ErrorAction SilentlyContinue) {
    Write-Warning "Comet is already running, so these switches would be ignored. Close all Comet windows (check the tray) and run this again."
    return
}
$flags = "--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-background-timer-throttling"
Start-Process $exe -ArgumentList ($flags + $(if ($Url) { " $Url" } else { "" }))
"Comet started with: $flags"
