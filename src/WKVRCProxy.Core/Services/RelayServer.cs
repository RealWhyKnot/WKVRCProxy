using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.Core.Services;

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
            int port = _portManager.CurrentPort;

            if (port == 0)
            {
                _logger.Error("RelayServer failed: Port is 0. Is RelayPortManager registered?");
                return Task.CompletedTask;
            }

            _listener = new HttpListener();
            string prefix = "http://127.0.0.1:" + port + "/";
            _listener.Prefixes.Add(prefix);
            _listener.Start();

            _logger.Success("Relay listening on port " + port);

            _ = Task.Run(ListenLoop);
        }
        catch (Exception ex)
        {
            _logger.Error("RelayServer Init Error: " + ex.Message);
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
                _logger?.Error("Relay Listen Loop Error: " + ex.Message);
            }
        }
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

            string targetUrl = Encoding.UTF8.GetString(Convert.FromBase64String(targetBase64));

            _logger?.Trace("Relaying request: " + context.Request.HttpMethod + " -> " + targetUrl);

            var relayEvent = new RelayEvent {
                TargetUrl = targetUrl,
                Method = context.Request.HttpMethod,
                StatusCode = 0,
                BytesTransferred = 0
            };
            OnRelayEvent?.Invoke(relayEvent);

            using var outboundRequest = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), targetUrl);

            var uri = new Uri(targetUrl);
            ProxyRule rule = _ruleManager?.GetRuleForDomain(uri.Host) ?? new ProxyRule();

            foreach (string key in context.Request.Headers.AllKeys)
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

            using var response = await _httpClient.SendAsync(outboundRequest, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            
            context.Response.StatusCode = (int)response.StatusCode;
            relayEvent.StatusCode = context.Response.StatusCode;
            OnRelayEvent?.Invoke(relayEvent);
            
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                string key = header.Key;
                string value = string.Join(", ", header.Value);
                
                if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                
                if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(value, out long len)) context.Response.ContentLength64 = len;
                    continue;
                }

                try { context.Response.Headers.Add(key, value); } catch { }
            }

            using var sourceStream = await response.Content.ReadAsStreamAsync();
            try
            {
                byte[] buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
                {
                    await context.Response.OutputStream.WriteAsync(buffer, 0, bytesRead, _cts.Token);
                    relayEvent.BytesTransferred += bytesRead;
                }
            }
            catch (HttpListenerException) { /* Player closed connection to seek */ }
            catch (IOException) { /* Socket closed abruptly */ }
            catch (TaskCanceledException) { /* System shutting down */ }

            OnRelayEvent?.Invoke(relayEvent);
        }
        catch (Exception ex)
        {
            _logger?.Error("Relay Request Handling Error: " + ex.Message);
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
