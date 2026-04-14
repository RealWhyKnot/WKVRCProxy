# Extended multi-site test — run from repo root
$ErrorActionPreference = "Stop"
$TestExe = Join-Path $PSScriptRoot "dist\WKVRCProxy.TestHarness.exe"
$ProjPath = Join-Path $PSScriptRoot "src\WKVRCProxy.TestHarness\WKVRCProxy.TestHarness.csproj"
$BuildDir = Join-Path $PSScriptRoot "dist"

Write-Host "Building test harness..." -ForegroundColor Cyan
dotnet publish $ProjPath -c Release -r win-x64 --self-contained true -o $BuildDir /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -warnaserror --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed"; exit 1 }

$Urls = @(
    # YouTube
    "https://www.youtube.com/watch?v=jNQXAC9IVRw",      # VOD (always available)
    "https://www.youtube.com/shorts/9bZkp7q19f0",        # Shorts (Gangnam Style)
    "https://www.youtube.com/@nasa/live",                 # Channel live
    # Live streaming platforms (may not be live)
    "https://www.twitch.tv/twitchgaming",
    "https://kick.com/kick",
    # Video platforms
    "https://vimeo.com/148751763",                        # Vimeo: Tears of Steel (CC)
    "https://www.dailymotion.com/video/x7tgd4o",         # Dailymotion public video
    "https://rumble.com/v4kcpf5-big-buck-bunny-trailer.html",
    "https://odysee.com/@Odysee:8/what-is-odysee:a",    # Odysee/LBRY
    "https://streamable.com/e/moo",                      # Streamable short clip
    # Audio
    "https://soundcloud.com/skrillex/make-it-bun-dem",  # SoundCloud
    # File hosting / Archives
    "https://archive.org/details/BigBuckBunny_124",
    # Social video
    "https://www.reddit.com/r/videos/comments/kws8mc/me_asking_my_son_what_he_wants_to_be_when_he/",
    # Direct HLS stream
    "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8"
)

Write-Host "Running extended test suite ($($Urls.Count) URLs)..." -ForegroundColor Cyan
& $TestExe --verbose --urls @Urls
exit $LASTEXITCODE
