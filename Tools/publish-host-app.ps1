# Publishes the tray host app AND the capture helper it launches into Builds\HostApp (the desktop shortcut points there).
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'Builds\HostApp'
foreach ($p in 'XrssHost', 'XrssCapture') {
    dotnet publish (Join-Path $PSScriptRoot $p) -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish $p failed" }
}
# the Launch on Headset button needs adb.exe (and its two DLLs) next to the app; copied from the Android platform-tools on this PC
$pt = Join-Path $env:USERPROFILE 'UnityAndroid\SDK\platform-tools'
foreach ($f in 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll') { if (Test-Path (Join-Path $pt $f)) { Copy-Item (Join-Path $pt $f) $out -Force } else { Write-Warning "$f not found in ${pt}: the Launch on Headset button will need adb on the PATH" } }
# the Install / Update on Headset button installs this package; it is the newest headset build (Unity: HeadsetBuild.Build)
$apk = Join-Path $root 'Builds\Headset\XrSpatialScreens.apk'
if (Test-Path $apk) { Copy-Item $apk (Join-Path $out 'XrSpatialScreens.apk') -Force } else { Write-Warning "No headset package at ${apk}: the Install / Update button will have nothing to install" }
if (-not (Test-Path (Join-Path $out 'XrssCapture.exe'))) { throw 'XrssCapture.exe missing from the output' }
Write-Host "Published to $out"
