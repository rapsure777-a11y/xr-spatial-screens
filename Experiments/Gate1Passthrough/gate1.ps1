# Gate 1 helper: connect to the Steam Frame over Wi-Fi adb, install the APK, launch it, and save the Gate1 log lines.
# usage: powershell -File gate1.ps1 [-Hostname frame] [-Action connect|install|launch|log|all]
param([string]$Hostname = "frame", [string]$Action = "all")
$adb = "C:\Users\fence\UnityAndroid\SDK\platform-tools\adb.exe"
$apk = Join-Path $PSScriptRoot "Builds\Gate1Passthrough.apk"
$pkg = "com.gamebreaklabs.gate1passthrough"
$out = Join-Path $PSScriptRoot "Logs\gate1-device.log"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
function Do-Connect { & $adb connect $Hostname; & $adb devices }
function Do-Install { & $adb install -r $apk }
function Do-Launch  { & $adb logcat -c; & $adb shell monkey -p $pkg -c android.intent.category.LAUNCHER 1 }
function Do-Log     { Start-Sleep 12; & $adb logcat -d | Select-String -Pattern "Gate1|Unity|OpenXR|openxr|blend|passthrough|XR_" | ForEach-Object { $_.Line } | Set-Content $out -Encoding utf8; "saved $out"; Select-String -Path $out -Pattern "\[Gate1\]" | Select-Object -First 30 | ForEach-Object { $_.Line } }
switch ($Action) {
  "connect" { Do-Connect } "install" { Do-Install } "launch" { Do-Launch } "log" { Do-Log }
  default   { Do-Connect; Do-Install; Do-Launch; Do-Log }
}
