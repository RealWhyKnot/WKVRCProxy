using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

public class PotProviderService : IProxyModule, IDisposable
{
    public string Name => "PotProviderService";
    
    private Logger? _logger;
    private Process? _providerProcess;
    private int _port = 0;
    private readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    
    // Cache: VideoId -> (Token, Expiry)
    private readonly ConcurrentDictionary<string, (string token, DateTime expires)> _tokenCache = new();

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;
        _logger.Trace("Initializing PO Token Provider...");

        try
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            _port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "bgutil-ytdlp-pot-provider.exe");

            if (File.Exists(exePath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "-p " + _port,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _providerProcess = Process.Start(psi);
                _logger.Trace("Started bgutil-ytdlp-pot-provider on port " + _port);
            }
            else
            {
                _logger.Warning("bgutil-ytdlp-pot-provider.exe not found. PO Token spoofing may fail.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to initialize PO Token provider: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    public async Task<string?> GetPotTokenAsync(string visitorData, string videoId)
    {
        if (string.IsNullOrEmpty(videoId)) return null;

        if (_tokenCache.TryGetValue(videoId, out var cached))
        {
            if (cached.expires > DateTime.Now) return cached.token;
            _tokenCache.TryRemove(videoId, out _);
        }

        if (_port == 0)
        {
            _logger?.Warning("PO Token provider not initialized (port=0). Token fetch skipped.");
            return null;
        }

        try
        {
            string url = "http://127.0.0.1:" + _port + "/get_pot";
            var payload = new
            {
                client = "web.gvs",
                visitorData = visitorData,
                dataSyncId = videoId
            };
            
            string json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            string responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            
            if (doc.RootElement.TryGetProperty("poToken", out var tokenObj))
            {
                string token = tokenObj.GetString() ?? "";
                _tokenCache[videoId] = (token, DateTime.Now.AddHours(4)); // Cache for 4 hours
                return token;
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning("Failed to fetch PO Token: " + ex.Message);
        }

        return null;
    }

    public void Shutdown()
    {
        if (_providerProcess != null && !_providerProcess.HasExited)
        {
            try { _providerProcess.Kill(true); }
            catch (Exception ex) { _logger?.Trace("Failed to kill bgutil-ytdlp-pot-provider: " + ex.Message); }
            try { _providerProcess.Dispose(); }
            catch (Exception ex) { _logger?.Trace("Failed to dispose provider process: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        Shutdown();
        _httpClient.Dispose();
    }
}
