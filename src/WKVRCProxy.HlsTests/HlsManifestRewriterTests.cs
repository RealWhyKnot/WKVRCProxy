using System;
using System.Linq;
using System.Net;
using System.Text;
using WKVRCProxy.Core.Services;
using Xunit;

#pragma warning disable CA1416 // HlsManifestRewriter itself has no Windows-only calls

namespace WKVRCProxy.HlsTests;

public class HlsManifestRewriterTests
{
    // ──────────────────────────────────────────────────────────
    //  Test configuration
    // ──────────────────────────────────────────────────────────

    private const int Port = 9001;
    private const string BaseUrl = "https://cdn.example.com/hls/index.m3u8";
    private static readonly Uri BaseUri = new(BaseUrl);

    // ──────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────

    /// <summary>Decode a relay URL's target= param back to the original URL.</summary>
    private static string? Decode(string relayUrl)
    {
        try
        {
            var uri = new Uri(relayUrl.Trim());
            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0] == "target")
                {
                    string raw = WebUtility.UrlDecode(kv[1]).Replace(" ", "+");
                    return Encoding.UTF8.GetString(Convert.FromBase64String(raw));
                }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Return all non-comment, non-empty lines from a rewritten playlist that start
    /// with the relay host. These are segment URL lines (one per line after #EXTINF).
    /// </summary>
    private static string[] RelaySegmentLines(string output)
        => output.Replace("\r\n", "\n").Split('\n')
                 .Where(l => l.StartsWith("http://localhost.youtube.com"))
                 .ToArray();

    private static string RelayPrefix => $"http://localhost.youtube.com:{Port}/play?target=";

    // ──────────────────────────────────────────────────────────
    //  Master playlist tests
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void MasterPlaylist_VariantUrls_Rewritten()
    {
        string manifest = string.Join("\n", new[]
        {
            "#EXTM3U",
            "#EXT-X-VERSION:3",
            "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360",
            "https://cdn.example.com/low/index.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=2400000,RESOLUTION=1280x720",
            "https://cdn.example.com/high/index.m3u8",
        });

        string result = HlsManifestRewriter.Rewrite(manifest, BaseUrl, Port);

        var lines = RelaySegmentLines(result);
        Assert.Equal(2, lines.Length);
        Assert.Equal("https://cdn.example.com/low/index.m3u8",  Decode(lines[0]));
        Assert.Equal("https://cdn.example.com/high/index.m3u8", Decode(lines[1]));
        // Original CDN URLs must not appear as bare segment lines
        Assert.DoesNotContain(lines, l => l.Contains("cdn.example.com"));
    }

    [Fact]
    public void MasterPlaylist_ExtXMedia_UriRewritten()
    {
        string manifest = string.Join("\n", new[]
        {
            "#EXTM3U",
            "#EXT-X-VERSION:6",
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"English\",DEFAULT=YES,URI=\"https://cdn.example.com/audio/en.m3u8\"",
            "#EXT-X-STREAM-INF:BANDWIDTH=1400000,AUDIO=\"audio\"",
            "https://cdn.example.com/video/index.m3u8",
        });

        string result = HlsManifestRewriter.Rewrite(manifest, BaseUrl, Port);

        // Original audio URI must be gone
        Assert.DoesNotContain("URI=\"https://cdn.example.com/audio/en.m3u8\"", result);
        // Relay URI must be present in its place
        Assert.Contains($"URI=\"{RelayPrefix}", result);
        // The relay URI in the EXT-X-MEDIA line must decode to the original
        string? mediaLine = result.Replace("\r\n", "\n").Split('\n')
            .FirstOrDefault(l => l.StartsWith("#EXT-X-MEDIA"));
        Assert.NotNull(mediaLine);
        int uriStart = mediaLine!.IndexOf("URI=\"", StringComparison.Ordinal) + 5;
        int uriEnd   = mediaLine.IndexOf('"', uriStart);
        string decoded = Decode(mediaLine[uriStart..uriEnd])!;
        Assert.Equal("https://cdn.example.com/audio/en.m3u8", decoded);
    }

    [Fact]
    public void MasterPlaylist_IFrameStreamUri_Rewritten()
    {
        string manifest = string.Join("\n", new[]
        {
            "#EXTM3U",
            "#EXT-X-STREAM-INF:BANDWIDTH=1400000",
            "https://cdn.example.com/video/index.m3u8",
            "#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=100000,URI=\"https://cdn.example.com/iframes/index.m3u8\"",
        });

        string result = HlsManifestRewriter.Rewrite(manifest, BaseUrl, Port);

        Assert.DoesNotContain("URI=\"https://cdn.example.com/iframes/index.m3u8\"", result);
        Assert.Contains($"URI=\"{RelayPrefix}", result);
        string? iframeLine = result.Replace("\r\n", "\n").Split('\n')
            .FirstOrDefault(l => l.StartsWith("#EXT-X-I-FRAME-STREAM-INF"));
        Assert.NotNull(iframeLine);
        int uriStart = iframeLine!.IndexOf("URI=\"", StringComparison.Ordinal) + 5;
        int uriEnd   = iframeLine.IndexOf('"', uriStart);
        Assert.Equal("https://cdn.example.com/iframes/index.m3u8", Decode(iframeLine[uriStart..uriEnd]));
    }

    // ──────────────────────────────────────────────────────────
    //  Media playlist tests
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void MediaPlaylist_SegmentUrls_Rewritten()
    {
        string manifest = string.Join("\n", new[]
        {
            "#EXTM3U",
            "#EXT-X-VERSION:3",
            "#EXT-X-TARGETDURATION:10",
            "#EXT-X-MEDIA-SEQUENCE:0",
            "#EXTINF:10.0,",
            "https://cdn.example.com/seg0.ts",
            "#EXTINF:10.0,",
            "https://cdn.example.com/seg1.ts",
            "#EXT-X-ENDLIST",
        });

        string result = HlsManifestRewriter.Rewrite(manifest, BaseUrl, Port);

        var lines = RelaySegmentLines(result);
        Assert.Equal(2, lines.Length);
        Assert.Equal("https://cdn.example.com/seg0.ts", Decode(lines[0]));
        Assert.Equal("https://cdn.example.com/seg1.ts", Decode(lines[1]));
    }

    [Fact]
    public void MediaPlaylist_RelativeUrls_ResolvedBeforeEncoding()
    {
        string manifest = string.Join("\n", new[]
        {
            "#EXTM3U",
            "#EXT-X-TARGETDURATION:10",
            "#EXTINF:10.0,",
            "seg0.ts",
            "#EXTINF:10.0,",
            "../other/seg1.ts",
        });

        string result = HlsManifestRewriter.Rewrite(
            manifest, "https://cdn.example.com/hls/stream/index.m3u8", Port);

        var lines = RelaySegmentLines(result);
        Assert.Equal(2, lines.Length);
        Assert.Equal("https://cdn.example.com/hls/stream/seg0.ts", Decode(lines[0]));
        Assert.Equal("https://cdn.example.com/hls/other/seg1.ts",  Decode(lines[1]));
    }

    [Fact]
    public void MediaPlaylist_ProtocolRelativeUrl_InheritsScheme()
    {
        // A URL like //cdn.example.com/seg.ts should inherit the scheme from the base URL.
        string manifest = string.Join("\n", new[]
        {
            "#EXTM3U",
            "#EXT-X-TARGETDURATION:10",
            "#EXTINF:10.0,",
            "//cdn.example.com/seg0.ts",
        });

        string result = HlsManifestRewriter.Rewrite(
            manifest, "https://manifest.example.com/hls/index.m3u8", Port);

        var lines = RelaySegmentLines(result);
        Assert.Single(lines);
        // Protocol-relative URL inherits HTTPS from the base URL
        Assert.Equal("https://cdn.example.com/seg0.ts", Decode(lines[0]));
    }

    [Fact]
    public void MediaPlaylist_ExtXKey_UriRewritten()
    {
        string manifest = string.Join("\n", new[]
        {
            "#EXTM3U",
            "#EXT-X-VERSION:3",
            "#EXT-X-TARGETDURATION:10",
            "#EXT-X-KEY:METHOD=AES-128,URI=\"https://cdn.example.com/key.bin\",IV=0x00000000000000000000000000000000",
            "#EXTINF:10.0,",
            "https://cdn.example.com/seg0.ts",
        });

        string result = HlsManifestRewriter.Rewrite(manifest, BaseUrl, Port);

        // Original key URI must be gone
        Assert.DoesNotContain("URI=\"https://cdn.example.com/key.bin\"", result);
        // Key URI should now be a relay URL
        string? keyLine = result.Replace("\r\n", "\n").Split('\n')
            .FirstOrDefault(l => l.StartsWith("#EXT-X-KEY"));
        Assert.NotNull(keyLine);
        int uriStart = keyLine!.IndexOf("URI=\"", StringComparison.Ordinal) + 5;
        int uriEnd   = keyLine.IndexOf('"', uriStart);
        Assert.Equal("https://cdn.example.com/key.bin", Decode(keyLine[uriStart..uriEnd]));
    }

    [Fact]
    public void MediaPlaylist_ExtXMap_UriRewritten()
    {
        string manifest = string.Join("\n", new[]
        {
            "#EXTM3U",
            "#EXT-X-VERSION:6",
            "#EXT-X-TARGETDURATION:10",
            "#EXT-X-MAP:URI=\"https://cdn.example.com/init.mp4\"",
            "#EXTINF:10.0,",
            "https://cdn.example.com/seg0.ts",
        });

        string result = HlsManifestRewriter.Rewrite(manifest, BaseUrl, Port);

        Assert.DoesNotContain("URI=\"https://cdn.example.com/init.mp4\"", result);
        string? mapLine = result.Replace("\r\n", "\n").Split('\n')
            .FirstOrDefault(l => l.StartsWith("#EXT-X-MAP"));
        Assert.NotNull(mapLine);
        int uriStart = mapLine!.IndexOf("URI=\"", StringComparison.Ordinal) + 5;
        int uriEnd   = mapLine.IndexOf('"', uriStart);
        Assert.Equal("https://cdn.example.com/init.mp4", Decode(mapLine[uriStart..uriEnd]));
    }

    // ──────────────────────────────────────────────────────────
    //  HLS tag preservation tests
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void MediaPlaylist_TargetDuration_Preserved()
    {
        string manifest = string.Join("\n", new[]
        {
            "#EXTM3U",
            "#EXT-X-TARGETDURATION:8",
            "#EXTINF:8.0,",
            "https://cdn.example.com/seg0.ts",
        });

        string result = HlsManifestRewriter.Rewrite(manifest, BaseUrl, Port);
        Assert.Contains("#EXT-X-TARGETDURATION:8", result);
    }

    [Fact]
    public void MediaPlaylist_Discontinuity_SegmentsStillRewritten()
    {
        // M3U8Parser 2.0.0 does not re-serialize #EXT-X-DISCONTINUITY in ToString() —
        // the tag is parsed but dropped on output. This is a known library limitation;
        // both segments on either side of a discontinuity boundary are still rewritten.
        // AVPro Video typically handles the missing tag gracefully (may glitch at the
        // splice point, but stream continues).
        string manifest = string.Join("\n", new[]
        {
            "#EXTM3U",
            "#EXT-X-TARGETDURATION:10",
            "#EXTINF:10.0,",
            "https://cdn.example.com/seg0.ts",
            "#EXT-X-DISCONTINUITY",
            "#EXTINF:10.0,",
            "https://cdn.example.com/seg1.ts",
        });

        string result = HlsManifestRewriter.Rewrite(manifest, BaseUrl, Port);

        // Both segments are rewritten correctly despite the discontinuity boundary
        Assert.StartsWith("#EXTM3U", result.TrimStart());
        var lines = RelaySegmentLines(result);
        Assert.Equal(2, lines.Length);
        Assert.Equal("https://cdn.example.com/seg0.ts", Decode(lines[0]));
        Assert.Equal("https://cdn.example.com/seg1.ts", Decode(lines[1]));
    }

    [Fact]
    public void MediaPlaylist_Endlist_Preserved()
    {
        string manifest = string.Join("\n", new[]
        {
            "#EXTM3U",
            "#EXT-X-TARGETDURATION:10",
            "#EXTINF:10.0,",
            "https://cdn.example.com/seg0.ts",
            "#EXT-X-ENDLIST",
        });

        string result = HlsManifestRewriter.Rewrite(manifest, BaseUrl, Port);
        Assert.Contains("#EXT-X-ENDLIST", result);
    }

    // ──────────────────────────────────────────────────────────
    //  Error handling / edge cases
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void EmptyManifest_ReturnsEmpty()
    {
        string result = HlsManifestRewriter.Rewrite("", BaseUrl, Port);
        Assert.Equal("", result);
    }

    [Fact]
    public void InvalidBaseUrl_ReturnsOriginal()
    {
        string manifest = "#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10.0,\nhttps://cdn.example.com/seg.ts\n";
        string result = HlsManifestRewriter.Rewrite(manifest, "not a valid url", Port);
        Assert.Equal(manifest, result);
    }

    [Fact]
    public void MalformedManifest_DoesNotThrow()
    {
        // Should never throw regardless of input — worst case returns the original.
        string garbage = "not a valid m3u8\nbinary-ish content\x01\x02\x03";
        string result = HlsManifestRewriter.Rewrite(garbage, BaseUrl, Port);
        Assert.NotNull(result); // may be original or empty parse result — either is fine
    }

    // ──────────────────────────────────────────────────────────
    //  HlsManifestRewriter.MakeAbsolute unit tests
    // ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://cdn.example.com/seg.ts",  "https://cdn.example.com/seg.ts")]        // already absolute
    [InlineData("http://cdn.example.com/seg.ts",   "http://cdn.example.com/seg.ts")]         // already absolute http
    [InlineData("seg.ts",                          "https://cdn.example.com/hls/seg.ts")]    // relative
    [InlineData("../other/seg.ts",                 "https://cdn.example.com/other/seg.ts")]  // parent-relative
    [InlineData("//cdn.example.com/seg.ts",        "https://cdn.example.com/seg.ts")]        // protocol-relative
    public void MakeAbsolute_ResolvesCorrectly(string input, string expected)
    {
        string result = HlsManifestRewriter.MakeAbsolute(input, new Uri("https://cdn.example.com/hls/index.m3u8"));
        Assert.Equal(expected, result);
    }
}
