# Builds the capture helper. Default: Release build next to the sources (used by the editor and tests).
# -Publish: self-contained single-file exe in Tools\XrssCapture\publish (no .NET runtime needed on the target PC); the player build copies it next to the exe.
param([switch]$Publish)
$proj = Join-Path $PSScriptRoot "XrssCapture\XrssCapture.csproj"
if ($Publish) {
    dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o (Join-Path $PSScriptRoot "XrssCapture\publish")
} else {
    dotnet build $proj -c Release
}
