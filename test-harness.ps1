# WKVRCProxy Test Harness — builds the test harness into dist/ and runs it.
# Must be run from the repository root.
#
# Usage:
#   .\test-harness.ps1                             # run default test suite
#   .\test-harness.ps1 --verbose                   # verbose log output
#   .\test-harness.ps1 --url https://...           # single URL
#   .\test-harness.ps1 --urls https://... https://... --player Unity
#   .\test-harness.ps1 --help                      # show harness help
#
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$BuildDir  = Join-Path $ScriptDir "dist"
$ProjPath  = Join-Path $ScriptDir "src\WKVRCProxy.TestHarness\WKVRCProxy.TestHarness.csproj"
$TestExe   = Join-Path $BuildDir "WKVRCProxy.TestHarness.exe"

if (!(Test-Path $BuildDir)) {
    Write-Error "dist/ not found at $BuildDir — run build.ps1 first to create the production distribution."
    exit 1
}

if (!(Test-Path (Join-Path $BuildDir "tools\yt-dlp.exe"))) {
    Write-Error "dist/tools/yt-dlp.exe not found — run build.ps1 first."
    exit 1
}

Write-Host "Building test harness..." -ForegroundColor Cyan

dotnet publish $ProjPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $BuildDir `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    -warnaserror `
    --nologo `
    -v quiet

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}

Write-Host "Build complete. Running test harness...`n" -ForegroundColor Cyan

& $TestExe @args
exit $LASTEXITCODE
