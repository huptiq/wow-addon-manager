$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet test tests/HupAddonManager.Tests -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Tests failed - not publishing." }

if (Test-Path dist) { Remove-Item dist -Recurse -Force }
dotnet publish src/HupAddonManager.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o dist --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Copy-Item README.md dist/README.md
$exe = Get-Item dist/HupAddonManager.exe
"Done: $($exe.FullName) ($([math]::Round($exe.Length / 1MB, 1)) MB)"
