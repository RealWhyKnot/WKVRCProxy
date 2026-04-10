$ErrorActionPreference = "Stop"

$BuildDir = Join-Path $PSScriptRoot "dist"
$VendorDir = Join-Path $PSScriptRoot "vendor"
$VersionFile = Join-Path $VendorDir "versions.json"
$LocalVersionState = Join-Path $VendorDir "local_build_state.json"

if (Test-Path $BuildDir) { 
    Write-Host "Cleaning dist folder..." -ForegroundColor Cyan
    
    # Terminate running instances to release file locks
    Get-Process "WKVRCProxy" -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process "redirector" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1

    try {
        Remove-Item -Path $BuildDir -Recurse -Force -ErrorAction Stop
    } catch {
        Write-Host "Warning: Failed to fully clean dist folder. Some files may be in use." -ForegroundColor Yellow
    }
}
New-Item -ItemType Directory $BuildDir -Force | Out-Null
if (!(Test-Path $VendorDir)) { New-Item -ItemType Directory $VendorDir }

# --- Dependency Tracking ---
$Versions = @{ "ytdlp" = ""; "deno" = ""; "curlimp" = ""; "bgutil" = "" }
if (Test-Path $VersionFile) {
    $Versions = Get-Content $VersionFile | ConvertFrom-Json
}

Write-Host "--- Checking Dependencies ---" -ForegroundColor Cyan

# 1. Fetch Latest yt-dlp
$YtDlpRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest"
$LatestYtDlpVersion = $YtDlpRelease.tag_name

if ($Versions.ytdlp -ne $LatestYtDlpVersion) {
    Write-Host "Updating yt-dlp to $LatestYtDlpVersion..." -ForegroundColor Yellow
    $DownloadUrl = ($YtDlpRelease.assets | Where-Object { $_.name -eq "yt-dlp.exe" }).browser_download_url
    Invoke-WebRequest -Uri $DownloadUrl -OutFile (Join-Path $VendorDir "yt-dlp.exe")
    $Versions.ytdlp = $LatestYtDlpVersion
}

# 2. Fetch Latest Deno
$DenoRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/denoland/deno/releases/latest"
$LatestDenoVersion = $DenoRelease.tag_name

if ($Versions.deno -ne $LatestDenoVersion) {
    Write-Host "Updating Deno to $LatestDenoVersion..." -ForegroundColor Yellow
    $DownloadUrl = ($DenoRelease.assets | Where-Object { $_.name -eq "deno-x86_64-pc-windows-msvc.zip" }).browser_download_url
    if (!$DownloadUrl) {
        $DownloadUrl = ($DenoRelease.assets | Where-Object { $_.name -match "x86_64-pc-windows-msvc\.zip" } | Select-Object -First 1).browser_download_url
    }
    $ZipPath = Join-Path $VendorDir "deno.zip"
    Invoke-WebRequest -Uri $DownloadUrl -OutFile $ZipPath
    Expand-Archive -Path $ZipPath -DestinationPath $VendorDir -Force
    Remove-Item $ZipPath
    $Versions.deno = $LatestDenoVersion
}
# 3. Fetch Latest curl-impersonate-win (RealWhyKnot/curl-impersonate-win)
# Go-based Windows CLI wrapper around bogdanfinn/tls-client for Chrome TLS fingerprint impersonation.
$CurlImpVendorPath = Join-Path $VendorDir "curl-impersonate-win.exe"
try {
    $CurlImpRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/RealWhyKnot/curl-impersonate-win/releases/latest" -ErrorAction Stop
    $LatestCurlImpVersion = $CurlImpRelease.tag_name
    if ($Versions.curlimp -ne $LatestCurlImpVersion -or !(Test-Path $CurlImpVendorPath)) {
        Write-Host "Updating curl-impersonate-win to $LatestCurlImpVersion..." -ForegroundColor Yellow
        $CurlImpAsset = ($CurlImpRelease.assets | Where-Object { $_.name -eq "curl-impersonate-win.exe" } | Select-Object -First 1)
        if ($CurlImpAsset) {
            Invoke-WebRequest -Uri $CurlImpAsset.browser_download_url -OutFile $CurlImpVendorPath
            $Versions.curlimp = $LatestCurlImpVersion
            Write-Host "curl-impersonate-win.exe ready ($LatestCurlImpVersion)." -ForegroundColor Green
        } else {
            Write-Host "Warning: curl-impersonate-win.exe asset not found in release. Relay will use standard HttpClient." -ForegroundColor Yellow
        }
    } else {
        Write-Host "curl-impersonate-win.exe is up-to-date ($LatestCurlImpVersion)." -ForegroundColor Green
    }
} catch {
    if (Test-Path $CurlImpVendorPath) {
        Write-Host "curl-impersonate-win.exe found in vendor/ (offline - could not check for updates)." -ForegroundColor Green
    } else {
        Write-Host "Note: Could not fetch curl-impersonate-win release. Relay will use standard HttpClient." -ForegroundColor Yellow
    }
}

# 4. Fetch and Compile bgutil-ytdlp-pot-provider implementation
try {
    $BgutilRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/Brainicism/bgutil-ytdlp-pot-provider/commits/main" -ErrorAction SilentlyContinue
} catch {
    $BgutilRelease = $null
}

if (!$BgutilRelease) {
    try {
        $BgutilRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/Brainicism/bgutil-ytdlp-pot-provider/commits/master" -ErrorAction Stop
    } catch {
        Write-Host "Warning: Failed to fetch bgutil commits. GitHub API might be rate limited." -ForegroundColor Yellow
        $BgutilRelease = $null
    }
}

if ($BgutilRelease) {
    $LatestBgutilCommit = $BgutilRelease.sha.Substring(0, 7)

    if ($Versions.bgutil -ne $LatestBgutilCommit) {
        Write-Host "Compiling bgutil-ytdlp-pot-provider server at commit $LatestBgutilCommit..." -ForegroundColor Yellow
        $BgutilDir = Join-Path $VendorDir "bgutil_repo"
        if (Test-Path $BgutilDir) { Remove-Item -Path $BgutilDir -Recurse -Force }
        
        git clone --depth 1 https://github.com/Brainicism/bgutil-ytdlp-pot-provider.git $BgutilDir
        
        Push-Location (Join-Path $BgutilDir "server")
        & (Join-Path $VendorDir "deno.exe") install
        & (Join-Path $VendorDir "deno.exe") compile -A --output (Join-Path $VendorDir "bgutil-ytdlp-pot-provider.exe") src/main.ts
        Pop-Location
        
        Remove-Item -Path $BgutilDir -Recurse -Force
        
        if ($null -eq $Versions.psobject.properties['bgutil']) {
            $Versions | Add-Member -NotePropertyName bgutil -NotePropertyValue $LatestBgutilCommit
        } else {
            $Versions.bgutil = $LatestBgutilCommit
        }
    }
}

$Versions | ConvertTo-Json | Out-File $VersionFile

# --- Daily Versioning Logic ---
$Today = Get-Date -Format "yyyy.M.d"
$BuildCount = 0
$UID = [Guid]::NewGuid().ToString().Substring(0, 4).ToUpper()

if (Test-Path $LocalVersionState) {
    $State = Get-Content $LocalVersionState | ConvertFrom-Json
    if ($State.Date -eq $Today) {
        $BuildCount = $State.Count + 1
    }
}

$FullVersion = "$Today.$BuildCount-$UID"
@{ "Date" = $Today; "Count" = $BuildCount } | ConvertTo-Json | Out-File $LocalVersionState

Write-Host "Building Version: $FullVersion" -ForegroundColor Magenta

# --- Inject Version into Store ---
$AppStorePath = "src/WKVRCProxy.UI/ui/src/stores/appStore.ts"
$StoreContent = Get-Content $AppStorePath -Raw
$RegexPattern = 'version = ref\(''(.+?)''\)'
$RegexReplace = 'version = ref(''{0}'')' -f $FullVersion
$NewStoreContent = $StoreContent -replace $RegexPattern, $RegexReplace
Set-Content $AppStorePath $NewStoreContent

# --- Build Frontend ---
Write-Host "`n--- Building Frontend ---" -ForegroundColor Cyan
Push-Location "src/WKVRCProxy.UI/ui"
npm run build
Pop-Location

# --- Build .NET Projects ---
Write-Host "`n--- Building .NET Projects ---" -ForegroundColor Cyan
# Production build: Release mode
dotnet publish src/WKVRCProxy.UI/WKVRCProxy.UI.csproj -c Release -o $BuildDir --self-contained true -r win-x64 /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -warnaserror
dotnet publish src/WKVRCProxy.Redirector/WKVRCProxy.Redirector.csproj -c Release -o $BuildDir --self-contained true -r win-x64 /p:PublishSingleFile=true -warnaserror

# --- Final Packaging ---
Write-Host "`n--- Packaging Assets ---" -ForegroundColor Cyan
$ToolsDir = Join-Path $BuildDir "tools"
if (!(Test-Path $ToolsDir)) { New-Item -ItemType Directory $ToolsDir }

# Renaming logic needs to account for the -windows TFM
$UiExeName = "WKVRCProxy.UI.exe"
$UiBuildPath = Join-Path $BuildDir $UiExeName

Move-Item $UiBuildPath (Join-Path $BuildDir "WKVRCProxy.exe") -Force
Move-Item (Join-Path $BuildDir "WKVRCProxy.Redirector.exe") (Join-Path $ToolsDir "redirector.exe") -Force

Copy-Item -Path "src/WKVRCProxy.UI/wwwroot" -Destination $BuildDir -Recurse -Force
Copy-Item (Join-Path $VendorDir "yt-dlp.exe") (Join-Path $ToolsDir "yt-dlp.exe")
if (Test-Path (Join-Path $VendorDir "curl-impersonate-win.exe")) {
    Copy-Item (Join-Path $VendorDir "curl-impersonate-win.exe") (Join-Path $ToolsDir "curl-impersonate-win.exe")
}
Copy-Item (Join-Path $VendorDir "bgutil-ytdlp-pot-provider.exe") (Join-Path $ToolsDir "bgutil-ytdlp-pot-provider.exe")

# Cleanup
Get-ChildItem -Path $BuildDir -Filter "*.pdb" -Recurse | Remove-Item -Force
Get-ChildItem -Path $BuildDir -Filter "*.log" | Remove-Item -Force

$FullVersion | Set-Content -Path (Join-Path $BuildDir "version.txt") -Encoding UTF8
$FullVersion | Set-Content -Path (Join-Path $PSScriptRoot "version.txt") -Encoding UTF8

Write-Host "`nBuild $FullVersion Complete! Output in: $BuildDir" -ForegroundColor Green
