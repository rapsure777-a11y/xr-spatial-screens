# Publishes the tray host app AND the capture helper it launches into Builds\HostApp (the desktop shortcut points there).
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'Builds\HostApp'
foreach ($p in 'XrssHost', 'XrssCapture') {
    dotnet publish (Join-Path $PSScriptRoot $p) -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish $p failed" }
}
if (-not (Test-Path (Join-Path $out 'XrssCapture.exe'))) { throw 'XrssCapture.exe missing from the output' }
Write-Host "Published to $out"
