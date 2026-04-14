using System;
using System.Net;
using System.Text;
using M3U8Parser;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

/// <summary>
/// Rewrites all URI-bearing tags in an HLS manifest so that every request
/// the player makes flows back through the local relay server.
///
/// Master playlists:
///   EXT-X-STREAM-INF      — variant stream playlists
///   EXT-X-MEDIA           — external audio / subtitle / CC rendition playlists
///   EXT-X-I-FRAME-STREAM-INF — trick-play I-frame playlists
///
/// Media playlists:
///   EXTINF segments       — individual media segments
///   EXT-X-MAP             — init segment (fMP4 / CMAF)
///   EXT-X-KEY             — AES-128 / SAMPLE-AES encryption key
///
/// Relative and protocol-relative URIs are resolved against the manifest's
/// base URL before relay-encoding, so they remain valid regardless of where
/// the relay serves them.
///
/// Known library limitation (M3U8Parser 2.0.0): EXT-X-DISCONTINUITY is parsed
/// but dropped by the library's ToString(). Segments on both sides of the boundary
/// are still rewritten correctly; most players handle the missing marker gracefully.
/// </summary>
public static class HlsManifestRewriter
{
    /// <summary>
    /// Rewrites all URIs in <paramref name="manifest"/> through the relay at
    /// <paramref name="relayPort"/>. Returns the original manifest unchanged if
    /// <paramref name="baseUrl"/> is invalid or M3U8Parser cannot parse the input.
    /// </summary>
    public static string Rewrite(string manifest, string baseUrl, int relayPort, Logger? logger = null)
    {
        if (string.IsNullOrEmpty(manifest)) return manifest;

        Uri baseUri;
        try { baseUri = new Uri(baseUrl); }
        catch { return manifest; }

        try
        {
            if (manifest.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                // ── Master playlist ──────────────────────────────────────────────
                var master = MasterPlaylist.LoadFromText(manifest);

                // EXT-X-STREAM-INF: variant stream playlists
                for (int i = 0; i < master.Streams.Count; i++)
                    if (!string.IsNullOrEmpty(master.Streams[i].Uri))
                        master.Streams[i].Uri = BuildRelayUrl(master.Streams[i].Uri, baseUri, relayPort);

                // EXT-X-MEDIA: audio / subtitle / CC rendition playlists
                for (int i = 0; i < master.Medias.Count; i++)
                    if (!string.IsNullOrEmpty(master.Medias[i].Uri))
                        master.Medias[i].Uri = BuildRelayUrl(master.Medias[i].Uri, baseUri, relayPort);

                // EXT-X-I-FRAME-STREAM-INF: trick-play playlists
                for (int i = 0; i < master.IFrameStreams.Count; i++)
                    if (!string.IsNullOrEmpty(master.IFrameStreams[i].Uri))
                        master.IFrameStreams[i].Uri = BuildRelayUrl(master.IFrameStreams[i].Uri, baseUri, relayPort);

                return master.ToString();
            }
            else
            {
                // ── Media playlist ───────────────────────────────────────────────
                var media = MediaPlaylist.LoadFromText(manifest);

                // EXT-X-MAP: init segment (fMP4 / CMAF)
                if (media.Map != null && !string.IsNullOrEmpty(media.Map.Uri))
                    media.Map.Uri = BuildRelayUrl(media.Map.Uri, baseUri, relayPort);

                foreach (var seg in media.MediaSegments)
                {
                    // EXT-X-KEY: AES-128 / SAMPLE-AES encryption key
                    if (seg.Key != null && !string.IsNullOrEmpty(seg.Key.Uri))
                        seg.Key.Uri = BuildRelayUrl(seg.Key.Uri, baseUri, relayPort);

                    // EXTINF segments
                    foreach (var s in seg.Segments)
                        if (!string.IsNullOrEmpty(s.Uri))
                            s.Uri = BuildRelayUrl(s.Uri, baseUri, relayPort);
                }

                return media.ToString();
            }
        }
        catch (Exception ex)
        {
            logger?.Warning("[HLS] Manifest parse error — returning original unchanged: " + ex.Message);
            return manifest;
        }
    }

    /// <summary>
    /// Encodes <paramref name="url"/> as a relay URL routed through
    /// <c>localhost.youtube.com</c> at <paramref name="port"/>.
    /// Resolves relative and protocol-relative URLs against <paramref name="baseUri"/> first.
    /// </summary>
    public static string BuildRelayUrl(string url, Uri baseUri, int port)
    {
        string abs = MakeAbsolute(url, baseUri);
        string encoded = WebUtility.UrlEncode(Convert.ToBase64String(Encoding.UTF8.GetBytes(abs)));
        return "http://localhost.youtube.com:" + port + "/play?target=" + encoded;
    }

    /// <summary>
    /// Resolves <paramref name="url"/> against <paramref name="baseUri"/> if it is
    /// relative or protocol-relative. Absolute http/https URLs are returned unchanged.
    /// </summary>
    public static string MakeAbsolute(string url, Uri baseUri)
    {
        if (url.StartsWith("http://", StringComparison.Ordinal) ||
            url.StartsWith("https://", StringComparison.Ordinal)) return url;
        // Handles relative paths ("seg.ts", "../other/seg.ts") and
        // protocol-relative URLs ("//cdn.example.com/seg.ts") — Uri resolves both.
        try { return new Uri(baseUri, url).ToString(); }
        catch { return url; }
    }
}
