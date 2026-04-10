using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.Core.Services;

[SupportedOSPlatform("windows")]
public class RelayServer : IProxyModule, IDisposable
{
    public string Name => "RelayServer";
    public event Action<RelayEvent>? OnRelayEvent;

    private Logger? _logger;
    private IModuleContext? _context;
    private HttpListener? _listener;
    private CancellationTokenSource _cts = new();
    private RelayPortManager? _portManager;
    private ProxyRuleManager? _ruleManager;
    private CurlImpersonateClient? _curlClient;
    private PotProviderService? _potProvider;
    private readonly HttpClient _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    public Task InitializeAsync(IModuleContext context)
    {
        _context = context;
        _logger = context.Logger;
        _logger.Trace("Initializing RelayServer...");

        try
        {
            _portManager = context.GetModule<RelayPortManager>();
            _ruleManager = context.GetModule<ProxyRuleManager>();
            _curlClient = context.GetModule<CurlImpersonateClient>();
            _potProvider = context.GetModule<PotProviderService>();

            int attempts = 0;
            bool success = false;

            while (attempts < 5 && !success)
            {
                int port = _portManager.CurrentPort;

                if (port == 0)
                {
                    _logger.Error("RelayServer failed: Port is 0. Is RelayPortManager registered?");
                    return Task.CompletedTask;
                }

                _listener = new HttpListener();
                string prefix = "http://127.0.0.1:" + port + "/";
                _listener.Prefixes.Add(prefix);

                try
                {
                    _listener.Start();
                    success = true;
                    _logger.Success("Relay listening on port " + port);
                }
                catch (HttpListenerException)
                {
                    attempts++;
                    _logger.Warning("Port " + port + " conflict detected. Retrying... (" + attempts + "/5)");
                    _listener.Close();

                    if (attempts < 5)
                    {
                        _portManager.RefreshPort();
                    }
                }
            }

            if (!success)
            {
                _logger.Error("FATAL: Unable to bind to any local port after 5 attempts. Please check your firewall or restart your PC.");
                return Task.CompletedTask;
            }

            _ = Task.Run(ListenLoop);
        }
        catch (Exception ex)
        {
            _logger.Error("RelayServer Init Error: " + ex.Message, ex);
        }

        return Task.CompletedTask;
    }

    private async Task ListenLoop()
    {
        if (_listener == null) return;

        while (_listener.IsListening && !_cts.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (HttpListenerException) { /* Ignored on closing */ }
            catch (Exception ex)
            {
                _logger?.Error("Relay Listen Loop Error: " + ex.Message, ex);
            }
        }
    }

    // Rewrite HLS manifest lines so all segment and init-segment URLs route back through the relay.
    // This ensures that all content (including HLS segments for live streams) is served through
    // the relay, maintaining the localhost.youtube.com bypass for all requests the player makes.
    private string RewriteHlsManifest(string manifest, string baseUrl, int relayPort)
    {
        Uri baseUri;
        try { baseUri = new Uri(baseUrl); }
        catch { return manifest; }

        var sb = new StringBuilder();
        string[] lines = manifest.Split('\n');

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');

            if (line.Length > 0 && !line.StartsWith("#"))
            {
                // Segment URL (relative or absolute) — rewrite through relay
                string absUri = MakeAbsoluteUrl(line.Trim(), baseUri);
                string encoded = WebUtility.UrlEncode(Convert.ToBase64String(Encoding.UTF8.GetBytes(absUri)));
                line = "http://localhost.youtube.com:" + relayPort + "/play?target=" + encoded;
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                // Rewrite URI="..." attribute in MAP (init segment) and KEY (encryption key) tags.
                // Use IndexOf to locate URI= regardless of attribute order, since the HLS spec allows
                // attributes in any order (e.g. #EXT-X-KEY:METHOD=AES-128,URI="..." or
                // #EXT-X-MAP:BYTERANGE="800@0",URI="...").
                line = RewriteHlsTagUri(line, baseUri, relayPort);
            }

            sb.Append(line);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    // Finds URI="..." anywhere in an HLS tag line and rewrites the URI through the relay.
    private string RewriteHlsTagUri(string line, Uri baseUri, int relayPort)
    {
        int uriAttrPos = line.IndexOf("URI=\"", StringComparison.OrdinalIgnoreCase);
        if (uriAttrPos < 0) return line;

        int valueStart = uriAttrPos + 5; // skip URI="
        int valueEnd = line.IndexOf('"', valueStart);
        if (valueEnd <= valueStart) return line;

        string uri = line.Substring(valueStart, valueEnd - valueStart);
        string absUri = MakeAbsoluteUrl(uri, baseUri);
        string encoded = WebUtility.UrlEncode(Convert.ToBase64String(Encoding.UTF8.GetBytes(absUri)));
        string relayed = "http://localhost.youtube.com:" + relayPort + "/play?target=" + encoded;

        return line.Substring(0, valueStart) + relayed + line.Substring(valueEnd);
    }

    private string MakeAbsoluteUrl(string url, Uri baseUri)
    {
        if (url.StartsWith("http://") || url.StartsWith("https://")) return url;
        try { return new Uri(baseUri, url).ToString(); }
        catch { return url; }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (!context.Request.Url!.AbsolutePath.StartsWith("/play", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            string? targetBase64 = context.Request.QueryString["target"];
            if (string.IsNullOrEmpty(targetBase64))
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            // Re-replace spaces back with pluses for base64 safety (URL encoding converts + to space)
            targetBase64 = targetBase64.Replace(" ", "+");
            string targetUrl = Encoding.UTF8.GetString(Convert.FromBase64String(targetBase64));

            _logger?.Trace("Relaying request: " + context.Request.HttpMethod + " -> " + targetUrl);

            var relayEvent = new RelayEvent {
                TargetUrl = targetUrl,
                Method = context.Request.HttpMethod,
                StatusCode = 0,
                BytesTransferred = 0
            };
            OnRelayEvent?.Invoke(relayEvent);

            // Detect HLS content early from the URL — refined later from Content-Type header.
            // HLS manifests must be rewritten so all segment URLs route through the relay,
            // and ContentLength must NOT be forwarded (live manifests change size between polls).
            bool isHls = targetUrl.Contains(".m3u8") || targetUrl.Contains("m3u8");

            var uri = new Uri(targetUrl);
            ProxyRule rule = _ruleManager?.GetRuleForDomain(uri.Host) ?? new ProxyRule();

            if (rule.UsePoTokenProvider && _potProvider != null)
            {
                string? videoId = System.Web.HttpUtility.ParseQueryString(uri.Query).Get("id") ?? "unknown";
                string? visitorData = "dummy-visitor-data";
                string? token = await _potProvider.GetPotTokenAsync(visitorData, videoId);
                if (!string.IsNullOrEmpty(token))
                {
                    targetUrl += (targetUrl.Contains("?") ? "&" : "?") + "pot=" + token;
                }
            }

            using var outboundRequest = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), targetUrl);

            foreach (string? key in context.Request.Headers.AllKeys)
            {
                if (key != null && rule.ForwardHeaders.Contains(key))
                {
                    outboundRequest.Headers.TryAddWithoutValidation(key, context.Request.Headers[key]);
                }
            }

            if (!string.IsNullOrEmpty(rule.OverrideUserAgent))
            {
                outboundRequest.Headers.UserAgent.ParseAdd(rule.OverrideUserAgent);
            }
            else
            {
                outboundRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            }

            if (rule.ForwardReferer == "always")
            {
                if (context.Request.Headers["Referer"] != null)
                    outboundRequest.Headers.Referrer = new Uri(context.Request.Headers["Referer"]!);
            }
            else if (rule.ForwardReferer == "never")
            {
                outboundRequest.Headers.Remove("Referer");
            }
            else if (rule.ForwardReferer == "same-origin" && context.Request.Headers["Referer"] != null)
            {
                var refUri = new Uri(context.Request.Headers["Referer"]!);
                if (refUri.Host.EndsWith(uri.Host) || uri.Host.EndsWith(refUri.Host))
                    outboundRequest.Headers.Referrer = refUri;
            }

            Stream? sourceStream = null;
            HttpResponseMessage? response = null;

            if (rule.UseCurlImpersonate && _curlClient != null && _curlClient.IsAvailable)
            {
                var dict = new Dictionary<string, string>();
                foreach (var h in outboundRequest.Headers)
                {
                    dict[h.Key] = string.Join(", ", h.Value);
                }
                sourceStream = await _curlClient.SendRequestAsync(context.Request.HttpMethod, targetUrl, dict);

                // Parse HTTP headers from curl's -i output (headers then blank line then body)
                var headerLines = new List<string>();
                var currentLine = new StringBuilder();
                int emptyLineCount = 0;

                while (true)
                {
                    byte[] b = new byte[1];
                    int r = await sourceStream.ReadAsync(b, 0, 1, _cts.Token);
                    if (r == 0) break;

                    if (b[0] == '\r') continue;

                    if (b[0] == '\n')
                    {
                        if (currentLine.Length == 0)
                        {
                            emptyLineCount++;
                            if (emptyLineCount >= 1) break;
                        }
                        else
                        {
                            headerLines.Add(currentLine.ToString());
                            currentLine.Clear();
                            emptyLineCount = 0;
                        }
                    }
                    else
                    {
                        currentLine.Append((char)b[0]);
                        emptyLineCount = 0;
                    }
                }

                if (headerLines.Count > 0 && headerLines[0].StartsWith("HTTP/"))
                {
                    var parts = headerLines[0].Split(' ', 3);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int sc))
                    {
                        context.Response.StatusCode = sc;
                    }
                    headerLines.RemoveAt(0);
                }

                foreach (var headerLine in headerLines)
                {
                    int idx = headerLine.IndexOf(':');
                    if (idx > 0)
                    {
                        string key = headerLine.Substring(0, idx).Trim();
                        string value = headerLine.Substring(idx + 1).Trim();

                        if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;

                        // Detect HLS from Content-Type in curl response headers
                        if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            if (value.Contains("mpegurl") || value.Contains("m3u8")) isHls = true;
                        }

                        if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        {
                            // Skip Content-Length for HLS — will be set to rewritten manifest size
                            if (!isHls && long.TryParse(value, out long len)) context.Response.ContentLength64 = len;
                            continue;
                        }

                        try { context.Response.Headers.Add(key, value); } catch { }
                    }
                }

                // For HLS via curl: read sourceStream as text, rewrite, serve rewritten bytes
                if (isHls)
                {
                    using var reader = new StreamReader(sourceStream, Encoding.UTF8);
                    string rawManifest = await reader.ReadToEndAsync();
                    string rewritten = RewriteHlsManifest(rawManifest, targetUrl, _portManager!.CurrentPort);
                    byte[] manifestBytes = Encoding.UTF8.GetBytes(rewritten);
                    context.Response.ContentLength64 = manifestBytes.Length;
                    try { context.Response.Headers["Content-Type"] = "application/vnd.apple.mpegurl"; } catch { }
                    await context.Response.OutputStream.WriteAsync(manifestBytes, 0, manifestBytes.Length, _cts.Token);
                    relayEvent.StatusCode = context.Response.StatusCode;
                    relayEvent.BytesTransferred = manifestBytes.Length;
                    OnRelayEvent?.Invoke(relayEvent);
                    return;
                }

                // Non-HLS curl path: binary stream copy
            }
            else
            {
                response = await _httpClient.SendAsync(outboundRequest, HttpCompletionOption.ResponseHeadersRead, _cts.Token);

                context.Response.StatusCode = (int)response.StatusCode;

                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    string key = header.Key;
                    string value = string.Join(", ", header.Value);

                    if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;

                    // Detect HLS from Content-Type in response headers
                    if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.Contains("mpegurl") || value.Contains("m3u8")) isHls = true;
                    }

                    if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        // Skip Content-Length for HLS — will be set to rewritten manifest size
                        if (!isHls && long.TryParse(value, out long len)) context.Response.ContentLength64 = len;
                        continue;
                    }

                    try { context.Response.Headers.Add(key, value); } catch { }
                }

                // For HLS responses: read manifest as text, rewrite all segment URLs through relay,
                // then serve the rewritten content with correct Content-Length.
                if (isHls)
                {
                    string rawManifest = await response.Content.ReadAsStringAsync();
                    string rewritten = RewriteHlsManifest(rawManifest, targetUrl, _portManager!.CurrentPort);
                    byte[] manifestBytes = Encoding.UTF8.GetBytes(rewritten);
                    context.Response.ContentLength64 = manifestBytes.Length;
                    try { context.Response.Headers["Content-Type"] = "application/vnd.apple.mpegurl"; } catch { }
                    await context.Response.OutputStream.WriteAsync(manifestBytes, 0, manifestBytes.Length, _cts.Token);
                    relayEvent.StatusCode = context.Response.StatusCode;
                    relayEvent.BytesTransferred = manifestBytes.Length;
                    OnRelayEvent?.Invoke(relayEvent);
                    return;
                }

                sourceStream = await response.Content.ReadAsStreamAsync();
            }

            // Non-HLS binary streaming path: fire one final event with status + bytes once done.
            try
            {
                byte[] buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await sourceStream!.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
                {
                    await context.Response.OutputStream.WriteAsync(buffer, 0, bytesRead, _cts.Token);
                    relayEvent.BytesTransferred += bytesRead;
                }
            }
            catch (HttpListenerException) { /* Player closed connection (e.g., to seek) — normal */ }
            catch (IOException) { /* Socket closed abruptly */ }
            catch (TaskCanceledException) { /* System shutting down */ }
            catch (OperationCanceledException) { /* System shutting down */ }

            relayEvent.StatusCode = context.Response.StatusCode;
            OnRelayEvent?.Invoke(relayEvent);
        }
        catch (Exception ex)
        {
            _logger?.Error("Relay Request Handling Error: " + ex.Message, ex);
            try { context.Response.StatusCode = 500; } catch { }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    public void Shutdown()
    {
        _cts.Cancel();
        if (_listener != null && _listener.IsListening)
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        Shutdown();
        _cts.Dispose();
    }
}
