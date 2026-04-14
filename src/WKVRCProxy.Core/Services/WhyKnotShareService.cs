using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

/// <summary>
/// Relays a resolved video stream to a friend via the WhyKnot.dev P2P relay.
/// Connects to the /mesh WebSocket endpoint, registers the stream with relay_init,
/// then pumps chunks (via byte-range HttpClient requests) back to the server
/// whenever it sends a relay_read message.
/// </summary>
[SupportedOSPlatform("windows")]
public class WhyKnotShareService : IDisposable
{
    private const string WkWsUrl = "wss://whyknot.dev/mesh";
    private const string WkOrigin = "https://whyknot.dev";

    private readonly Logger _logger;
    private readonly HttpClient _httpClient;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;

    public event Action<string>? OnPublicUrlReady;
    public event Action? OnStopped;
    public event Action<string>? OnError;

    public bool IsActive => _ws?.State == WebSocketState.Open;

    public WhyKnotShareService(Logger logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("WKVRCProxy/1.0");
    }

    public async Task StartAsync(string streamUrl)
    {
        Stop(); // Clean up any existing session first

        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();

        try
        {
            _logger.Info("[P2PShare] Connecting to " + WkWsUrl + "...");
            await _ws.ConnectAsync(new Uri(WkWsUrl), _cts.Token);
            _logger.Info("[P2PShare] Connected.");

            string filename = DeriveFilename(streamUrl);
            string mime = DeriveMime(filename);

            var initPayload = JsonSerializer.Serialize(new
            {
                action = "relay_init",
                filename,
                size = 0, // unknown size — streaming
                mime
            });
            await SendTextAsync(initPayload);
            _logger.Debug("[P2PShare] relay_init sent for: " + filename + " (" + mime + ")");

            _ = Task.Run(() => ReceiveLoopAsync(streamUrl), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error("[P2PShare] Connection failed: " + ex.Message);
            OnError?.Invoke("Connection failed: " + ex.Message);
        }
    }

    private async Task ReceiveLoopAsync(string streamUrl)
    {
        if (_ws == null || _cts == null) return;

        var buffer = new byte[65536];

        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, _cts.Token);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.Info("[P2PShare] Server closed the WebSocket connection.");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                    await HandleTextMessageAsync(Encoding.UTF8.GetString(ms.ToArray()), streamUrl);
            }
        }
        catch (OperationCanceledException) { /* Expected on Stop() */ }
        catch (Exception ex)
        {
            if (!(_cts?.IsCancellationRequested ?? true))
            {
                _logger.Warning("[P2PShare] Receive loop error: " + ex.Message);
                OnError?.Invoke("Stream error: " + ex.Message);
            }
        }

        OnStopped?.Invoke();
    }

    private async Task HandleTextMessageAsync(string text, string streamUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            string action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";

            switch (action)
            {
                case "relay_ready":
                {
                    string publicPath = doc.RootElement.TryGetProperty("public_url", out var pu) ? pu.GetString() ?? "" : "";
                    string fullUrl = WkOrigin + publicPath;
                    _logger.Info("[P2PShare] Relay ready. Public URL: " + fullUrl);
                    OnPublicUrlReady?.Invoke(fullUrl);
                    break;
                }

                case "relay_read":
                {
                    string reqId = doc.RootElement.TryGetProperty("req_id", out var ri) ? ri.GetString() ?? "" : "";
                    long start = doc.RootElement.TryGetProperty("start", out var s) ? s.GetInt64() : 0;
                    long end   = doc.RootElement.TryGetProperty("end",   out var e) ? e.GetInt64() : 0;
                    // Serve the chunk asynchronously so we don't block the receive loop
                    _ = Task.Run(() => ServeChunkAsync(streamUrl, reqId, start, end));
                    break;
                }

                case "relay_error":
                {
                    string msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "unknown" : "unknown";
                    _logger.Warning("[P2PShare] Relay error from server: " + msg);
                    OnError?.Invoke("Relay error: " + msg);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning("[P2PShare] Message parse error: " + ex.Message);
        }
    }

    private async Task ServeChunkAsync(string streamUrl, string reqId, long start, long end)
    {
        if (_ws == null || _cts == null || string.IsNullOrEmpty(reqId)) return;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            req.Headers.Range = new RangeHeaderValue(start, end);

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead,
                                                         _cts.Token);

            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 206)
            {
                _logger.Warning("[P2PShare] Chunk fetch HTTP " + (int)resp.StatusCode + " for range " + start + "-" + end);
                return;
            }

            byte[] chunk = await resp.Content.ReadAsByteArrayAsync(_cts.Token);

            // Binary response format: [36 bytes req_id (UTF-8, zero-padded)][chunk]
            byte[] reqIdBytes = new byte[36];
            byte[] reqIdEncoded = Encoding.UTF8.GetBytes(reqId);
            Buffer.BlockCopy(reqIdEncoded, 0, reqIdBytes, 0, Math.Min(reqIdEncoded.Length, 36));

            byte[] response = new byte[36 + chunk.Length];
            Buffer.BlockCopy(reqIdBytes, 0, response, 0, 36);
            Buffer.BlockCopy(chunk, 0, response, 36, chunk.Length);

            await _ws.SendAsync(response, WebSocketMessageType.Binary, true, _cts.Token);
            _logger.Debug("[P2PShare] Chunk " + start + "-" + end + " (" + chunk.Length + " bytes) → req " + reqId.Substring(0, Math.Min(8, reqId.Length)) + "...");
        }
        catch (OperationCanceledException) { /* Expected on Stop() */ }
        catch (Exception ex)
        {
            _logger.Warning("[P2PShare] Chunk error for range " + start + "-" + end + ": " + ex.Message);
        }
    }

    private async Task SendTextAsync(string text)
    {
        if (_ws == null || _cts == null) return;
        byte[] data = Encoding.UTF8.GetBytes(text);
        await _ws.SendAsync(data, WebSocketMessageType.Text, true, _cts.Token);
    }

    public void Stop()
    {
        if (_cts == null && _ws == null) return;

        _cts?.Cancel();

        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopped", CancellationToken.None)
                       .Wait(2000);
            }
            catch { /* Best-effort close */ }
            _ws.Dispose();
            _ws = null;
        }

        _cts?.Dispose();
        _cts = null;
        _logger.Debug("[P2PShare] Session stopped.");
    }

    private static string DeriveFilename(string url)
    {
        try
        {
            string path = new Uri(url).AbsolutePath;
            string name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(name) && name.Contains('.')) return name;
        }
        catch { }
        return "stream.mp4";
    }

    private static string DeriveMime(string filename)
    {
        return Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".mp4"  => "video/mp4",
            ".webm" => "video/webm",
            ".mkv"  => "video/x-matroska",
            ".ts"   => "video/MP2T",
            ".m3u8" => "application/vnd.apple.mpegurl",
            _       => "application/octet-stream"
        };
    }

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
    }
}
