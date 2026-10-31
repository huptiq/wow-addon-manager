$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (Test-Path tests/HupAddonManager.Tests) {
    dotnet test tests/HupAddonManager.Tests -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed - not publishing." }
}

$dist = Join-Path $PSScriptRoot "dist"
$running = Get-Process HupAddonManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($dist, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    "Closing hup's AddOn Manager (running from dist)..."
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

if (Test-Path $dist) {
    for ($i = 1; $i -le 10; $i++) {
        try { Remove-Item $dist -Recurse -Force; break }
        catch {
            if ($i -eq 10) { throw "Can't clear the dist folder. Close anything that uses it (Explorer window, the app) and try again." }
            Start-Sleep -Milliseconds 500
        }
    }
}

dotnet publish src/HupAddonManager.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o dist --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

if (Test-Path README.md) { Copy-Item README.md dist/README.md }
$exe = Get-Item dist/HupAddonManager.exe
"Done: $($exe.FullName) ($([math]::Round($exe.Length / 1MB, 1)) MB)"
